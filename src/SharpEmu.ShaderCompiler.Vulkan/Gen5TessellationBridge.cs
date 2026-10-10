// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// The guest HS has already computed its factors and control points in a merged
// compute workgroup. This stage feeds those factors to Vulkan's tessellator;
// the domain shader still reads the original guest control-point ring.
public static class Gen5TessellationBridge
{
    public static uint FactorCount(Gen5TessellationDomain domain) => domain switch
    {
        Gen5TessellationDomain.Isolines => 2,
        Gen5TessellationDomain.Triangles => 4,
        Gen5TessellationDomain.Quads => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(domain)),
    };

    public static Gen5SpirvShader CompileControl(BindingLayout layout, Gen5TessellationDomain domain)
    {
        if (!layout.UsesTessellationData)
            throw new ArgumentException("The tessellation control bridge requires runtime tessellation data.", nameof(layout));
        var factors = FactorCount(domain);
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        module.AddCapability(SpirvCapability.Tessellation);
        module.AddCapability(SpirvCapability.Int64);
        module.AddCapability(SpirvCapability.PhysicalStorageBufferAddresses);
        module.SetPhysicalStorageBuffer64MemoryModel();
        var uintType = module.TypeInt(32, false);
        var ulongType = module.TypeInt(64, false);
        var floatType = module.TypeFloat(32);
        var boolType = module.TypeBool();
        uint UInt(uint value) => module.Constant(uintType, value);
        uint Op(SpirvOp op, uint type, params uint[] values) => module.AddInstruction(op, type, values);

        var words = module.TypeRuntimeArray(uintType);
        module.AddDecoration(words, SpirvDecoration.ArrayStride, 4);
        var block = module.TypeStruct(words);
        module.AddDecoration(block, SpirvDecoration.Block);
        module.AddMemberDecoration(block, 0, SpirvDecoration.Offset, 0);
        var data = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.StorageBuffer, block), SpirvStorageClass.StorageBuffer);
        module.AddDecoration(data, SpirvDecoration.NonWritable);
        module.AddDecoration(data, SpirvDecoration.DescriptorSet, layout.UsesBindlessImages ? 1u : 0u);
        // Shares the domain stage's runtime data, not its guest SGPR inputs.
        module.AddDecoration(data, SpirvDecoration.Binding,
            BindingLayout.NativeBindingIndex(ShaderStage.TessellationEvaluation, DescriptorBindingKind.ShaderData));
        var patch = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, uintType), SpirvStorageClass.Input);
        module.AddDecoration(patch, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.PrimitiveId);
        var outputFloat = module.TypePointer(SpirvStorageClass.Output, floatType);
        uint Output(SpirvBuiltIn builtIn, uint count)
        {
            var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, module.TypeArray(floatType, count)), SpirvStorageClass.Output);
            module.AddDecoration(output, SpirvDecoration.BuiltIn, (uint)builtIn);
            module.AddDecoration(output, SpirvDecoration.Patch);
            return output;
        }
        var outer = Output(SpirvBuiltIn.TessLevelOuter, 4);
        var inner = Output(SpirvBuiltIn.TessLevelInner, 2);
        var voidType = module.TypeVoid();
        var entry = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        uint Word(uint index) => Op(SpirvOp.Load, uintType, Op(SpirvOp.AccessChain,
            module.TypePointer(SpirvStorageClass.StorageBuffer, uintType), data, UInt(0), UInt(layout.TessellationDataDword + index)));
        var addressLow = Op(SpirvOp.UConvert, ulongType, Word(Gen5TessellationData.FactorAddress));
        var addressHigh = Op(SpirvOp.ShiftLeftLogical, ulongType,
            Op(SpirvOp.UConvert, ulongType, Word(Gen5TessellationData.FactorAddress + 1)), UInt(32));
        var address = Op(SpirvOp.BitwiseOr, ulongType, addressLow, addressHigh);
        var first = Op(SpirvOp.IMul, uintType, Op(SpirvOp.Load, uintType, patch), UInt(factors * 4));
        var minimum = Op(SpirvOp.Bitcast, floatType, Word(Gen5TessellationData.MinimumLevel));
        var maximum = Op(SpirvOp.Bitcast, floatType, Word(Gen5TessellationData.MaximumLevel));
        var zero = module.ConstantFloat(floatType, 0);
        void Write(uint output, uint index, uint? sourceIndex)
        {
            var value = zero;
            if (sourceIndex is { } source)
            {
                var offset = Op(SpirvOp.IAdd, uintType, first, UInt(source * 4));
                var pointer = Op(SpirvOp.ConvertUToPtr, module.TypePointer(SpirvStorageClass.PhysicalStorageBuffer, floatType),
                    Op(SpirvOp.IAdd, ulongType, address, Op(SpirvOp.UConvert, ulongType, offset)));
                var level = Op(SpirvOp.Load, floatType, pointer, 2, 4);
                var lower = Op(SpirvOp.Select, floatType, Op(SpirvOp.FOrdLessThan, boolType, level, minimum), minimum, level);
                var clamped = Op(SpirvOp.Select, floatType, Op(SpirvOp.FOrdGreaterThan, boolType, lower, maximum), maximum, lower);
                // Nonpositive or NaN outer factors must still cull the patch.
                value = Op(SpirvOp.Select, floatType, Op(SpirvOp.FOrdGreaterThan, boolType, level, zero), clamped, level);
            }
            module.AddStatement(SpirvOp.Store, Op(SpirvOp.AccessChain, outputFloat, output, UInt(index)), value);
        }
        var outerCount = domain == Gen5TessellationDomain.Isolines ? 2u : domain == Gen5TessellationDomain.Triangles ? 3u : 4u;
        for (uint index = 0; index < 4; index++)
            Write(outer, index, index < outerCount ? domain == Gen5TessellationDomain.Isolines ? 1u - index : index : null);
        for (uint index = 0; index < 2; index++)
            Write(inner, index, index + outerCount < factors ? index + outerCount : null);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.TessellationControl, entry, "main", [data, patch, outer, inner]);
        module.AddExecutionMode(entry, SpirvExecutionMode.OutputVertices, 1);
        return new(module.Build(), 0);
    }
}

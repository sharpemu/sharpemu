// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvExtInstOperandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OperationLiteralDoesNotEscapeAnUnrelatedPrivatePointer(bool indexed, bool folded)
    {
        const uint umin = 38;
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var glsl = module.ImportExtInst("GLSL.std.450");
        var integer = module.TypeInt(32, false);
        var pointerType = module.TypePointer(SpirvStorageClass.Private, integer);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var zero = module.Constant(integer, 0);
        var first = module.Constant(integer, 7);
        var second = module.Constant(integer, 9);
        var stored = module.Constant(integer, 42);
        var array = indexed
            ? module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, module.TypeArray(integer, 2)),
                SpirvStorageClass.Private)
            : 0;
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", indexed ? [array, output] : [output]);
        module.AddLabel();
        ReserveIdsBefore(module, umin);
        var pointer = indexed
            ? module.AddInstruction(SpirvOp.AccessChain, pointerType, array, zero)
            : module.AddGlobalVariable(pointerType, SpirvStorageClass.Private);
        Assert.Equal(umin, pointer);
        module.AddStatement(SpirvOp.Store, pointer, stored);
        var minimum = module.AddInstruction(SpirvOp.ExtInst, integer, glsl, umin, first, folded ? first : second);
        if (folded) Assert.Equal(first, minimum);
        // The literal 38 names an operation, not this coincidentally numbered pointer.
        Assert.Equal(stored, module.AddInstruction(SpirvOp.Load, integer, pointer));
        module.AddStatement(SpirvOp.Store, output, minimum);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
    }

    [Theory]
    [InlineData(35u)]
    [InlineData(51u)]
    public void ActualExtendedInstructionOutputPointerStillEscapes(uint operation)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var glsl = module.ImportExtInst("GLSL.std.450");
        var floating = module.TypeFloat(32);
        var resultType = operation == 35 ? floating : module.TypeInt(32, true);
        var pointer = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, resultType),
            SpirvStorageClass.Private);
        var value = operation == 35 ? module.ConstantFloat(floating, 4) : module.Constant(resultType, 4);
        var input = module.ConstantFloat(floating, 1.5f);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, resultType), SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", [pointer, output]);
        module.AddLabel();
        module.AddStatement(SpirvOp.Store, pointer, value);
        module.AddInstruction(SpirvOp.ExtInst, floating, glsl, operation, input, pointer);
        var loaded = module.AddInstruction(SpirvOp.Load, resultType, pointer);
        Assert.NotEqual(value, loaded);
        module.AddStatement(SpirvOp.Store, output, loaded);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
    }

    private static void ReserveIdsBefore(SpirvModuleBuilder module, uint target)
    {
        uint reserved;
        do
        {
            reserved = module.AllocateId();
        }
        while (reserved < target - 1);
        Assert.Equal(target - 1, reserved);
    }
}

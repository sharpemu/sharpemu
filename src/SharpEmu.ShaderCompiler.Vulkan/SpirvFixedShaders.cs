// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static class SpirvFixedShaders
{
    public static byte[] CreateFullscreenVertex(uint attributeCount)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var boolType = module.TypeBool();
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputUintPointer = module.TypePointer(SpirvStorageClass.Input, uintType);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);

        var vertexIndex = module.AddGlobalVariable(inputUintPointer, SpirvStorageClass.Input);
        module.AddName(vertexIndex, "vertexIndex");
        module.AddDecoration(
            vertexIndex,
            SpirvDecoration.BuiltIn,
            (uint)SpirvBuiltIn.VertexIndex);

        var position = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(position, "position");
        module.AddDecoration(position, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.Position);

        var attributes = new uint[attributeCount];
        for (uint index = 0; index < attributeCount; index++)
        {
            attributes[index] =
                module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
            module.AddName(attributes[index], $"attr{index}");
            module.AddDecoration(attributes[index], SpirvDecoration.Location, index);
            module.AddDecoration(attributes[index], SpirvDecoration.NoPerspective);
        }

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var indexValue = module.AddInstruction(SpirvOp.Load, uintType, vertexIndex);
        var one = module.Constant(uintType, 1);
        var two = module.Constant(uintType, 2);
        var shifted = module.AddInstruction(SpirvOp.ShiftLeftLogical, uintType, indexValue, one);
        var xBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, shifted, two);
        var yBits = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, indexValue, two);
        var x = module.AddInstruction(SpirvOp.ConvertUToF, floatType, xBits);
        var y = module.AddInstruction(SpirvOp.ConvertUToF, floatType, yBits);
        var zero = module.ConstantFloat(floatType, 0f);
        var oneFloat = module.ConstantFloat(floatType, 1f);
        var twoFloat = module.ConstantFloat(floatType, 2f);
        var xPosition = module.AddInstruction(SpirvOp.FMul, floatType, x, twoFloat);
        xPosition = module.AddInstruction(SpirvOp.FSub, floatType, xPosition, oneFloat);
        var yPosition = module.AddInstruction(SpirvOp.FMul, floatType, y, twoFloat);
        yPosition = module.AddInstruction(SpirvOp.FSub, floatType, yPosition, oneFloat);
        var positionValue = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            xPosition,
            yPosition,
            zero,
            oneFloat);
        module.AddStatement(SpirvOp.Store, position, positionValue);

        var attributeValue = module.AddInstruction(
            SpirvOp.CompositeConstruct,
            vec4Type,
            x,
            y,
            zero,
            oneFloat);
        foreach (var attribute in attributes)
        {
            module.AddStatement(SpirvOp.Store, attribute, attributeValue);
        }

        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var interfaces = new uint[2 + attributes.Length];
        interfaces[0] = vertexIndex;
        interfaces[1] = position;
        attributes.CopyTo(interfaces, 2);
        module.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", interfaces);
        _ = boolType;
        return module.Build();
    }

    public static byte[] CreateCopyFragment()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec2Type = module.TypeVector(floatType, 2);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputVec4Pointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var imageType = module.TypeImage(
            floatType,
            SpirvImageDim.Dim2D,
            depth: false,
            arrayed: false,
            multisampled: false,
            sampled: 1,
            SpirvImageFormat.Unknown);
        var sampledImageType = module.TypeSampledImage(imageType);
        var sampledImagePointer =
            module.TypePointer(SpirvStorageClass.UniformConstant, sampledImageType);

        var attribute = module.AddGlobalVariable(inputVec4Pointer, SpirvStorageClass.Input);
        module.AddName(attribute, "attr0");
        module.AddDecoration(attribute, SpirvDecoration.Location, 0);

        var texture = module.AddGlobalVariable(
            sampledImagePointer,
            SpirvStorageClass.UniformConstant);
        module.AddName(texture, "tex0");
        module.AddDecoration(texture, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(texture, SpirvDecoration.Binding, 1);

        var output = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var attributeValue = module.AddInstruction(SpirvOp.Load, vec4Type, attribute);
        var coordinates = module.AddInstruction(
            SpirvOp.VectorShuffle,
            vec2Type,
            attributeValue,
            attributeValue,
            0,
            1);
        var sampledImage = module.AddInstruction(SpirvOp.Load, sampledImageType, texture);
        var lod = module.ConstantFloat(floatType, 0f);
        var color = module.AddInstruction(
            SpirvOp.ImageSampleExplicitLod,
            vec4Type,
            sampledImage,
            coordinates,
            2,
            lod);
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(
            SpirvExecutionModel.Fragment,
            main,
            "main",
            [attribute, texture, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    public static byte[] CreateSolidFragment(float red, float green, float blue, float alpha)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var outputVec4Pointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var output = module.AddGlobalVariable(outputVec4Pointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var color = module.ConstantComposite(
            vec4Type,
            module.ConstantFloat(floatType, red),
            module.ConstantFloat(floatType, green),
            module.ConstantFloat(floatType, blue),
            module.ConstantFloat(floatType, alpha));
        module.AddStatement(SpirvOp.Store, output, color);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Diagnostic fragment stage that exposes one interpolated vertex output
    /// directly as color. This keeps the real guest vertex/index/depth path
    /// intact while isolating fragment-shader translation from interface data.
    /// </summary>
    public static byte[] CreateAttributeFragment(uint location)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var inputPointer = module.TypePointer(SpirvStorageClass.Input, vec4Type);
        var outputPointer = module.TypePointer(SpirvStorageClass.Output, vec4Type);
        var input = module.AddGlobalVariable(inputPointer, SpirvStorageClass.Input);
        module.AddName(input, $"attr{location}");
        module.AddDecoration(input, SpirvDecoration.Location, location);
        var output = module.AddGlobalVariable(outputPointer, SpirvStorageClass.Output);
        module.AddName(output, "outColor");
        module.AddDecoration(output, SpirvDecoration.Location, 0);

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        var value = module.AddInstruction(SpirvOp.Load, vec4Type, input);
        module.AddStatement(SpirvOp.Store, output, value);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(
            SpirvExecutionModel.Fragment,
            main,
            "main",
            [input, output]);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Minimal fragment stage for fixed-function depth-only passes.  The
    /// guest has no pixel shader and therefore cannot export colour; keeping
    /// this stage output-free preserves that contract while allowing Vulkan
    /// to run early/late depth tests for the translated vertex shader.
    /// </summary>
    public static byte[] CreateDepthOnlyFragment()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", []);
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        return module.Build();
    }

    /// <summary>
    /// Compute kernel that deswizzles one RDNA2 exact-XOR tiled surface (swizzle
    /// modes 5/9/24/27) at 4 bytes/element into a linear output buffer — one GPU
    /// thread per texel. Mirrors <c>GnmTiling.GetDetileParams</c>' ExactXor
    /// addressing so it is bit-identical to the CPU fallback:
    /// <code>
    ///   src = (y / blockHeight * blocksPerRow + x / blockWidth) * blockElements
    ///         + (xTerm[x &amp; xMask] ^ yTerm[y &amp; yMask]);
    ///   out[y * width + x] = tiled[src];
    /// </code>
    /// The X/Y term tables are ELEMENT offsets (the caller pre-shifts the
    /// byte-unit GetDetileParams terms right by log2(bytesPerElement); for 4bpp
    /// that shift is exact because the equation's low two byte-offset bits are 0).
    /// The presenter copies the linear output buffer into the sampled image,
    /// reusing the existing buffer-&gt;image upload.
    ///
    /// Descriptor set 0: binding 0 = tiled uint[], 1 = xTerm uint[],
    /// 2 = yTerm uint[], 3 = out uint[]. Push constants (8 x uint, offset i*4):
    /// width, height, blockWidth, blockHeight, blockElements, blocksPerRow,
    /// xMask, yMask. Local size 8x8x1.
    /// </summary>
    public static byte[] CreateDetileCompute()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);

        var voidType = module.TypeVoid();
        var boolType = module.TypeBool();
        var uintType = module.TypeInt(32, signed: false);
        var uvec3Type = module.TypeVector(uintType, 3);

        // One shared Block-decorated storage-buffer struct: struct { uint data[]; }.
        var runtimeArray = module.TypeRuntimeArray(uintType);
        module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, 4);
        var bufferStruct = module.TypeStruct(runtimeArray);
        module.AddDecoration(bufferStruct, SpirvDecoration.Block);
        module.AddMemberDecoration(bufferStruct, 0, SpirvDecoration.Offset, 0);
        var bufferPtrType = module.TypePointer(SpirvStorageClass.StorageBuffer, bufferStruct);
        var uintStoragePtr = module.TypePointer(SpirvStorageClass.StorageBuffer, uintType);

        uint MakeBuffer(uint binding, string name)
        {
            var variable = module.AddGlobalVariable(bufferPtrType, SpirvStorageClass.StorageBuffer);
            module.AddName(variable, name);
            module.AddDecoration(variable, SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(variable, SpirvDecoration.Binding, binding);
            return variable;
        }

        var tiledVar = MakeBuffer(0, "tiled");
        var xTermVar = MakeBuffer(1, "xTerm");
        var yTermVar = MakeBuffer(2, "yTerm");
        var outVar = MakeBuffer(3, "outLinear");

        // Push constants: struct { uint p0..p7; }, each member at offset i*4.
        var pushStruct = module.TypeStruct(
            uintType, uintType, uintType, uintType, uintType, uintType, uintType, uintType);
        module.AddDecoration(pushStruct, SpirvDecoration.Block);
        for (uint member = 0; member < 8; member++)
        {
            module.AddMemberDecoration(pushStruct, member, SpirvDecoration.Offset, member * 4);
        }

        var pushPtrType = module.TypePointer(SpirvStorageClass.PushConstant, pushStruct);
        var pushMemberPtrType = module.TypePointer(SpirvStorageClass.PushConstant, uintType);
        var pushVar = module.AddGlobalVariable(pushPtrType, SpirvStorageClass.PushConstant);
        module.AddName(pushVar, "pc");

        var inputUvec3Ptr = module.TypePointer(SpirvStorageClass.Input, uvec3Type);
        var gidVar = module.AddGlobalVariable(inputUvec3Ptr, SpirvStorageClass.Input);
        module.AddName(gidVar, "gid");
        module.AddDecoration(gidVar, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.GlobalInvocationId);

        var uintConst = new uint[8];
        for (uint value = 0; value < 8; value++)
        {
            uintConst[value] = module.Constant(uintType, value);
        }

        var functionType = module.TypeFunction(voidType);
        var main = module.BeginFunction(voidType, functionType);
        module.AddName(main, "main");
        module.AddLabel();

        var gid = module.AddInstruction(SpirvOp.Load, uvec3Type, gidVar);
        var x = module.AddInstruction(SpirvOp.CompositeExtract, uintType, gid, 0);
        var y = module.AddInstruction(SpirvOp.CompositeExtract, uintType, gid, 1);

        uint PushField(uint index)
        {
            var pointer = module.AddInstruction(
                SpirvOp.AccessChain, pushMemberPtrType, pushVar, uintConst[index]);
            return module.AddInstruction(SpirvOp.Load, uintType, pointer);
        }

        var width = PushField(0);
        var height = PushField(1);
        var blockWidth = PushField(2);
        var blockHeight = PushField(3);
        var blockElements = PushField(4);
        var blocksPerRow = PushField(5);
        var xMask = PushField(6);
        var yMask = PushField(7);

        var xInRange = module.AddInstruction(SpirvOp.ULessThan, boolType, x, width);
        var yInRange = module.AddInstruction(SpirvOp.ULessThan, boolType, y, height);
        var inRange = module.AddInstruction(SpirvOp.LogicalAnd, boolType, xInRange, yInRange);

        var bodyLabel = module.AllocateId();
        var mergeLabel = module.AllocateId();
        module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
        module.AddStatement(SpirvOp.BranchConditional, inRange, bodyLabel, mergeLabel);

        module.AddLabel(bodyLabel);

        // blockIdx = (y / blockHeight) * blocksPerRow + (x / blockWidth)
        var yDiv = module.AddInstruction(SpirvOp.UDiv, uintType, y, blockHeight);
        var blockRow = module.AddInstruction(SpirvOp.IMul, uintType, yDiv, blocksPerRow);
        var xDiv = module.AddInstruction(SpirvOp.UDiv, uintType, x, blockWidth);
        var blockIdx = module.AddInstruction(SpirvOp.IAdd, uintType, blockRow, xDiv);

        // off = xTerm[x & xMask] ^ yTerm[y & yMask]  (element offsets)
        var xIdx = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, x, xMask);
        var xPtr = module.AddInstruction(SpirvOp.AccessChain, uintStoragePtr, xTermVar, uintConst[0], xIdx);
        var xTerm = module.AddInstruction(SpirvOp.Load, uintType, xPtr);
        var yIdx = module.AddInstruction(SpirvOp.BitwiseAnd, uintType, y, yMask);
        var yPtr = module.AddInstruction(SpirvOp.AccessChain, uintStoragePtr, yTermVar, uintConst[0], yIdx);
        var yTerm = module.AddInstruction(SpirvOp.Load, uintType, yPtr);
        var off = module.AddInstruction(SpirvOp.BitwiseXor, uintType, xTerm, yTerm);

        // src = blockIdx * blockElements + off
        var blockBase = module.AddInstruction(SpirvOp.IMul, uintType, blockIdx, blockElements);
        var src = module.AddInstruction(SpirvOp.IAdd, uintType, blockBase, off);
        var srcPtr = module.AddInstruction(SpirvOp.AccessChain, uintStoragePtr, tiledVar, uintConst[0], src);
        var texel = module.AddInstruction(SpirvOp.Load, uintType, srcPtr);

        // out[y * width + x] = texel
        var rowBase = module.AddInstruction(SpirvOp.IMul, uintType, y, width);
        var dstIdx = module.AddInstruction(SpirvOp.IAdd, uintType, rowBase, x);
        var dstPtr = module.AddInstruction(SpirvOp.AccessChain, uintStoragePtr, outVar, uintConst[0], dstIdx);
        module.AddStatement(SpirvOp.Store, dstPtr, texel);

        module.AddStatement(SpirvOp.Branch, mergeLabel);
        module.AddLabel(mergeLabel);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 8, 8, 1);
        module.AddEntryPoint(
            SpirvExecutionModel.GLCompute,
            main,
            "main",
            [gidVar, tiledVar, xTermVar, yTermVar, outVar, pushVar]);
        return module.Build();
    }
}

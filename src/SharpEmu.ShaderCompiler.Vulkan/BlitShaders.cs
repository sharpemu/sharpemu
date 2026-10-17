// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// Assembles the fullscreen-triangle vertex shader, the color-to-multisample-depth fragment
// shader, and the multisample depth readback the tests use to check it.
public static class BlitShaders
{
    public static byte[] CreateFullscreenTriangleVertex()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var intType = module.TypeInt(32, signed: true);
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);
        var vec2Type = module.TypeVector(floatType, 2);
        var vec4Type = module.TypeVector(floatType, 4);

        var vertexIndexVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, intType), SpirvStorageClass.Input);
        module.AddName(vertexIndexVar, "gl_VertexIndex");
        module.AddDecoration(vertexIndexVar, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.VertexIndex);
        var positionVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, vec4Type), SpirvStorageClass.Output);
        module.AddName(positionVar, "gl_Position");
        module.AddDecoration(positionVar, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.Position);
        var uvVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, vec2Type), SpirvStorageClass.Output);
        module.AddName(uvVar, "uv");
        module.AddDecoration(uvVar, SpirvDecoration.Location, 0);

        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddName(main, "main");
        module.AddLabel();
        var vertexIndex = module.AddInstruction(SpirvOp.Bitcast, uintType, module.AddInstruction(SpirvOp.Load, intType, vertexIndexVar));
        var xBits = module.AddInstruction(SpirvOp.ShiftLeftLogical, uintType, module.AddInstruction(SpirvOp.BitwiseAnd, uintType, vertexIndex, module.Constant(uintType, 1)), module.Constant(uintType, 2));
        var yBits = module.AddInstruction(SpirvOp.ShiftLeftLogical, uintType, module.AddInstruction(SpirvOp.BitwiseAnd, uintType, vertexIndex, module.Constant(uintType, 2)), module.Constant(uintType, 1));
        var x = module.AddInstruction(SpirvOp.ConvertUToF, floatType, xBits);
        var y = module.AddInstruction(SpirvOp.ConvertUToF, floatType, yBits);
        var position = module.AddInstruction(SpirvOp.CompositeConstruct, vec2Type, x, y);
        var one = module.ConstantFloat(floatType, 1.0f);
        var ones = module.ConstantComposite(vec2Type, one, one);
        var centered = module.AddInstruction(SpirvOp.FSub, vec2Type, position, ones);
        var centeredX = module.AddInstruction(SpirvOp.CompositeExtract, floatType, centered, 0);
        var centeredY = module.AddInstruction(SpirvOp.CompositeExtract, floatType, centered, 1);
        var clip = module.AddInstruction(SpirvOp.CompositeConstruct, vec4Type, centeredX, centeredY, module.ConstantFloat(floatType, 0.0f), one);
        module.AddStatement(SpirvOp.Store, positionVar, clip);
        var half = module.ConstantFloat(floatType, 0.5f);
        var halves = module.ConstantComposite(vec2Type, half, half);
        module.AddStatement(SpirvOp.Store, uvVar, module.AddInstruction(SpirvOp.FMul, vec2Type, position, halves));
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.Vertex, main, "main", [vertexIndexVar, positionVar, uvVar]);
        return module.Build();
    }

    // Each sample of the depth output takes one component of the source color texel.
    public static byte[] CreateColorToMultisampleDepthFragment()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        module.AddCapability(SpirvCapability.SampleRateShading);
        module.AddCapability(SpirvCapability.ImageQuery);
        var voidType = module.TypeVoid();
        var intType = module.TypeInt(32, signed: true);
        var floatType = module.TypeFloat(32);
        var vec2Type = module.TypeVector(floatType, 2);
        var vec4Type = module.TypeVector(floatType, 4);
        var ivec2Type = module.TypeVector(intType, 2);
        var imageType = module.TypeImage(floatType, SpirvImageDim.Dim2D, depth: false, arrayed: false, multisampled: false, sampled: 1, SpirvImageFormat.Unknown);

        var colorVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.UniformConstant, imageType), SpirvStorageClass.UniformConstant);
        module.AddName(colorVar, "color");
        module.AddDecoration(colorVar, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(colorVar, SpirvDecoration.Binding, 0);
        var uvVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, vec2Type), SpirvStorageClass.Input);
        module.AddName(uvVar, "uv");
        module.AddDecoration(uvVar, SpirvDecoration.Location, 0);
        var sampleIdVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, intType), SpirvStorageClass.Input);
        module.AddName(sampleIdVar, "gl_SampleID");
        module.AddDecoration(sampleIdVar, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.SampleId);
        module.AddDecoration(sampleIdVar, SpirvDecoration.Flat);
        var fragDepthVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, floatType), SpirvStorageClass.Output);
        module.AddName(fragDepthVar, "gl_FragDepth");
        module.AddDecoration(fragDepthVar, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.FragDepth);

        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddName(main, "main");
        module.AddLabel();
        var uv = module.AddInstruction(SpirvOp.Load, vec2Type, uvVar);
        var image = module.AddInstruction(SpirvOp.Load, imageType, colorVar);
        var size = module.AddInstruction(SpirvOp.ImageQuerySizeLod, ivec2Type, image, module.Constant(intType, 0));
        var sizeF = module.AddInstruction(SpirvOp.ConvertSToF, vec2Type, size);
        var scaled = module.AddInstruction(SpirvOp.FMul, vec2Type, uv, sizeF);
        var coord = module.AddInstruction(SpirvOp.ConvertFToS, ivec2Type, scaled);
        var texel = module.AddInstruction(SpirvOp.ImageFetch, vec4Type, image, coord, 0x2, module.Constant(intType, 0));
        var sampleId = module.AddInstruction(SpirvOp.Load, intType, sampleIdVar);
        var depth = module.AddInstruction(SpirvOp.VectorExtractDynamic, floatType, texel, sampleId);
        module.AddStatement(SpirvOp.Store, fragDepthVar, depth);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddExecutionMode(main, SpirvExecutionMode.OriginUpperLeft);
        module.AddExecutionMode(main, SpirvExecutionMode.DepthReplacing);
        module.AddEntryPoint(SpirvExecutionModel.Fragment, main, "main", [colorVar, uvVar, sampleIdVar, fragDepthVar]);
        return module.Build();
    }

    // Test helper: writes the depth of each sample of texel (0, 0) as raw float bits.
    public static byte[] CreateMultisampleDepthSampleReadback()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var boolType = module.TypeBool();
        var intType = module.TypeInt(32, signed: true);
        var uintType = module.TypeInt(32, signed: false);
        var floatType = module.TypeFloat(32);
        var vec4Type = module.TypeVector(floatType, 4);
        var uvec3Type = module.TypeVector(uintType, 3);
        var ivec2Type = module.TypeVector(intType, 2);
        var imageType = module.TypeImage(floatType, SpirvImageDim.Dim2D, depth: false, arrayed: false, multisampled: true, sampled: 1, SpirvImageFormat.Unknown);

        var depthVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.UniformConstant, imageType), SpirvStorageClass.UniformConstant);
        module.AddName(depthVar, "input_depth");
        module.AddDecoration(depthVar, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(depthVar, SpirvDecoration.Binding, 0);
        var runtimeArray = module.TypeRuntimeArray(uintType);
        module.AddDecoration(runtimeArray, SpirvDecoration.ArrayStride, 4);
        var bufferStruct = module.TypeStruct(runtimeArray);
        module.AddDecoration(bufferStruct, SpirvDecoration.Block);
        module.AddMemberDecoration(bufferStruct, 0, SpirvDecoration.Offset, 0);
        var outputVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.StorageBuffer, bufferStruct), SpirvStorageClass.StorageBuffer);
        module.AddName(outputVar, "output_data");
        module.AddDecoration(outputVar, SpirvDecoration.DescriptorSet, 0);
        module.AddDecoration(outputVar, SpirvDecoration.Binding, 1);
        var pushStruct = module.TypeStruct(uintType);
        module.AddDecoration(pushStruct, SpirvDecoration.Block);
        module.AddMemberDecoration(pushStruct, 0, SpirvDecoration.Offset, 0);
        var pushVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.PushConstant, pushStruct), SpirvStorageClass.PushConstant);
        module.AddName(pushVar, "push");
        var invocationVar = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, uvec3Type), SpirvStorageClass.Input);
        module.AddDecoration(invocationVar, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.LocalInvocationId);

        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddName(main, "main");
        module.AddLabel();
        var invocation = module.AddInstruction(SpirvOp.Load, uvec3Type, invocationVar);
        var sampleId = module.AddInstruction(SpirvOp.CompositeExtract, uintType, invocation, 0);
        var samplesPointer = module.AddInstruction(SpirvOp.AccessChain, module.TypePointer(SpirvStorageClass.PushConstant, uintType), pushVar, module.Constant(uintType, 0));
        var samples = module.AddInstruction(SpirvOp.Load, uintType, samplesPointer);
        var inRange = module.AddInstruction(SpirvOp.ULessThan, boolType, sampleId, samples);
        var bodyLabel = module.AllocateId();
        var mergeLabel = module.AllocateId();
        module.AddStatement(SpirvOp.SelectionMerge, mergeLabel, 0);
        module.AddStatement(SpirvOp.BranchConditional, inRange, bodyLabel, mergeLabel);
        module.AddLabel(bodyLabel);
        var image = module.AddInstruction(SpirvOp.Load, imageType, depthVar);
        var zero = module.Constant(intType, 0);
        var origin = module.ConstantComposite(ivec2Type, zero, zero);
        var sampleIndex = module.AddInstruction(SpirvOp.Bitcast, intType, sampleId);
        var texel = module.AddInstruction(SpirvOp.ImageFetch, vec4Type, image, origin, 0x40, sampleIndex);
        var depth = module.AddInstruction(SpirvOp.CompositeExtract, floatType, texel, 0);
        var bits = module.AddInstruction(SpirvOp.Bitcast, uintType, depth);
        var target = module.AddInstruction(SpirvOp.AccessChain, module.TypePointer(SpirvStorageClass.StorageBuffer, uintType), outputVar, module.Constant(uintType, 0), sampleId);
        module.AddStatement(SpirvOp.Store, target, bits);
        module.AddStatement(SpirvOp.Branch, mergeLabel);
        module.AddLabel(mergeLabel);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 4, 1, 1);
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [invocationVar, depthVar, outputVar, pushVar]);
        return module.Build();
    }
}

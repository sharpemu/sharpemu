// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static class SpirvOcclusionSum
{
    public const uint PushWords = 7;

    public static byte[] Create()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var word = module.TypeInt(32, false);
        var boolean = module.TypeBool();
        uint Constant(uint value) => module.Constant(word, value);
        uint Binary(SpirvOp op, uint a, uint b) => module.AddInstruction(op, word, a, b);
        uint Less(uint a, uint b) => module.AddInstruction(SpirvOp.ULessThan, boolean, a, b);
        uint Select(uint condition, uint a, uint b) => module.AddInstruction(SpirvOp.Select, word, condition, a, b);
        var array = module.TypeRuntimeArray(word);
        module.AddDecoration(array, SpirvDecoration.ArrayStride, 4);
        var structure = module.TypeStruct(array);
        module.AddDecoration(structure, SpirvDecoration.Block);
        module.AddMemberDecoration(structure, 0, SpirvDecoration.Offset, 0);
        var pointer = module.TypePointer(SpirvStorageClass.StorageBuffer, structure);
        var wordPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, word);
        var buffers = new uint[2];
        for (uint index = 0; index < 2; index++)
        {
            buffers[index] = module.AddGlobalVariable(pointer, SpirvStorageClass.StorageBuffer);
            module.AddDecoration(buffers[index], SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(buffers[index], SpirvDecoration.Binding, index);
        }
        var pushArray = module.TypeArray(word, PushWords);
        module.AddDecoration(pushArray, SpirvDecoration.ArrayStride, 4);
        var pushStructure = module.TypeStruct(pushArray);
        module.AddDecoration(pushStructure, SpirvDecoration.Block);
        module.AddMemberDecoration(pushStructure, 0, SpirvDecoration.Offset, 0);
        var push = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.PushConstant, pushStructure), SpirvStorageClass.PushConstant);
        var pushPointer = module.TypePointer(SpirvStorageClass.PushConstant, word);
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        var entry = module.AddLabel();
        uint LoadPush(uint index) => module.AddInstruction(SpirvOp.Load, word,
            module.AddInstruction(SpirvOp.AccessChain, pushPointer, push, Constant(0), Constant(index)));
        uint Address(int buffer, uint index) => module.AddInstruction(SpirvOp.AccessChain, wordPointer, buffers[buffer], Constant(0), index);
        uint Read(int buffer, uint index) => module.AddInstruction(SpirvOp.Load, word, Address(buffer, index));
        var first = LoadPush(0);
        var count = LoadPush(1);
        var output = LoadPush(2);
        var accumulate = module.AddInstruction(SpirvOp.INotEqual, boolean, LoadPush(3), Constant(0));
        var initialLow = Select(accumulate, Read(1, output), Constant(0));
        var initialHigh = Select(accumulate, Read(1, Binary(SpirvOp.IAdd, output, Constant(1))), Constant(0));
        var header = module.AllocateId();
        var body = module.AllocateId();
        var continuation = module.AllocateId();
        var merge = module.AllocateId();
        var nextIndex = module.AllocateId();
        var nextLow = module.AllocateId();
        var nextHigh = module.AllocateId();
        module.AddStatement(SpirvOp.Branch, header);
        module.AddLabel(header);
        var indexValue = module.AddInstruction(SpirvOp.Phi, word, Constant(0), entry, nextIndex, continuation);
        var low = module.AddInstruction(SpirvOp.Phi, word, initialLow, entry, nextLow, continuation);
        var high = module.AddInstruction(SpirvOp.Phi, word, initialHigh, entry, nextHigh, continuation);
        var active = Less(indexValue, count);
        module.AddStatement(SpirvOp.LoopMerge, merge, continuation, 0);
        module.AddStatement(SpirvOp.BranchConditional, active, body, merge);
        module.AddLabel(body);
        var input = Binary(SpirvOp.IAdd, first, Binary(SpirvOp.IMul, indexValue, Constant(2)));
        var sumLow = Binary(SpirvOp.IAdd, low, Read(0, input));
        var sumHigh = Binary(SpirvOp.IAdd, Binary(SpirvOp.IAdd, high,
            Read(0, Binary(SpirvOp.IAdd, input, Constant(1)))), Select(Less(sumLow, low), Constant(1), Constant(0)));
        module.AddStatement(SpirvOp.Branch, continuation);
        module.AddLabel(continuation);
        module.AddStatement(SpirvOp.IAdd, word, nextIndex, indexValue, Constant(1));
        module.AddStatement(SpirvOp.IAdd, word, nextLow, sumLow, Constant(0));
        module.AddStatement(SpirvOp.IAdd, word, nextHigh, sumHigh, Constant(0));
        module.AddStatement(SpirvOp.Branch, header);
        module.AddLabel(merge);
        var baseLow = LoadPush(4);
        var resultLow = Binary(SpirvOp.ISub, low, baseLow);
        var resultHigh = Binary(SpirvOp.ISub, Binary(SpirvOp.ISub, high, LoadPush(5)),
            Select(Less(low, baseLow), Constant(1), Constant(0)));
        resultHigh = Binary(SpirvOp.BitwiseOr, resultHigh, LoadPush(6));
        module.AddStatement(SpirvOp.Store, Address(1, output), resultLow);
        module.AddStatement(SpirvOp.Store, Address(1, Binary(SpirvOp.IAdd, output, Constant(1))), resultHigh);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [.. buffers, push]);
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 1, 1, 1);
        return module.Build();
    }
}

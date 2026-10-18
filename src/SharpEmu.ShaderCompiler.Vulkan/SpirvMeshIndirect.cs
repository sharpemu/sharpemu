// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static class SpirvMeshIndirect
{
    public const uint HeaderWords = 16;
    public const uint PushWordCount = 11;

    public static byte[] Create()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var uintType = module.TypeInt(32, false);
        var boolType = module.TypeBool();
        var vectorType = module.TypeVector(uintType, 3);
        uint Constant(uint value) => module.Constant(uintType, value);
        uint Operation(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, uintType, left, right);
        uint Compare(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, boolType, left, right);
        uint And(uint left, uint right) => Compare(SpirvOp.LogicalAnd, left, right);
        uint Select(uint condition, uint yes, uint no) => module.AddInstruction(SpirvOp.Select, uintType, condition, yes, no);
        var array = module.TypeRuntimeArray(uintType);
        module.AddDecoration(array, SpirvDecoration.ArrayStride, 4);
        var structure = module.TypeStruct(array);
        module.AddDecoration(structure, SpirvDecoration.Block);
        module.AddMemberDecoration(structure, 0, SpirvDecoration.Offset, 0);
        var bufferPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, structure);
        var wordPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, uintType);
        var buffers = new uint[4];
        for (uint index = 0; index < buffers.Length; index++)
        {
            buffers[index] = module.AddGlobalVariable(bufferPointer, SpirvStorageClass.StorageBuffer);
            module.AddDecoration(buffers[index], SpirvDecoration.DescriptorSet, 0);
            module.AddDecoration(buffers[index], SpirvDecoration.Binding, index);
        }
        var pushArray = module.TypeArray(uintType, PushWordCount);
        module.AddDecoration(pushArray, SpirvDecoration.ArrayStride, 4);
        var pushStructure = module.TypeStruct(pushArray);
        module.AddDecoration(pushStructure, SpirvDecoration.Block);
        module.AddMemberDecoration(pushStructure, 0, SpirvDecoration.Offset, 0);
        var push = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.PushConstant, pushStructure), SpirvStorageClass.PushConstant);
        var pushWord = module.TypePointer(SpirvStorageClass.PushConstant, uintType);
        var globalId = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, vectorType), SpirvStorageClass.Input);
        module.AddDecoration(globalId, SpirvDecoration.BuiltIn, (uint)SpirvBuiltIn.GlobalInvocationId);
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        uint LoadPush(uint index) => module.AddInstruction(SpirvOp.Load, uintType,
            module.AddInstruction(SpirvOp.AccessChain, pushWord, push, Constant(0), Constant(index)));
        uint Pointer(int buffer, uint index) => module.AddInstruction(SpirvOp.AccessChain, wordPointer, buffers[buffer], Constant(0), index);
        uint Read(int buffer, uint index) => module.AddInstruction(SpirvOp.Load, uintType, Pointer(buffer, index));
        void Write(int buffer, uint index, uint value) => module.AddStatement(SpirvOp.Store, Pointer(buffer, Constant(index)), value);
        void If(uint condition, Action body)
        {
            var yes = module.AllocateId();
            var end = module.AllocateId();
            module.AddStatement(SpirvOp.SelectionMerge, end, 0);
            module.AddStatement(SpirvOp.BranchConditional, condition, yes, end);
            module.AddLabel(yes);
            body();
            module.AddStatement(SpirvOp.Branch, end);
            module.AddLabel(end);
        }
        var id = module.AddInstruction(SpirvOp.CompositeExtract, uintType,
            module.AddInstruction(SpirvOp.Load, vectorType, globalId), 0);
        var argumentsOffset = LoadPush(0);
        var indexByteOffset = LoadPush(1);
        var capacity = LoadPush(2);
        var elementSize = LoadPush(3);
        var perGroup = LoadPush(4);
        var pointList = Compare(SpirvOp.INotEqual, LoadPush(5), Constant(0));
        // The count is bounded by the buffer, as on the CPU path; status 1 reports a first index past it.
        var requested = Read(0, argumentsOffset);
        requested = Select(Compare(SpirvOp.ULessThan, requested, capacity), requested, capacity);
        var instances = Read(0, Operation(SpirvOp.IAdd, argumentsOffset, Constant(1)));
        var requestedFirst = Read(0, Operation(SpirvOp.IAdd, argumentsOffset, Constant(2)));
        var firstIndex = Select(Compare(SpirvOp.ULessThan, requestedFirst, capacity), requestedFirst, capacity);
        var room = Operation(SpirvOp.ISub, capacity, firstIndex);
        var count = Select(Compare(SpirvOp.ULessThan, requested, room), requested, room);
        var baseVertex = Read(0, Operation(SpirvOp.IAdd, argumentsOffset, Constant(3)));
        var firstInstance = Read(0, Operation(SpirvOp.IAdd, argumentsOffset, Constant(4)));
        uint IndexAt(uint ordinal)
        {
            var byteOffset = Operation(SpirvOp.IAdd, indexByteOffset,
                Operation(SpirvOp.IMul, Operation(SpirvOp.IAdd, firstIndex, ordinal), elementSize));
            var word = Read(1, Operation(SpirvOp.ShiftRightLogical, byteOffset, Constant(2)));
            var shift = Operation(SpirvOp.IMul, Operation(SpirvOp.BitwiseAnd, byteOffset, Constant(3)), Constant(8));
            var half = Operation(SpirvOp.BitwiseAnd, Operation(SpirvOp.ShiftRightLogical, word, shift), Constant(65535));
            return Select(Compare(SpirvOp.IEqual, elementSize, Constant(2)), half, word);
        }
        var primitives = Select(pointList, count, Operation(SpirvOp.UDiv, count, Constant(3)));
        var vertices = Select(pointList, count, Operation(SpirvOp.IMul, primitives, Constant(3)));
        var groups = Operation(SpirvOp.UDiv, Operation(SpirvOp.IAdd, primitives,
            Operation(SpirvOp.ISub, perGroup, Constant(1))), perGroup);
        var active = And(Compare(SpirvOp.INotEqual, count, Constant(0)), Compare(SpirvOp.INotEqual, instances, Constant(0)));
        var requestedActive = And(Compare(SpirvOp.INotEqual, requested, Constant(0)), Compare(SpirvOp.INotEqual, instances, Constant(0)));
        var rangeValid = And(Compare(SpirvOp.ULessThanEqual, requestedFirst, capacity),
            Compare(SpirvOp.ULessThanEqual, requested, room));
        var nonzeroGroups = Select(Compare(SpirvOp.IEqual, groups, Constant(0)), Constant(1), groups);
        var limitsValid = And(And(Compare(SpirvOp.ULessThanEqual, groups, LoadPush(6)),
            Compare(SpirvOp.ULessThanEqual, instances, LoadPush(7))),
            Compare(SpirvOp.LogicalOr, Compare(SpirvOp.IEqual, groups, Constant(0)),
                Compare(SpirvOp.ULessThanEqual, instances, Operation(SpirvOp.UDiv, LoadPush(8), nonzeroGroups))));
        var execute = And(active, limitsValid);
        If(Compare(SpirvOp.IEqual, id, Constant(0)), () =>
        {
            Write(2, 0, Select(execute, groups, Constant(0)));
            Write(2, 1, Select(execute, instances, Constant(0)));
            Write(2, 2, Constant(1));
            Write(2, 4, Select(execute, vertices, Constant(0)));
            Write(2, 5, Constant(0));
            Write(2, 6, firstInstance);
            Write(2, 7, Constant(4));
            Write(2, 8, LoadPush(9));
            Write(2, 9, LoadPush(10));
            var error = Select(limitsValid, Select(rangeValid, Constant(0), Constant(1)), Constant(2));
            Write(3, 0, Select(requestedActive, error, Constant(0)));
        });
        If(And(execute, Compare(SpirvOp.ULessThan, id, vertices)), () =>
            module.AddStatement(SpirvOp.Store, Pointer(2, Operation(SpirvOp.IAdd, id, Constant(HeaderWords))),
                Operation(SpirvOp.IAdd, IndexAt(id), baseVertex)));
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [.. buffers, push, globalId]);
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 64, 1, 1);
        return module.Build();
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Vulkan;

// The task stage of an automatic indirect mesh draw, launched as one workgroup of one invocation.
// It applies SpirvMeshIndirect's rules for an automatic draw to the current arguments, passes the
// mesh shader its draw parameters in the payload and launches the groups of the helper's command.
public static class SpirvMeshTask
{
    // The task stage's push range directly follows the push data the other stages share.
    public const uint PushOffsetBytes = PushData.ByteSize;

    // Words 0-1: the arguments' device address. Words 2-3: the address of two diagnostic counters
    // (draws over the mesh limits, draws past the range), 0 for none. Words 4-7: the arguments,
    // read only when their address is 0.
    public const uint PushWordCount = 8;

    // SpirvMeshIndirect's push words 4-8: primitives per group, point list and the mesh group limits.
    public const uint SpecPrimitivesPerGroup = 0;
    public const uint SpecPointList = 1;
    public const uint SpecMaxGroupCountX = 2;
    public const uint SpecMaxGroupCountY = 3;
    public const uint SpecMaxGroupTotalCount = 4;

    // { DrawCount, FirstVertex, FirstInstance }: the first three of the mesh shader's draw parameters.
    public const uint PayloadWordCount = 3;

    public static byte[] Create()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        module.AddCapability(SpirvCapability.Int64);
        module.AddCapability(SpirvCapability.PhysicalStorageBufferAddresses);
        module.AddCapability(SpirvCapability.MeshShadingExt);
        module.AddExtension("SPV_EXT_mesh_shader");
        module.SetPhysicalStorageBuffer64MemoryModel();
        var voidType = module.TypeVoid();
        var uintType = module.TypeInt(32, false);
        var ulongType = module.TypeInt(64, false);
        var boolType = module.TypeBool();
        uint Constant(uint value) => module.Constant(uintType, value);
        uint Constant64(ulong value) => module.Constant64(ulongType, value);
        uint Operation(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, uintType, left, right);
        uint Compare(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, boolType, left, right);
        uint And(uint left, uint right) => Compare(SpirvOp.LogicalAnd, left, right);
        uint Not(uint value) => module.AddInstruction(SpirvOp.LogicalNot, boolType, value);
        uint Select(uint condition, uint yes, uint no) => module.AddInstruction(SpirvOp.Select, uintType, condition, yes, no);
        uint Specialization(uint id, uint defaultValue) => module.SpecConstant(uintType, id, defaultValue);
        var perGroup = Specialization(SpecPrimitivesPerGroup, 1);
        var pointListValue = Specialization(SpecPointList, 0);
        var maxGroupsX = Specialization(SpecMaxGroupCountX, 1);
        var maxGroupsY = Specialization(SpecMaxGroupCountY, 1);
        var maxGroups = Specialization(SpecMaxGroupTotalCount, 1);
        var pushArray = module.TypeArray(uintType, PushWordCount);
        module.AddDecoration(pushArray, SpirvDecoration.ArrayStride, 4);
        var pushStructure = module.TypeStruct(pushArray);
        module.AddDecoration(pushStructure, SpirvDecoration.Block);
        module.AddMemberDecoration(pushStructure, 0, SpirvDecoration.Offset, PushOffsetBytes);
        var push = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.PushConstant, pushStructure), SpirvStorageClass.PushConstant);
        var pushWord = module.TypePointer(SpirvStorageClass.PushConstant, uintType);
        var payload = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.TaskPayloadWorkgroupExt,
            PayloadType(module, uintType)), SpirvStorageClass.TaskPayloadWorkgroupExt);
        var payloadWord = module.TypePointer(SpirvStorageClass.TaskPayloadWorkgroupExt, uintType);
        var deviceWord = module.TypePointer(SpirvStorageClass.PhysicalStorageBuffer, uintType);
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        var entry = module.AddLabel();
        uint LoadPush(uint index) => module.AddInstruction(SpirvOp.Load, uintType,
            module.AddInstruction(SpirvOp.AccessChain, pushWord, push, Constant(0), Constant(index)));
        uint Address(uint low, uint high) => module.AddInstruction(SpirvOp.BitwiseOr, ulongType,
            module.AddInstruction(SpirvOp.UConvert, ulongType, low),
            module.AddInstruction(SpirvOp.ShiftLeftLogical, ulongType, module.AddInstruction(SpirvOp.UConvert, ulongType, high), Constant64(32)));
        uint DeviceWord(uint address, uint byteOffset) => module.AddInstruction(SpirvOp.ConvertUToPtr, deviceWord,
            module.AddInstruction(SpirvOp.IAdd, ulongType, address, Constant64(byteOffset)));
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
        var argumentsAddress = Address(LoadPush(0), LoadPush(1));
        var counterAddress = Address(LoadPush(2), LoadPush(3));
        // The arguments (vertexCount, instanceCount, firstVertex, firstInstance) are read through their
        // address, or come in the push words when the host has none. Address 0 is never read.
        var pushed = new uint[4];
        for (uint index = 0; index < pushed.Length; index++)
            pushed[index] = LoadPush(4 + index);
        var addressed = Compare(SpirvOp.INotEqual, argumentsAddress, Constant64(0));
        var read = module.AllocateId();
        var merge = module.AllocateId();
        module.AddStatement(SpirvOp.SelectionMerge, merge, 0);
        module.AddStatement(SpirvOp.BranchConditional, addressed, read, merge);
        module.AddLabel(read);
        var loaded = new uint[4];
        for (uint index = 0; index < loaded.Length; index++)
            loaded[index] = module.AddInstruction(SpirvOp.Load, uintType, DeviceWord(argumentsAddress, index * 4), 2u, 4u);
        module.AddStatement(SpirvOp.Branch, merge);
        module.AddLabel(merge);
        uint Argument(int index) => module.AddInstruction(SpirvOp.Phi, uintType, loaded[index], read, pushed[index], entry);
        var requested = Argument(0);
        var instances = Argument(1);
        var requestedFirst = Argument(2);
        var firstInstance = Argument(3);
        // From here on as SpirvMeshIndirect for an automatic draw, whose capacity is the whole range.
        var capacity = Constant(uint.MaxValue);
        requested = Select(Compare(SpirvOp.ULessThan, requested, capacity), requested, capacity);
        var firstIndex = Select(Compare(SpirvOp.ULessThan, requestedFirst, capacity), requestedFirst, capacity);
        var room = Operation(SpirvOp.ISub, capacity, firstIndex);
        var count = Select(Compare(SpirvOp.ULessThan, requested, room), requested, room);
        var pointList = Compare(SpirvOp.INotEqual, pointListValue, Constant(0));
        var primitives = Select(pointList, count, Operation(SpirvOp.UDiv, count, Constant(3)));
        var vertices = Select(pointList, count, Operation(SpirvOp.IMul, primitives, Constant(3)));
        var groups = Operation(SpirvOp.UDiv, Operation(SpirvOp.IAdd, primitives,
            Operation(SpirvOp.ISub, perGroup, Constant(1))), perGroup);
        var active = And(Compare(SpirvOp.INotEqual, count, Constant(0)), Compare(SpirvOp.INotEqual, instances, Constant(0)));
        var requestedActive = And(Compare(SpirvOp.INotEqual, requested, Constant(0)), Compare(SpirvOp.INotEqual, instances, Constant(0)));
        var rangeValid = And(Compare(SpirvOp.ULessThanEqual, requestedFirst, capacity),
            Compare(SpirvOp.ULessThanEqual, requested, room));
        var nonzeroGroups = Select(Compare(SpirvOp.IEqual, groups, Constant(0)), Constant(1), groups);
        var limitsValid = And(And(Compare(SpirvOp.ULessThanEqual, groups, maxGroupsX),
            Compare(SpirvOp.ULessThanEqual, instances, maxGroupsY)),
            Compare(SpirvOp.LogicalOr, Compare(SpirvOp.IEqual, groups, Constant(0)),
                Compare(SpirvOp.ULessThanEqual, instances, Operation(SpirvOp.UDiv, maxGroups, nonzeroGroups))));
        var execute = And(active, limitsValid);
        // The helper's nonzero statuses, counted: 2 (over the mesh limits) in word 0, 1 (a range past
        // the capacity) in word 1. Device-scope relaxed atomics.
        var counted = Compare(SpirvOp.INotEqual, counterAddress, Constant64(0));
        void Count(uint condition, uint byteOffset) => If(And(counted, condition), () =>
            module.AddInstruction(SpirvOp.AtomicIAdd, uintType, DeviceWord(counterAddress, byteOffset), Constant(1), Constant(0), Constant(1)));
        Count(And(requestedActive, Not(limitsValid)), 0);
        Count(And(And(requestedActive, limitsValid), Not(rangeValid)), 4);
        // The draw parameters the helper wrote for the mesh shader, then its indirect command. The
        // only invocation emits it once, in uniform control flow.
        void Pass(uint member, uint value) => module.AddStatement(SpirvOp.Store,
            module.AddInstruction(SpirvOp.AccessChain, payloadWord, payload, Constant(member)), value);
        Pass(0, Select(execute, vertices, Constant(0)));
        Pass(1, firstIndex);
        Pass(2, firstInstance);
        module.AddStatement(SpirvOp.EmitMeshTasksExt, Select(execute, groups, Constant(0)),
            Select(execute, instances, Constant(0)), Select(execute, Constant(1), Constant(0)), payload);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.TaskExt, main, "main", [push, payload]);
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 1, 1, 1);
        return module.Build();
    }

    // The payload's type. The mesh shader of a task-launched draw declares the same one.
    internal static uint PayloadType(SpirvModuleBuilder module, uint uintType) =>
        module.TypeStruct([.. Enumerable.Repeat(uintType, (int)PayloadWordCount)]);
}

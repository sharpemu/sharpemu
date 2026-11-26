// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

// The groups of a dispatch whose guest arguments count threads (DISPATCH_INITIATOR.USE_THREAD_DIMENSIONS) when a shader
// wrote them: one invocation reads the three thread counts (binding 0), writes the groups of the shader's group size in the
// pipeline's axis order for vkCmdDispatchIndirect (binding 1), and the counts as the shader's thread limits (binding 2), as
// the CPU path does with counts it read. A zero count launches nothing.
public static class SpirvDispatchThreads
{
    // [0] the arguments' word in binding 0; per logical axis i: [1 + i] the group size, [4 + i] the axis in binding 1
    // (the pipeline's physical axis), [8 + i] the device's group limit on that axis; [7] the limits' word in binding 2, or
    // NoLimits.
    public const uint PushWordCount = 11;
    public const uint NoLimits = uint.MaxValue;

    public static byte[] Create()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var uintType = module.TypeInt(32, false);
        var boolType = module.TypeBool();
        uint Constant(uint value) => module.Constant(uintType, value);
        uint Operation(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, uintType, left, right);
        uint Compare(SpirvOp operation, uint left, uint right) => module.AddInstruction(operation, boolType, left, right);
        uint Select(uint condition, uint yes, uint no) => module.AddInstruction(SpirvOp.Select, uintType, condition, yes, no);
        var array = module.TypeRuntimeArray(uintType);
        module.AddDecoration(array, SpirvDecoration.ArrayStride, 4);
        var structure = module.TypeStruct(array);
        module.AddDecoration(structure, SpirvDecoration.Block);
        module.AddMemberDecoration(structure, 0, SpirvDecoration.Offset, 0);
        var bufferPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, structure);
        var wordPointer = module.TypePointer(SpirvStorageClass.StorageBuffer, uintType);
        var buffers = new uint[3];
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
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        uint Pointer(int buffer, uint index) => module.AddInstruction(SpirvOp.AccessChain, wordPointer, buffers[buffer], Constant(0), index);
        uint LoadParameter(uint index) => module.AddInstruction(SpirvOp.Load, uintType,
            module.AddInstruction(SpirvOp.AccessChain, pushWord, push, Constant(0), Constant(index)));
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

        var argumentsWord = LoadParameter(0);
        var threads = new uint[3];
        for (uint axis = 0; axis < 3; axis++)
        {
            threads[axis] = module.AddInstruction(SpirvOp.Load, uintType, Pointer(0, Operation(SpirvOp.IAdd, argumentsWord, Constant(axis))));
        }

        var empty = module.AddInstruction(SpirvOp.LogicalOr, boolType,
            module.AddInstruction(SpirvOp.LogicalOr, boolType,
                Compare(SpirvOp.IEqual, threads[0], Constant(0)), Compare(SpirvOp.IEqual, threads[1], Constant(0))),
            Compare(SpirvOp.IEqual, threads[2], Constant(0)));
        for (uint axis = 0; axis < 3; axis++)
        {
            var size = LoadParameter(1 + axis);
            size = Select(Compare(SpirvOp.IEqual, size, Constant(0)), Constant(1), size);
            // (threads - 1) / size + 1 rounds up without overflow; a zero count was handled above.
            var groups = Operation(SpirvOp.IAdd, Operation(SpirvOp.UDiv,
                Operation(SpirvOp.ISub, threads[axis], Constant(1)), size), Constant(1));
            var limit = LoadParameter(8 + axis);
            groups = Select(Compare(SpirvOp.UGreaterThan, groups, limit), limit, groups);
            var output = LoadParameter(4 + axis);
            module.AddStatement(SpirvOp.Store, Pointer(1, output), Select(empty, Constant(0), groups));
        }

        var limitsWord = LoadParameter(7);
        If(Compare(SpirvOp.INotEqual, limitsWord, Constant(NoLimits)), () =>
        {
            for (uint axis = 0; axis < 3; axis++)
            {
                module.AddStatement(SpirvOp.Store, Pointer(2, Operation(SpirvOp.IAdd, limitsWord, Constant(axis))), threads[axis]);
            }
        });
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", [.. buffers, push]);
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 1, 1, 1);
        return module.Build();
    }
}

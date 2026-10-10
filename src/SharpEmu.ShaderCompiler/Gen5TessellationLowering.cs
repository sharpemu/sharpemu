// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public static class Gen5TessellationLowering
{
    public static uint FindFactorBuffer(Resources.ShaderResourcePlan plan, uint hullEntryPc)
    {
        uint? resource = null;
        foreach (var instruction in plan.Graph.Program.Instructions)
        {
            // SGPR4 is the factor-ring byte offset in the merged HS ABI. Do
            // not mistake a later reuse of that register for a factor store.
            if (instruction.Destinations.Any(d => d.Kind == Gen5OperandKind.ScalarRegister && d.Value == 4))
                throw new NotSupportedException("The hull shader overwrites its hardware factor-ring offset.");
            if (instruction.Pc < hullEntryPc || !instruction.Opcode.StartsWith("BufferStore", StringComparison.Ordinal) ||
                instruction.Sources.Count < 3 || instruction.Sources[2] != Gen5Operand.Scalar(4)) continue;
            if (!instruction.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal) ||
                instruction.Control is not Gen5BufferMemoryControl { Typed: false } ||
                plan.Memory.Find(instruction.Pc) is not { Resource: var current } ||
                current == Resources.MemoryAccessInfo.NoResource)
                throw new NotSupportedException("The hull factor ring requires a resolvable raw buffer descriptor.");
            if (resource is { } previous && previous != current)
                throw new NotSupportedException("The hull shader writes factors to multiple buffer descriptors.");
            resource = current;
        }
        return resource ?? throw new NotSupportedException("The hull shader has no identifiable hardware factor-ring writes.");
    }

    // The normalized inputs append the separate HS address after the LS user
    // scalars. Move that pair to its architectural s0:s1 before LS can reuse
    // those input slots, then publish LS writes before HS reads. Guest
    // instruction addresses are preserved for physical-memory reads.
    public static Gen5ShaderProgram PrepareMergedHull(Gen5ShaderProgram program, uint hullUserDataRegister)
    {
        if (hullUserDataRegister < 8 || hullUserDataRegister > 40)
            throw new ArgumentOutOfRangeException(nameof(hullUserDataRegister));
        if (program.FusedContinuationPc is not { } continuation ||
            !program.Instructions.Any(i => i.Pc == continuation))
            throw new ArgumentException("A merged hull shader requires its registered LS/HS continuation.", nameof(program));
        var instructions = new List<Gen5ShaderInstruction>(program.Instructions.Count + 2)
        {
            new(0, Gen5ShaderEncoding.Sop1, "SMovB64", [0u],
                [Gen5Operand.Scalar(hullUserDataRegister)], [Gen5Operand.Scalar(0)], null),
        };
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Pc == continuation)
                instructions.Add(new(continuation + 4, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
            instructions.Add(instruction with
            {
                Pc = checked(instruction.Pc + (instruction.Pc < continuation ? 4u : 8u)),
                AddressOffset = program.InstructionAddressOffset(instruction.Pc),
            });
        }
        return new(program.Address, instructions) { FusedContinuationPc = checked(continuation + 8) };
    }
}

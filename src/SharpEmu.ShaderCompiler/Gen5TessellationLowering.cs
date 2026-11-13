// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public static class Gen5TessellationLowering
{
    public static uint FindFactorBuffer(Resources.ShaderResourcePlan plan, uint hullEntryPc)
    {
        uint? resource = null;
        var registers = plan.Graph.Program.TessellationRegisters;
        var factor = registers.HullFactorStoreScalar;
        foreach (var instruction in plan.Graph.Program.Instructions)
        {
            // SGPR4 is the factor-ring byte offset in the merged HS ABI. Do
            // not mistake a later reuse of that register for a factor store.
            if ((!registers.HullEntrySetsFactor || instruction.Pc >= hullEntryPc) &&
                instruction.Destinations.Any(d => d.Kind == Gen5OperandKind.ScalarRegister && d.Value == factor))
                throw new NotSupportedException("The hull shader overwrites its hardware factor-ring offset.");
            if (instruction.Pc < hullEntryPc || !instruction.Opcode.StartsWith("BufferStore", StringComparison.Ordinal) ||
                instruction.Sources.Count < 3 || instruction.Sources[2] != Gen5Operand.Scalar(factor)) continue;
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
                AddressOffset = instruction.ProgramOffset,
            });
        }
        return new(program.Address, instructions) { FusedContinuationPc = checked(continuation + 8) };
    }

    // A complete hull binary (AGC shader type HS) holds both halves in one code object,
    // with no jump between two code objects to mark the boundary. The local half runs for
    // the lanes below the low byte of the wave counts; the hull half then re-opens every
    // lane and keeps those below the second byte. That re-open is the hull entry.
    public static bool TryFindCombinedHullEntry(Gen5ShaderProgram program, out uint hullEntryPc)
    {
        hullEntryPc = 0;
        if (program.TessellationRegisters.HullWaveCountScalar is not { } counts) return false;
        var instructions = program.Instructions;
        var gate = 0;
        while (gate < instructions.Count && !(instructions[gate].Opcode.StartsWith("VCmpx", StringComparison.Ordinal) &&
            instructions[gate].Control is Gen5SdwaControl { Source1Select: 1 } &&
            instructions[gate].Sources.Contains(Gen5Operand.Scalar(counts)))) gate++;
        if (gate == instructions.Count) return false;
        for (var index = gate - 1; index >= 0 && !Ir.Gen5IrBranchResolver.Instance.IsBranch(instructions[index]); index--)
        {
            var instruction = instructions[index];
            if (instruction.Opcode != "SMovB64" || instruction.Destinations.Count != 1 || instruction.Sources.Count != 1 ||
                instruction.Destinations[0] != Gen5Operand.Scalar(126) || instruction.Sources[0] != Gen5Operand.Source(193)) continue;
            hullEntryPc = instruction.Pc;
            return true;
        }
        return false;
    }

    // Both halves of a complete hull binary share one set of user scalars, and the hull
    // half may read registers the local half left. It needs only the barrier that
    // publishes the local half's writes; the local half's skip lands on that barrier.
    public static Gen5ShaderProgram PrepareCombinedHull(Gen5ShaderProgram program)
    {
        if (!TryFindCombinedHullEntry(program, out var entry))
            throw new ArgumentException("A combined hull shader requires its local and hull wave-count gates.", nameof(program));
        var instructions = new List<Gen5ShaderInstruction>(program.Instructions.Count + 1);
        foreach (var instruction in program.Instructions)
        {
            if (Ir.Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var target) &&
                target != entry && (instruction.Pc < entry) != (target < entry))
                throw new NotSupportedException("A branch crosses the combined hull entry.");
            if (instruction.Pc == entry)
                instructions.Add(new(entry, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
            instructions.Add(instruction.Pc < entry ? instruction : instruction with
            {
                Pc = checked(instruction.Pc + 4),
                AddressOffset = instruction.ProgramOffset,
            });
        }
        return new(program.Address, instructions);
    }
}

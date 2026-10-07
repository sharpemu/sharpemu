// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Ir;

// The instructions that run with every lane of EXEC set. EXEC starts full; it stays full
// through S_WQM of a full EXEC and comes back when a saved copy of a full EXEC is moved or
// ORed back into it. Any other EXEC write (V_CMPX, S_AND_SAVEEXEC, ...) makes it unknown,
// and a join is full only when every path into it is. A backend can skip the EXEC guard of
// a vector register write there: no lane keeps its old value, so the old value need not
// stay live until the write.
public static class Gen5ExecFullAnalysis
{
    private const uint ExecLow = 126;
    private const uint ExecHigh = 127;

    private readonly struct State(bool reached, bool execFull, UInt128 fullCopies)
    {
        public readonly bool Reached = reached;
        public readonly bool ExecFull = execFull;
        // SGPRs 0..127 holding (part of) a copy of a full EXEC.
        public readonly UInt128 FullCopies = fullCopies;

        public static State Meet(State left, State right) =>
            !left.Reached ? right
            : !right.Reached ? left
            : new State(true, left.ExecFull && right.ExecFull, left.FullCopies & right.FullCopies);

        public bool SameAs(State other) =>
            Reached == other.Reached && ExecFull == other.ExecFull && FullCopies == other.FullCopies;
    }

    // Pcs of the instructions that start with a full EXEC.
    public static IReadOnlySet<uint> Analyze(Gen5ShaderProgram program, bool wave32) => AnalyzeCore(program, wave32, null);

    // Instructions whose EXEC can only contain lanes initialized by one vector
    // write. Joins require this on every path; unknown expansions decline it.
    public static IReadOnlySet<uint> AnalyzeInitializedLanes(Gen5ShaderProgram program, uint initializationPc, bool wave32) =>
        AnalyzeCore(program, wave32, initializationPc);

    public static IReadOnlySet<uint> AnalyzeMatchingExecutionLanes(Gen5ShaderProgram program, uint referencePc, bool wave32) =>
        AnalyzeCore(program, wave32, referencePc, allowSubsets: false);

    private static IReadOnlySet<uint> AnalyzeCore(Gen5ShaderProgram program, bool wave32, uint? initializationPc, bool allowSubsets = true)
    {
        var instructions = program.Instructions;
        var result = new HashSet<uint>();
        if (instructions.Count == 0)
        {
            return result;
        }

        var indexByPc = new Dictionary<uint, int>(instructions.Count);
        for (var index = 0; index < instructions.Count; index++)
        {
            if (instructions[index].Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64")
            {
                return result;
            }

            indexByPc[instructions[index].Pc] = index;
        }

        var states = new State[instructions.Count];
        if (initializationPc.HasValue && !indexByPc.ContainsKey(initializationPc.Value)) return result;
        // Quad expansion preserves a subset only when the initialization mask
        // already contains complete quads. Require the same EXEC as immediately
        // after a dominating WQM instruction, rather than assuming entry lanes.
        var quadClosures = !initializationPc.HasValue || !allowSubsets ? [] :
            Enumerable.Range(0, instructions.Count - 1).Where(index =>
                instructions[index].Pc < initializationPc.Value &&
                !instructions.Take(index).Any(instruction => Gen5IrBranchResolver.IsTerminator(instruction) ||
                    Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out _)) &&
                instructions[index] is { Opcode: "SWqmB64", Sources.Count: 1, Destinations.Count: 1 } wqm &&
                wqm.Sources[0] == Gen5Operand.Scalar(ExecLow) && wqm.Destinations[0] == Gen5Operand.Scalar(ExecLow) &&
                AnalyzeCore(program, wave32, instructions[index + 1].Pc, allowSubsets: false).Contains(initializationPc.Value)).ToArray();
        var quadClosed = quadClosures.Length != 0;
        UInt128 initialCopies = 0;
        foreach (var index in quadClosures)
        {
            // The exact EXEC saved immediately before WQM is contained in its
            // quad closure. Keep that fact only while both SGPR words survive.
            if (instructions.Skip(index).Take(indexByPc[initializationPc!.Value] - index).Any(instruction =>
                    Gen5IrBranchResolver.IsTerminator(instruction) || Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out _)) ||
                index == 0 || instructions[index - 1] is not { Opcode: "SMovB64", Sources.Count: 1, Destinations.Count: 1 } save ||
                save.Sources[0] != Gen5Operand.Scalar(ExecLow) || save.Destinations[0] is not
                    { Kind: Gen5OperandKind.ScalarRegister, Value: < ExecLow } destination || (destination.Value & 1) != 0) continue;
            var saved = new State(true, false, Bit(destination.Value) | Bit(destination.Value + 1));
            for (var cursor = index; cursor < indexByPc[initializationPc!.Value]; cursor++)
                saved = Transfer(instructions[cursor], saved, wave32, subset: true, relative: true, quadClosed: true);
            initialCopies |= saved.FullCopies;
        }
        states[0] = new State(true, !initializationPc.HasValue, 0);
        var pending = new Queue<int>();
        var queued = new bool[instructions.Count];
        pending.Enqueue(0);
        queued[0] = true;
        while (pending.TryDequeue(out var index))
        {
            queued[index] = false;
            var instruction = instructions[index];
            var input = instruction.Pc == initializationPc ? new State(true, true, initialCopies) : states[index];
            var output = Transfer(instruction, input, wave32, initializationPc.HasValue && allowSubsets,
                initializationPc.HasValue, quadClosed);
            void Flow(int successor)
            {
                var merged = State.Meet(states[successor], output);
                if (!merged.SameAs(states[successor]))
                {
                    states[successor] = merged;
                    if (!queued[successor])
                    {
                        queued[successor] = true;
                        pending.Enqueue(successor);
                    }
                }
            }

            if (Gen5IrBranchResolver.IsTerminator(instruction))
            {
                continue;
            }

            if (Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var targetPc))
            {
                if (!indexByPc.TryGetValue(targetPc, out var target))
                {
                    // A branch out of the decoded range: give up on the whole program.
                    return new HashSet<uint>();
                }

                Flow(target);
                if (Gen5IrBranchResolver.IsUnconditionalBranch(instruction))
                {
                    continue;
                }
            }

            if (index + 1 < instructions.Count)
            {
                Flow(index + 1);
            }
        }

        for (var index = 0; index < instructions.Count; index++)
        {
            if (states[index].Reached && (states[index].ExecFull || instructions[index].Pc == initializationPc))
            {
                result.Add(instructions[index].Pc);
            }
        }

        return result;
    }

    private static State Transfer(Gen5ShaderInstruction instruction, State input, bool wave32, bool subset = false,
        bool relative = false, bool quadClosed = false)
    {
        if (!input.Reached)
        {
            return input;
        }

        var execFull = input.ExecFull;
        var copies = input.FullCopies;
        var opcode = instruction.Opcode;
        var sources = instruction.Sources;
        var destinations = instruction.Destinations;
        // GFX10 VCMPX writes only EXEC; legacy SDWA/VOP3 destination bits
        // must not invalidate an SGPR holding a saved execution mask.
        var executionCompare = opcode.StartsWith("VCmpx", StringComparison.Ordinal);
        var wide = opcode.StartsWith('S') &&
            (opcode.EndsWith("B64", StringComparison.Ordinal) ||
             opcode.EndsWith("U64", StringComparison.Ordinal) ||
             opcode.EndsWith("I64", StringComparison.Ordinal));

        // Registers written, with EXEC treated separately.
        UInt128 written = 0;
        var writesExec = false;
        foreach (var destination in destinations)
        {
            if (destination.Kind != Gen5OperandKind.ScalarRegister || executionCompare)
            {
                continue;
            }

            written |= Bit(destination.Value);
            if (wide)
            {
                written |= Bit(destination.Value + 1);
            }
        }

        var scalarDestination = executionCompare ? null : instruction.Control switch
        {
            Gen5Vop3Control control => control.ScalarDestination,
            Gen5SdwaControl control => control.ScalarDestination,
            _ => null,
        };
        if (scalarDestination is { } sdst)
        {
            written |= Bit(sdst) | Bit(sdst + 1);
        }

        if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
        {
            written |= Bit(ExecLow) | Bit(ExecHigh);
        }
        else if (opcode.StartsWith("VCmp", StringComparison.Ordinal) && scalarDestination is null)
        {
            // VOPC compares write VCC.
            written |= Bit(106) | Bit(107);
        }

        if (opcode.StartsWith('V') && opcode.Contains("Co", StringComparison.Ordinal) && scalarDestination is null)
        {
            // VOP2 carry forms write VCC without listing it.
            written |= Bit(106) | Bit(107);
        }

        if (opcode.Contains("Saveexec", StringComparison.Ordinal) || opcode.Contains("Wrexec", StringComparison.Ordinal) ||
            opcode.StartsWith("SMovrel", StringComparison.Ordinal))
        {
            // These also write EXEC, or registers chosen at run time.
            written |= Bit(ExecLow) | Bit(ExecHigh);
            if (opcode.StartsWith("SMovrel", StringComparison.Ordinal))
            {
                return new State(true, false, 0);
            }
        }

        writesExec = (written & (Bit(ExecLow) | Bit(ExecHigh))) != 0;
        var nextCopies = copies & ~written;
        var nextExecFull = execFull;
        if (writesExec)
        {
            nextExecFull = false;
            var onlyExecDestination = destinations.Count == 1 && destinations[0] is { Kind: Gen5OperandKind.ScalarRegister, Value: ExecLow };
            // In wave64 a 32-bit write leaves EXEC_HI as it was.
            if (onlyExecDestination && scalarDestination is null && (wide || wave32))
            {
                switch (opcode)
                {
                    case "SWqmB64" or "SWqmB32" when sources.Count == 1 && IsExec(sources[0]):
                        nextExecFull = execFull && (!relative || quadClosed);
                        break;
                    case "SMovB64" or "SMovB32" when sources.Count == 1:
                        nextExecFull = IsFullCopy(sources[0], copies, execFull, wide);
                        break;
                    case "SOrB64" or "SOrB32" when sources.Count == 2:
                        nextExecFull = IsFullCopy(sources[0], copies, execFull, wide) ||
                            IsFullCopy(sources[1], copies, execFull, wide);
                        break;
                }
            }
        }
        else if (execFull && opcode is "SMovB64" or "SMovB32" && destinations.Count == 1 &&
                 destinations[0].Kind == Gen5OperandKind.ScalarRegister && sources.Count == 1 && IsExec(sources[0]))
        {
            // A saved copy of the full EXEC.
            nextCopies |= Bit(destinations[0].Value);
            if (wide)
            {
                nextCopies |= Bit(destinations[0].Value + 1);
            }
        }

        if (subset && writesExec)
        {
            nextExecFull = opcode.StartsWith("VCmpx", StringComparison.Ordinal) || opcode is "SAndSaveexecB64" or "SAndSaveexecB32"
                ? execFull : false;
            var onlyExec = destinations.Count == 1 && destinations[0] == Gen5Operand.Scalar(ExecLow);
            if (onlyExec && (wide || wave32) && scalarDestination is null)
            {
                bool Known(int index) => sources.Count > index && IsFullCopy(sources[index], copies, execFull, wide);
                nextExecFull = opcode switch
                {
                    "SMovB64" or "SMovB32" => Known(0),
                    "SWqmB64" or "SWqmB32" => quadClosed && Known(0),
                    "SAndB64" or "SAndB32" => Known(0) || Known(1),
                    "SAndn2B64" or "SAndn2B32" => Known(0),
                    "SAndn1B64" or "SAndn1B32" => Known(1),
                    "SOrB64" or "SOrB32" => Known(0) && Known(1),
                    _ => nextExecFull,
                };
            }
        }

        if (opcode.Contains("Saveexec", StringComparison.Ordinal) && execFull && destinations.Count >= 1 &&
            destinations[0].Kind == Gen5OperandKind.ScalarRegister && destinations[0].Value < ExecLow)
        {
            // The saved destination receives the (full) EXEC from before the instruction.
            nextCopies |= Bit(destinations[0].Value);
            if (wide)
            {
                nextCopies |= Bit(destinations[0].Value + 1);
            }
        }

        if (subset && !writesExec && destinations.Count == 1 &&
            destinations[0] is { Kind: Gen5OperandKind.ScalarRegister, Value: < ExecLow } savedDestination && (wide || wave32))
        {
            bool Known(int index) => sources.Count > index && IsFullCopy(sources[index], copies, execFull, wide);
            var known = opcode switch
            {
                "SMovB64" or "SMovB32" => Known(0),
                "SAndB64" or "SAndB32" => Known(0) || Known(1),
                "SAndn2B64" or "SAndn2B32" => Known(0),
                "SAndn1B64" or "SAndn1B32" => Known(1),
                "SOrB64" or "SOrB32" => Known(0) && Known(1),
                "SWqmB64" or "SWqmB32" => quadClosed && Known(0),
                _ => false,
            };
            if (known) nextCopies |= Bit(savedDestination.Value) | (wide ? Bit(savedDestination.Value + 1) : 0);
        }

        return new State(true, nextExecFull, nextCopies);
    }

    private static bool IsExec(Gen5Operand operand) =>
        operand.Kind == Gen5OperandKind.ScalarRegister && operand.Value == ExecLow;

    // True when the operand is a full EXEC or a saved copy of one.
    private static bool IsFullCopy(Gen5Operand operand, UInt128 copies, bool execFull, bool wide)
    {
        if (operand.Kind != Gen5OperandKind.ScalarRegister)
        {
            return false;
        }

        if (operand.Value == ExecLow)
        {
            return execFull;
        }

        var mask = Bit(operand.Value) | (wide ? Bit(operand.Value + 1) : 0);
        return (copies & mask) == mask;
    }

    private static UInt128 Bit(uint register) => register < 128 ? UInt128.One << (int)register : 0;
}

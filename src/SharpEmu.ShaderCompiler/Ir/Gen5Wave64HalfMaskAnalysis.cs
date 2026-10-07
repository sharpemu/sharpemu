// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Ir;

public sealed class Gen5Wave64HalfMaskPlan
{
    public required IReadOnlyDictionary<uint, uint[]> ExactPairsBefore { get; init; }

    public required IReadOnlyDictionary<uint, uint> HalfMaskWrites { get; init; }

    public required IReadOnlySet<uint> SharedMemoryBarriersBefore { get; init; }
}

public static class Gen5Wave64HalfMaskAnalysis
{
    private const uint Vcc = 106;
    private const uint Exec = 126;
    private const uint VcczSource = 251;
    private const uint ExeczSource = 252;
    private const uint SccSource = 253;

    private static readonly UInt128 EvenRegisters = new(0x5555_5555_5555_5555UL, 0x5555_5555_5555_5555UL);
    private static readonly UInt128 OddRegisters = EvenRegisters << 1;

    private static readonly HashSet<string> CarryIn = new(StringComparer.Ordinal)
    {
        "VAddcU32", "VAddCoCiU32", "VSubbU32", "VSubCoCiU32", "VSubbrevU32", "VSubrevCoCiU32",
    };

    private static readonly HashSet<string> CarryOut = new(CarryIn, StringComparer.Ordinal)
    {
        "VAddCoU32", "VSubCoU32", "VSubrevCoU32", "VMadU64U32", "VMadI64I32",
    };

    private static readonly HashSet<string> BitwiseWithScc = new(StringComparer.Ordinal)
    {
        "SNotB64", "SAndB64", "SOrB64", "SXorB64", "SAndn1B64", "SAndn2B64", "SOrn1B64", "SOrn2B64",
        "SNandB64", "SNorB64", "SXnorB64",
        "SAndSaveexecB64", "SOrSaveexecB64", "SXorSaveexecB64", "SAndn1SaveexecB64", "SAndn2SaveexecB64",
        "SOrn1SaveexecB64", "SOrn2SaveexecB64", "SNandSaveexecB64", "SNorSaveexecB64", "SXnorSaveexecB64",
    };

    private static readonly HashSet<string> BitwiseWithoutScc = new(StringComparer.Ordinal)
    {
        "SMovB64", "SWqmB64", "SCselectB64",
    };

    private static readonly HashSet<string> SccReaders = new(StringComparer.Ordinal)
    {
        "SCbranchScc0", "SCbranchScc1", "SAddcU32", "SSubbU32", "SCselectB32", "SCselectB64",
    };

    private static readonly HashSet<string> SccWriters = new(StringComparer.Ordinal)
    {
        "SNotB32", "SAbsI32", "SWqmB32", "SBrevB32", "SBcnt1I32B32", "SBcnt1I32B64", "SQuadmaskB32", "SQuadmaskB64",
        "SAddU32", "SSubU32", "SAddI32", "SSubI32", "SAddcU32", "SSubbU32",
        "SAndB32", "SOrB32", "SXorB32", "SAndn2B32", "SOrn2B32", "SNandB32", "SNorB32", "SXnorB32",
        "SLshlB32", "SLshrB32", "SAshrI32", "SBfeU32", "SBfeI32", "SMinU32", "SMinI32", "SMaxU32", "SMaxI32",
        "SLshlB64", "SLshrB64", "SAshrI64", "SBfeU64", "SBfeI64", "SBfmB64",
    };

    private static readonly HashSet<string> DynamicControlFlow = new(StringComparer.Ordinal)
    {
        "SSetpcB64", "SSwappcB64", "SRfeB64", "SCbranchJoin", "SCbranchGFork", "SCbranchIFork",
    };

    private readonly record struct State(bool Reached, UInt128 Half, bool PendingRead, bool PendingWrite)
    {
        public static State Meet(State left, State right) =>
            !left.Reached ? right
            : !right.Reached ? left
            : new State(true, left.Half | right.Half, left.PendingRead | right.PendingRead, left.PendingWrite | right.PendingWrite);
    }

    public static Gen5Wave64HalfMaskPlan? Analyze(Gen5ShaderProgram program, IReadOnlySet<uint> sharedFlatPcs)
    {
        var instructions = program.Instructions;
        if (instructions.Count == 0)
        {
            return null;
        }

        var successors = BuildSuccessors(instructions);
        if (successors is null)
        {
            return null;
        }

        var sccLiveAfter = ComputeSccLiveAfter(instructions, successors);
        var states = new State[instructions.Count];
        states[0] = new State(true, 0, false, false);
        var pending = new Queue<int>();
        var queued = new bool[instructions.Count];
        pending.Enqueue(0);
        queued[0] = true;
        while (pending.TryDequeue(out var index))
        {
            queued[index] = false;
            var output = Step(instructions[index], states[index], sccLiveAfter[index], sharedFlatPcs, out _, out _, out _);
            foreach (var successor in successors[index])
            {
                var merged = State.Meet(states[successor], output);
                if (merged != states[successor])
                {
                    states[successor] = merged;
                    if (!queued[successor])
                    {
                        queued[successor] = true;
                        pending.Enqueue(successor);
                    }
                }
            }
        }

        var exactPairs = new Dictionary<uint, uint[]>();
        var halfWrites = new Dictionary<uint, uint>();
        var barriers = new HashSet<uint>();
        for (var index = 0; index < instructions.Count; index++)
        {
            if (!states[index].Reached)
            {
                continue;
            }

            var instruction = instructions[index];
            Step(instruction, states[index], sccLiveAfter[index], sharedFlatPcs, out var exact, out var barrier, out var halfWrite);
            if (exact != 0)
            {
                exactPairs[instruction.Pc] = PairBases(exact);
            }

            if (barrier)
            {
                barriers.Add(instruction.Pc);
            }

            if (halfWrite && TryGetHalfMaskDestination(instruction, out var maskDestination))
            {
                halfWrites[instruction.Pc] = maskDestination;
            }
        }

        return new Gen5Wave64HalfMaskPlan
        {
            ExactPairsBefore = exactPairs,
            HalfMaskWrites = halfWrites,
            SharedMemoryBarriersBefore = barriers,
        };
    }

    private static State Step(
        Gen5ShaderInstruction instruction,
        State input,
        bool sccLiveAfter,
        IReadOnlySet<uint> sharedFlatPcs,
        out UInt128 exact,
        out bool sharedBarrier,
        out bool halfWrite)
    {
        var opcode = instruction.Opcode;
        var half = input.Half;
        var bitwise = IsBitwiseMaskOperation(instruction);
        UInt128 needed = 0;
        UInt128 bitwiseInputs = 0;
        for (var index = 0; index < instruction.Sources.Count; index++)
        {
            var operand = instruction.Sources[index];
            if (operand.Kind == Gen5OperandKind.EncodedConstant)
            {
                if (operand.Value == VcczSource)
                {
                    needed |= Pair(Vcc);
                }
                else if (operand.Value == ExeczSource)
                {
                    needed |= Pair(Exec);
                }

                continue;
            }

            if (operand.Kind != Gen5OperandKind.ScalarRegister)
            {
                continue;
            }

            if (bitwise)
            {
                bitwiseInputs |= Pair(operand.Value);
                continue;
            }

            if (index == 2 && (opcode == "VCndmaskB32" || CarryIn.Contains(opcode)))
            {
                continue;
            }

            needed |= ReadRegisters(instruction, index, operand.Value);
        }

        if (opcode is "SCbranchExecz" or "SCbranchExecnz")
        {
            needed |= Pair(Exec);
        }
        else if (opcode is "SCbranchVccz" or "SCbranchVccnz")
        {
            needed |= Pair(Vcc);
        }

        if ((instruction.Encoding == Gen5ShaderEncoding.Sopk || opcode.StartsWith("SBitset", StringComparison.Ordinal)) &&
            instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister } readDestination, ..])
        {
            needed |= Range(readDestination.Value, opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u);
        }

        if (opcode.StartsWith("SMovrel", StringComparison.Ordinal))
        {
            needed |= half;
        }

        if (bitwise)
        {
            if (opcode.EndsWith("SaveexecB64", StringComparison.Ordinal))
            {
                bitwiseInputs |= Pair(Exec);
            }

            if (sccLiveAfter && BitwiseWithScc.Contains(opcode))
            {
                needed |= bitwiseInputs;
            }
        }

        exact = Expand(needed & half);
        half &= ~exact;

        var pendingRead = input.PendingRead;
        var pendingWrite = input.PendingWrite;
        if (exact != 0)
        {
            pendingRead = false;
            pendingWrite = false;
        }

        var (reads, writes) = SharedMemoryAccess(instruction, sharedFlatPcs);
        sharedBarrier = (writes && (pendingRead || pendingWrite)) || (reads && pendingWrite);
        if (sharedBarrier)
        {
            pendingRead = false;
            pendingWrite = false;
        }

        pendingRead |= reads;
        pendingWrite |= writes;
        if (opcode == "SBarrier")
        {
            pendingRead = false;
            pendingWrite = false;
        }

        halfWrite = false;
        if (TryGetHalfMaskDestination(instruction, out var maskDestination))
        {
            half |= Pair(maskDestination);
            halfWrite = true;
        }
        else if (bitwise)
        {
            var destination = instruction.Destinations[0].Value;
            var outputHalf = (bitwiseInputs & half) != 0;
            half = outputHalf ? half | Pair(destination) : half & ~Pair(destination);
            if (opcode.EndsWith("SaveexecB64", StringComparison.Ordinal))
            {
                half = outputHalf ? half | Pair(Exec) : half & ~Pair(Exec);
            }
        }
        else
        {
            var wide = IsWideScalar(opcode);
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind != Gen5OperandKind.ScalarRegister)
                {
                    continue;
                }

                half &= ~Bit(destination.Value);
                if (wide)
                {
                    half &= ~Bit(destination.Value + 1);
                }
            }

            if (opcode.EndsWith("SaveexecB32", StringComparison.Ordinal))
            {
                half &= ~Pair(Exec);
            }
        }

        return new State(true, half, pendingRead, pendingWrite);
    }

    private static bool TryGetHalfMaskDestination(Gen5ShaderInstruction instruction, out uint destination)
    {
        var opcode = instruction.Opcode;
        if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
        {
            destination = Exec;
        }
        else if (opcode.StartsWith("VCmp", StringComparison.Ordinal))
        {
            destination = instruction.Control switch
            {
                Gen5SdwaControl { ScalarDestination: { } sdwa } => sdwa,
                Gen5Vop3Control { ScalarDestination: { } vop3 } => vop3,
                _ => Vcc,
            };
        }
        else if (CarryOut.Contains(opcode))
        {
            destination = instruction.Control is Gen5Vop3Control { ScalarDestination: { } carry } ? carry : Vcc;
        }
        else
        {
            destination = 0;
            return false;
        }

        return (destination & 1) == 0 && (destination < 124 || destination == Exec);
    }

    private static bool IsBitwiseMaskOperation(Gen5ShaderInstruction instruction)
    {
        if (!BitwiseWithScc.Contains(instruction.Opcode) && !BitwiseWithoutScc.Contains(instruction.Opcode))
        {
            return false;
        }

        if (instruction.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister } destination] ||
            (destination.Value & 1) != 0 ||
            (destination.Value >= 124 && destination.Value != Exec))
        {
            return false;
        }

        foreach (var source in instruction.Sources)
        {
            if (source.Kind == Gen5OperandKind.VectorRegister)
            {
                return false;
            }

            if (source.Kind == Gen5OperandKind.ScalarRegister &&
                ((source.Value & 1) != 0 || (source.Value >= 124 && source.Value != Exec)))
            {
                return false;
            }
        }

        return true;
    }

    private static (bool Reads, bool Writes) SharedMemoryAccess(Gen5ShaderInstruction instruction, IReadOnlySet<uint> sharedFlatPcs)
    {
        if (instruction.Control is Gen5DataShareControl { Gds: false })
        {
            var opcode = instruction.Opcode;
            if (opcode is "DsSwizzleB32" or "DsBpermuteB32" or "DsPermuteB32" or "DsNop")
            {
                return (false, false);
            }

            if (opcode.StartsWith("DsRead", StringComparison.Ordinal))
            {
                return (true, false);
            }

            if (opcode.StartsWith("DsWrite", StringComparison.Ordinal))
            {
                return (false, true);
            }

            return (true, true);
        }

        return sharedFlatPcs.Contains(instruction.Pc) ? (true, true) : (false, false);
    }

    private static int[][]? BuildSuccessors(IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        var indexByPc = new Dictionary<uint, int>(instructions.Count);
        for (var index = 0; index < instructions.Count; index++)
        {
            if (DynamicControlFlow.Contains(instructions[index].Opcode))
            {
                return null;
            }

            indexByPc[instructions[index].Pc] = index;
        }

        var last = instructions[^1];
        var endPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
        var successors = new int[instructions.Count][];
        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            var next = index + 1 < instructions.Count ? index + 1 : -1;
            if (Gen5IrBranchResolver.IsTerminator(instruction))
            {
                successors[index] = [];
                continue;
            }

            var unconditional = Gen5IrBranchResolver.IsUnconditionalBranch(instruction);
            if (!unconditional && !Gen5IrBranchResolver.Instance.IsConditional(instruction))
            {
                successors[index] = next < 0 ? [] : [next];
                continue;
            }

            if (!Gen5IrBranchResolver.Instance.TryGetBranchTarget(instruction, out var targetPc))
            {
                return null;
            }

            var targets = new List<int>(2);
            if (indexByPc.TryGetValue(targetPc, out var target))
            {
                targets.Add(target);
            }
            else if (targetPc < endPc)
            {
                return null;
            }

            if (!unconditional && next >= 0)
            {
                targets.Add(next);
            }

            successors[index] = [.. targets];
        }

        return successors;
    }

    private static bool[] ComputeSccLiveAfter(IReadOnlyList<Gen5ShaderInstruction> instructions, int[][] successors)
    {
        var liveBefore = new bool[instructions.Count];
        var liveAfter = new bool[instructions.Count];
        bool changed;
        do
        {
            changed = false;
            for (var index = instructions.Count - 1; index >= 0; index--)
            {
                var after = false;
                foreach (var successor in successors[index])
                {
                    after |= liveBefore[successor];
                }

                var instruction = instructions[index];
                var before = ReadsScc(instruction) || (!WritesScc(instruction) && after);
                if (after != liveAfter[index] || before != liveBefore[index])
                {
                    liveAfter[index] = after;
                    liveBefore[index] = before;
                    changed = true;
                }
            }
        }
        while (changed);

        return liveAfter;
    }

    private static bool ReadsScc(Gen5ShaderInstruction instruction)
    {
        if (SccReaders.Contains(instruction.Opcode))
        {
            return true;
        }

        foreach (var source in instruction.Sources)
        {
            if (source.Kind == Gen5OperandKind.EncodedConstant && source.Value == SccSource)
            {
                return true;
            }
        }

        return false;
    }

    private static bool WritesScc(Gen5ShaderInstruction instruction) =>
        instruction.Encoding == Gen5ShaderEncoding.Sopc ||
        instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal) ||
        instruction.Opcode.EndsWith("SaveexecB32", StringComparison.Ordinal) ||
        SccWriters.Contains(instruction.Opcode) ||
        BitwiseWithScc.Contains(instruction.Opcode);

    private static bool IsWideScalar(string opcode) =>
        opcode.StartsWith('S') &&
        (opcode.EndsWith("B64", StringComparison.Ordinal) ||
         opcode.EndsWith("U64", StringComparison.Ordinal) ||
         opcode.EndsWith("I64", StringComparison.Ordinal));

    private static UInt128 ReadRegisters(Gen5ShaderInstruction instruction, int sourceIndex, uint register)
    {
        var scalarOrdinal = 0;
        for (var index = 0; index < sourceIndex; index++)
        {
            if (instruction.Sources[index].Kind == Gen5OperandKind.ScalarRegister)
            {
                scalarOrdinal++;
            }
        }

        var count = instruction.Encoding switch
        {
            Gen5ShaderEncoding.Smem or Gen5ShaderEncoding.Smrd => scalarOrdinal != 0 ? 1u
                : instruction.Opcode.StartsWith("SLoad", StringComparison.Ordinal) ? 2u : 4u,
            Gen5ShaderEncoding.Mubuf or Gen5ShaderEncoding.Mtbuf => scalarOrdinal == 0 ? 4u : 1u,
            Gen5ShaderEncoding.Mimg => scalarOrdinal == 0 ? 8u : 4u,
            Gen5ShaderEncoding.Flat => 2u,
            _ => instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u,
        };
        return Range(register, count);
    }

    private static UInt128 Range(uint register, uint count)
    {
        UInt128 registers = 0;
        for (var offset = 0u; offset < count; offset++)
        {
            registers |= Bit(register + offset);
        }

        return registers;
    }

    private static UInt128 Expand(UInt128 registers) =>
        registers | ((registers & EvenRegisters) << 1) | ((registers & OddRegisters) >> 1);

    private static uint[] PairBases(UInt128 registers)
    {
        var bases = new List<uint>();
        for (uint register = 0; register < 128; register += 2)
        {
            if ((registers & Pair(register)) != 0)
            {
                bases.Add(register);
            }
        }

        return [.. bases];
    }

    private static UInt128 Pair(uint register) => Expand(Bit(register));

    private static UInt128 Bit(uint register) => register < 128 ? UInt128.One << (int)register : 0;
}

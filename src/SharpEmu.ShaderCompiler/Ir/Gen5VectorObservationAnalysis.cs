// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.ShaderCompiler.Ir;

public readonly record struct VectorRegisterSet(UInt128 Low, UInt128 High)
{
    public bool IsEmpty => Low == UInt128.Zero && High == UInt128.Zero;

    public bool Contains(uint register) =>
        register < 128
            ? ((Low >> (int)register) & UInt128.One) != 0
            : register < 256 && ((High >> (int)(register - 128)) & UInt128.One) != 0;

    public VectorRegisterSet With(uint register) =>
        register < 128 ? this with { Low = Low | (UInt128.One << (int)register) }
        : register < 256 ? this with { High = High | (UInt128.One << (int)(register - 128)) }
        : this;

    public VectorRegisterSet Union(VectorRegisterSet other) => new(Low | other.Low, High | other.High);

    public VectorRegisterSet Except(VectorRegisterSet other) => new(Low & ~other.Low, High & ~other.High);

    public int Count => PopCount(Low) + PopCount(High);

    private static int PopCount(UInt128 value) =>
        BitOperations.PopCount((ulong)value) + BitOperations.PopCount((ulong)(value >> 64));
}

public sealed class Gen5VectorObservationPlan(IReadOnlyDictionary<uint, VectorRegisterSet> unobserved)
{
    public VectorRegisterSet UnobservedWrites(uint pc) => unobserved.TryGetValue(pc, out var set) ? set : default;

    public int WriteCount => unobserved.Values.Sum(static set => set.Count);
}

public static class Gen5VectorObservationAnalysis
{
    private const uint ExecLow = 126;
    private const uint ExecHigh = 127;

    private enum ExecEffect : byte
    {
        None,
        Narrowing,
        Widening,
    }

    private readonly record struct Effect(
        VectorRegisterSet Reads,
        VectorRegisterSet CrossReads,
        VectorRegisterSet Writes,
        VectorRegisterSet FullWrites,
        ExecEffect Exec);

    public static Gen5VectorObservationPlan? Analyze(Gen5ShaderProgram program, bool wave32)
    {
        var instructions = program.Instructions;
        if (instructions.Count == 0)
        {
            return null;
        }

        var effects = new Effect[instructions.Count];
        for (var index = 0; index < instructions.Count; index++)
        {
            if (!TryDescribe(instructions[index], out effects[index]))
            {
                return null;
            }
        }

        if (BuildSuccessors(instructions) is not { } successors)
        {
            return null;
        }

        var execFull = Gen5ExecFullAnalysis.Analyze(program, wave32);
        var predecessors = new List<int>[instructions.Count];
        for (var index = 0; index < instructions.Count; index++)
        {
            predecessors[index] = [];
        }

        for (var index = 0; index < instructions.Count; index++)
        {
            foreach (var successor in successors[index])
            {
                predecessors[successor].Add(index);
            }
        }

        var liveIn = new VectorRegisterSet[instructions.Count];
        var observedIn = new VectorRegisterSet[instructions.Count];
        var queued = new bool[instructions.Count];
        var work = new Queue<int>();
        for (var index = instructions.Count - 1; index >= 0; index--)
        {
            work.Enqueue(index);
            queued[index] = true;
        }

        while (work.TryDequeue(out var index))
        {
            queued[index] = false;
            var (liveOut, observedOut) = Out(index, successors, liveIn, observedIn);
            var (live, observed) = Transfer(effects[index], execFull.Contains(instructions[index].Pc), liveOut, observedOut);
            if (live == liveIn[index] && observed == observedIn[index])
            {
                continue;
            }

            liveIn[index] = live;
            observedIn[index] = observed;
            foreach (var predecessor in predecessors[index])
            {
                if (!queued[predecessor])
                {
                    queued[predecessor] = true;
                    work.Enqueue(predecessor);
                }
            }
        }

        var unobserved = new Dictionary<uint, VectorRegisterSet>();
        for (var index = 0; index < instructions.Count; index++)
        {
            var writes = effects[index].Writes;
            if (writes.IsEmpty)
            {
                continue;
            }

            var (_, observedOut) = Out(index, successors, liveIn, observedIn);
            var dead = execFull.Contains(instructions[index].Pc) ? writes : writes.Except(observedOut);
            if (!dead.IsEmpty)
            {
                unobserved[instructions[index].Pc] = dead;
            }
        }

        return new Gen5VectorObservationPlan(unobserved);
    }

    private static (VectorRegisterSet Live, VectorRegisterSet Observed) Out(
        int index, int[][] successors, VectorRegisterSet[] liveIn, VectorRegisterSet[] observedIn)
    {
        var live = default(VectorRegisterSet);
        var observed = default(VectorRegisterSet);
        foreach (var successor in successors[index])
        {
            live = live.Union(liveIn[successor]);
            observed = observed.Union(observedIn[successor]);
        }

        return (live, observed);
    }

    private static (VectorRegisterSet Live, VectorRegisterSet Observed) Transfer(
        in Effect effect, bool execFull, VectorRegisterSet liveOut, VectorRegisterSet observedOut)
    {
        var observed = effect.Exec == ExecEffect.Widening ? observedOut.Union(liveOut) : observedOut;
        var kills = execFull ? effect.FullWrites : default;
        var live = liveOut.Except(kills).Union(effect.Reads).Union(effect.CrossReads);
        observed = observed.Except(kills).Union(effect.CrossReads);
        return (live, observed);
    }

    private static bool TryDescribe(Gen5ShaderInstruction instruction, out Effect effect)
    {
        effect = default;
        var opcode = instruction.Opcode;
        if (opcode.StartsWith("VMovrel", StringComparison.Ordinal) ||
            opcode.Contains("Raw", StringComparison.Ordinal) ||
            opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64" or "SCbranchJoin" or "SCbranchGFork" or "SCbranchIFork")
        {
            return false;
        }

        var reads = default(VectorRegisterSet);
        var cross = default(VectorRegisterSet);
        var writes = default(VectorRegisterSet);
        var full = default(VectorRegisterSet);
        var wideName = opcode.Contains("64", StringComparison.Ordinal);

        foreach (var source in instruction.Sources)
        {
            if (source.Kind != Gen5OperandKind.VectorRegister)
            {
                continue;
            }

            reads = reads.With(source.Value);
            if (wideName)
            {
                reads = reads.With(source.Value + 1);
            }
        }

        switch (instruction.Encoding)
        {
            case Gen5ShaderEncoding.Vop1:
            case Gen5ShaderEncoding.Vop2:
            case Gen5ShaderEncoding.Vop3:
            case Gen5ShaderEncoding.Vop3p:
            case Gen5ShaderEncoding.Vopc:
            case Gen5ShaderEncoding.Vintrp:
                DescribeVector(instruction, wideName, ref reads, ref cross, ref writes, ref full);
                break;
            case Gen5ShaderEncoding.Mubuf:
            case Gen5ShaderEncoding.Mtbuf:
                DescribeBuffer(instruction, ref reads, ref writes);
                break;
            case Gen5ShaderEncoding.Flat:
                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.VectorRegister)
                    {
                        writes = writes.With(destination.Value);
                        reads = reads.With(destination.Value);
                    }
                }

                break;
            case Gen5ShaderEncoding.Mimg:
                cross = cross.Union(reads);
                DescribeImage(instruction, ref reads, ref writes);
                break;
            case Gen5ShaderEncoding.Ds:
                if (opcode is "DsSwizzleB32" or "DsBpermuteB32" or "DsPermuteB32")
                {
                    cross = cross.Union(reads);
                }

                foreach (var destination in instruction.Destinations)
                {
                    if (destination.Kind == Gen5OperandKind.VectorRegister)
                    {
                        writes = writes.With(destination.Value);
                        reads = reads.With(destination.Value);
                    }
                }

                break;
        }

        effect = new Effect(reads, cross, writes, full, DescribeExec(instruction));
        return true;
    }

    private static void DescribeVector(Gen5ShaderInstruction instruction, bool wideName, ref VectorRegisterSet reads,
        ref VectorRegisterSet cross, ref VectorRegisterSet writes, ref VectorRegisterSet full)
    {
        var opcode = instruction.Opcode;
        if (instruction.Control is Gen5DppControl or Gen5Dpp8Control ||
            opcode is "VReadlaneB32" or "VReadfirstlaneB32" or "VPermlane16B32" or "VPermlanex16B32")
        {
            if (instruction.Sources.Count > 0 && instruction.Sources[0].Kind == Gen5OperandKind.VectorRegister)
            {
                cross = cross.With(instruction.Sources[0].Value);
                if (wideName)
                {
                    cross = cross.With(instruction.Sources[0].Value + 1);
                }
            }
        }

        if (instruction.Destinations.Count == 0 || instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister)
        {
            return;
        }

        var destination = instruction.Destinations[0].Value;
        writes = writes.With(destination);
        if (wideName)
        {
            writes = writes.With(destination + 1);
        }

        var partial =
            opcode.Contains("Mac", StringComparison.Ordinal) ||
            opcode.StartsWith("VDot", StringComparison.Ordinal) ||
            opcode is "VWritelaneB32" ||
            opcode.Contains("16", StringComparison.Ordinal) ||
            opcode.Contains("Pk", StringComparison.Ordinal) ||
            instruction.Encoding is Gen5ShaderEncoding.Vintrp ||
            instruction.Control is Gen5SdwaControl { DestinationSelect: not 6 } or Gen5SdwaControl { DestinationUnused: not 0 } ||
            instruction.Control is Gen5Vop3Control { OperandSelect: not 0 };
        if (partial)
        {
            reads = reads.With(destination);
            if (wideName)
            {
                reads = reads.With(destination + 1);
            }

            return;
        }

        full = full.With(destination);
    }

    private static void DescribeBuffer(Gen5ShaderInstruction instruction, ref VectorRegisterSet reads, ref VectorRegisterSet writes)
    {
        var opcode = instruction.Opcode;
        if (instruction.Control is Gen5BufferMemoryControl buffer)
        {
            reads = reads.With(buffer.VectorAddress).With(buffer.VectorAddress + 1);
        }

        var load = opcode.Contains("Load", StringComparison.Ordinal);
        foreach (var destination in instruction.Destinations)
        {
            if (destination.Kind != Gen5OperandKind.VectorRegister)
            {
                continue;
            }

            reads = reads.With(destination.Value);
            if (load || opcode.Contains("Atomic", StringComparison.Ordinal))
            {
                writes = writes.With(destination.Value);
            }
        }
    }

    private static void DescribeImage(Gen5ShaderInstruction instruction, ref VectorRegisterSet reads, ref VectorRegisterSet writes)
    {
        var (data, count) = instruction.Control switch
        {
            Gen5ImageControl image => (image.VectorData, 5u),
            Gen5RayIntersectControl ray => (ray.VectorData, Gen5RayIntersectControl.ResultDwords),
            _ => (0u, 0u),
        };
        var store = instruction.Opcode.StartsWith("ImageStore", StringComparison.Ordinal);
        for (var offset = 0u; offset < count; offset++)
        {
            reads = reads.With(data + offset);
            if (!store)
            {
                writes = writes.With(data + offset);
            }
        }
    }

    private static ExecEffect DescribeExec(Gen5ShaderInstruction instruction)
    {
        var opcode = instruction.Opcode;
        var scalarDestination = instruction.Control switch
        {
            Gen5Vop3Control control => control.ScalarDestination,
            Gen5SdwaControl control => control.ScalarDestination,
            _ => null,
        };
        if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
        {
            return ExecEffect.Narrowing;
        }

        if (opcode.StartsWith("VCmp", StringComparison.Ordinal))
        {
            return scalarDestination is ExecLow ? ExecEffect.Narrowing : ExecEffect.None;
        }

        var writesExec =
            scalarDestination is ExecLow or ExecHigh ||
            opcode.Contains("Saveexec", StringComparison.Ordinal) ||
            opcode.Contains("Wrexec", StringComparison.Ordinal) ||
            instruction.Destinations.Any(static destination =>
                destination is { Kind: Gen5OperandKind.ScalarRegister, Value: ExecLow or ExecHigh });
        if (!writesExec)
        {
            return ExecEffect.None;
        }

        if (opcode is "SAndSaveexecB64" or "SAndSaveexecB32" or "SAndn1SaveexecB64" or "SAndn1SaveexecB32")
        {
            return ExecEffect.Narrowing;
        }

        var toExec = instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: ExecLow }];
        var sources = instruction.Sources;
        if (toExec && opcode is "SAndB64" or "SAndB32" && sources.Count == 2 && (IsExec(sources[0]) || IsExec(sources[1])))
        {
            return ExecEffect.Narrowing;
        }

        if (toExec && opcode is "SAndn2B64" or "SAndn2B32" && sources.Count == 2 && IsExec(sources[0]))
        {
            return ExecEffect.Narrowing;
        }

        return ExecEffect.Widening;
    }

    private static bool IsExec(Gen5Operand operand) => operand is { Kind: Gen5OperandKind.ScalarRegister, Value: ExecLow };

    private static int[][]? BuildSuccessors(IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        var indexByPc = new Dictionary<uint, int>(instructions.Count);
        for (var index = 0; index < instructions.Count; index++)
        {
            indexByPc[instructions[index].Pc] = index;
        }

        var last = instructions[^1];
        var endPc = last.Pc + (uint)(last.Words.Count * sizeof(uint));
        var successors = new int[instructions.Count][];
        var resolver = Gen5IrBranchResolver.Instance;
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
            if (!unconditional && !resolver.IsConditional(instruction))
            {
                successors[index] = next < 0 ? [] : [next];
                continue;
            }

            if (!resolver.TryGetBranchTarget(instruction, out var targetPc))
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

            if (!unconditional && next >= 0 && !targets.Contains(next))
            {
                targets.Add(next);
            }

            successors[index] = [.. targets];
        }

        return successors;
    }
}

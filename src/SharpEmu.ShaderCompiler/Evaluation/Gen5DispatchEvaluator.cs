// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Evaluation;

public enum Gen5EvaluationRefusal
{
    None,
    NotEvaluable,
    Indirect,
    Scratch,
    LocalDataShare,
    RoundingMode,
    Threads,
    Budget,
    InputOwned,
    OutOfBounds,
    Descriptor,
    Unaligned,
    ReadAfterWrite,
    WriteConflict,
    UndefinedRegister,
    NoActiveLane,
    UndefinedBehaviour,
    FloatDenormal,
    FloatNaN,
    FloatSpecial,
    FloatPrecision,
    MaskRegister,
    OutputTooLarge,
    WriteRegions,
    NoWrites,
    ThreadLimit,
    LaneLimit,
    Count,
}

public static class Gen5WaveState
{
    public const uint Vcc = 106;
    public const uint Null = 125;
    public const uint Exec = 126;
}

public sealed class Gen5DispatchRequest
{
    public required uint[] UserData { get; init; }
    public Gen5ComputeSystemRegisters SystemRegisters { get; init; }
    public uint ThreadsX { get; init; } = 1;
    public uint ThreadsY { get; init; } = 1;
    public uint ThreadsZ { get; init; } = 1;
    public uint GroupsX { get; init; } = 1;
    public uint GroupsY { get; init; } = 1;
    public uint GroupsZ { get; init; } = 1;
    public bool LimitsThreads { get; init; }
    public uint ThreadLimitX { get; init; } = uint.MaxValue;
    public uint ThreadLimitY { get; init; } = uint.MaxValue;
    public uint ThreadLimitZ { get; init; } = uint.MaxValue;
    public uint WaveSize { get; init; } = 64;
    public bool PreservesFloatSpecials { get; init; } = true;
    public long LaneOperationBudget { get; init; } = 1024L * 1024;
    public long ThreadBudget { get; init; } = 1024 * 1024;
    public int OutputDwordBudget { get; init; } = Gen5EvaluationWrites.MaxOutputDwords;
}

public sealed class Gen5DispatchPrediction
{
    internal Gen5DispatchPrediction(ulong address, uint[] words, int count, int writtenCount)
    {
        Address = address;
        Buffer = words;
        Count = count;
        WrittenCount = writtenCount;
    }

    public ulong Address { get; }

    public uint[] Buffer { get; }

    public int Count { get; }

    public int WrittenCount { get; }

    public bool Dense => WrittenCount == Count;

    public bool Pooled => Gen5EvaluationWrites.IsPooled(Count);

    public ReadOnlySpan<uint> Words => Buffer.AsSpan(0, Count);
}

public readonly record struct Gen5DispatchOutcome(
    Gen5EvaluationRefusal Refusal,
    Gen5DispatchPrediction? Prediction,
    long LaneOperations,
    int ProbeRuns,
    Gen5MemoLookup Memo = Gen5MemoLookup.None);

public enum Gen5FloatVariant : byte
{
    Nearest,
    Down,
    Up,
}

public static class Gen5DispatchEvaluator
{
    private sealed class Session
    {
        public readonly Gen5EvaluationReads Reads = new();
        public readonly Gen5WaveInterpreter Interpreter = new();
        public readonly Gen5EvaluationWrites Baseline = new();
        public readonly Gen5EvaluationWrites Probe = new();
    }

    [ThreadStatic]
    private static Session? _session;

    public static Gen5DispatchOutcome Evaluate(Gen5EvaluableProgram program, Gen5DispatchRequest request, IGen5EvaluationMemory memory) =>
        Evaluate(program, request, memory, out _);

    // The reads stay valid until the next evaluation on this thread.
    internal static Gen5DispatchOutcome Evaluate(Gen5EvaluableProgram program, Gen5DispatchRequest request, IGen5EvaluationMemory memory,
        out Gen5EvaluationReads reads)
    {
        var session = _session ??= new Session();
        reads = session.Reads;
        var precheck = Precheck(program, request, out _);
        if (precheck != Gen5EvaluationRefusal.None)
        {
            return new Gen5DispatchOutcome(precheck, null, 0, 0);
        }

        reads.Reset(memory);
        var interpreter = session.Interpreter;
        interpreter.Reset(program, request, reads);
        var baseline = session.Baseline.Reset(request.OutputDwordBudget);
        var refusal = interpreter.Run(baseline, Gen5FloatVariant.Nearest);
        var probeRuns = 0;
        if (refusal == Gen5EvaluationRefusal.None && interpreter.SawInexactApproximation)
        {
            foreach (var variant in (ReadOnlySpan<Gen5FloatVariant>)[Gen5FloatVariant.Down, Gen5FloatVariant.Up])
            {
                probeRuns++;
                var probe = session.Probe.Reset(request.OutputDwordBudget);
                refusal = interpreter.Run(probe, variant);
                if (refusal == Gen5EvaluationRefusal.None && !probe.SameAs(baseline))
                {
                    refusal = Gen5EvaluationRefusal.FloatPrecision;
                }

                if (refusal != Gen5EvaluationRefusal.None)
                {
                    break;
                }
            }
        }

        if (refusal != Gen5EvaluationRefusal.None)
        {
            return new Gen5DispatchOutcome(refusal, null, interpreter.LaneOperations, probeRuns);
        }

        refusal = baseline.TryBuild(reads, out var address, out var words, out var count);
        return refusal == Gen5EvaluationRefusal.None
            ? new Gen5DispatchOutcome(refusal, new Gen5DispatchPrediction(address, words, count, baseline.Count), interpreter.LaneOperations, probeRuns)
            : new Gen5DispatchOutcome(refusal, null, interpreter.LaneOperations, probeRuns);
    }

    // Refuses from the dispatch shape and the static instruction count alone, before any lane runs.
    public static Gen5EvaluationRefusal Precheck(Gen5EvaluableProgram program, Gen5DispatchRequest request, out ulong threads)
    {
        var groupThreads = (ulong)request.ThreadsX * request.ThreadsY * request.ThreadsZ;
        var groups = (ulong)request.GroupsX * request.GroupsY * request.GroupsZ;
        threads = groupThreads * groups;
        if (!program.IsEvaluable)
        {
            return Gen5EvaluationRefusal.NotEvaluable;
        }

        if (request.WaveSize is not (32 or 64) || groupThreads == 0 || groupThreads > 1024 || threads == 0)
        {
            return Gen5EvaluationRefusal.Threads;
        }

        if (threads > (ulong)request.ThreadBudget)
        {
            return Gen5EvaluationRefusal.ThreadLimit;
        }

        var lanes = groups * ((groupThreads + request.WaveSize - 1) / request.WaveSize) * request.WaveSize;
        return lanes * (ulong)program.Instructions.Length > (ulong)request.LaneOperationBudget
            ? Gen5EvaluationRefusal.LaneLimit
            : Gen5EvaluationRefusal.None;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Evaluation;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Small buffer-only dispatches run on the CPU as well, so their writes are known before the GPU finishes them.
internal sealed partial class ShaderPipelineCache
{
    internal const int MuteAfterRefusals = 16;
    internal const int MutedRecheckPeriod = 256;

    private static readonly ThreadCounter _kernelsIneligible = new();
    private static readonly ThreadCounter _kernelsEvaluated = new();
    private static readonly ThreadCounter _kernelsPredicted = new();
    private static readonly ThreadCounter _kernelPredictedBytes = new();
    private static readonly ThreadCounter _kernelsRefused = new();
    private static readonly ThreadCounter _kernelLaneOperations = new();
    private static readonly ThreadCounter _kernelProbeRuns = new();
    private static readonly ThreadCounter _kernelEvaluationTicks = new();
    private static readonly ThreadCounter _kernelPredictionsRead = new();
    private static readonly ThreadCounter _kernelProgramsMuted = new();
    private static readonly ThreadCounter _kernelProgramsUnmuted = new();
    private static readonly ThreadCounter _kernelMutedSkips = new();
    private static readonly ThreadCounter _kernelMutedRechecks = new();
    private static readonly ThreadCounter _kernelsConsumed = new();
    private static readonly ThreadCounter _kernelConsumedBytes = new();
    private static readonly ThreadCounter[] _kernelConsumeRefusals = NewCounters((int)KernelWriteRefusal.Count);
    private static readonly ThreadCounter[] _kernelRefusals = NewCounters((int)Gen5EvaluationRefusal.Count);
    internal static readonly ThreadCounter KernelWritesToCpu = new();
    internal static readonly ThreadCounter KernelWritesOverGpu = new();
    internal static readonly ThreadCounter KernelWriteBytesOverGpu = new();
    private static readonly ThreadCounter[] _kernelMemoLookups = NewCounters(4);
    private static long _kernelMemoBytes;
    private static readonly ConditionalWeakTable<Gen5ShaderProgram, KernelDemand> _kernelDemands = new();
    private readonly Gen5DispatchMemo _kernelMemo = new();

    private static ThreadCounter[] NewCounters(int count)
    {
        var counters = new ThreadCounter[count];
        for (var index = 0; index < count; index++)
        {
            counters[index] = new ThreadCounter();
        }

        return counters;
    }

    public static string TakeCpuKernelReport()
    {
        var milliseconds = _kernelEvaluationTicks.Take() * 1000.0 / Stopwatch.Frequency;
        return FormattableString.Invariant($"[PERF][CPU_KERNEL] evaluated={_kernelsEvaluated.Take()} predicted={_kernelsPredicted.Take()} ") +
               FormattableString.Invariant($"memo_hits={_kernelMemoLookups[(int)Gen5MemoLookup.Hit].Take()} ") +
               FormattableString.Invariant($"memo_misses=absent:{_kernelMemoLookups[(int)Gen5MemoLookup.Absent].Take()},") +
               FormattableString.Invariant($"changed:{_kernelMemoLookups[(int)Gen5MemoLookup.Changed].Take()} memo_bytes={Volatile.Read(ref _kernelMemoBytes)} ") +
               FormattableString.Invariant($"applied=cpu:{KernelWritesToCpu.Take()},over_gpu:{KernelWritesOverGpu.Take()},") +
               FormattableString.Invariant($"over_gpu_bytes:{KernelWriteBytesOverGpu.Take()} ") +
               FormattableString.Invariant($"predicted_bytes={_kernelPredictedBytes.Take()} refused={_kernelsRefused.Take()} reasons=") + TakeKernelRefusals() +
               FormattableString.Invariant($" consumed={_kernelsConsumed.Take()} consumed_bytes={_kernelConsumedBytes.Take()} consume_refused=") + TakeConsumeRefusals() +
               FormattableString.Invariant($" ineligible={_kernelsIneligible.Take()} lane_ops={_kernelLaneOperations.Take()} ") +
               FormattableString.Invariant($"probe_runs={_kernelProbeRuns.Take()} eval_ms={milliseconds:F2}") +
               FormattableString.Invariant($" demand=read:{_kernelPredictionsRead.Take()},muted:{_kernelProgramsMuted.Take()},") +
               FormattableString.Invariant($"unmuted:{_kernelProgramsUnmuted.Take()},muted_skips:{_kernelMutedSkips.Take()},rechecks:{_kernelMutedRechecks.Take()}");
    }

    private static string TakeConsumeRefusals()
    {
        var reasons = new List<string>();
        for (var index = 1; index < _kernelConsumeRefusals.Length; index++)
        {
            var count = _kernelConsumeRefusals[index].Take();
            if (count != 0)
            {
                reasons.Add(FormattableString.Invariant($"{(KernelWriteRefusal)index}:{count}"));
            }
        }

        return reasons.Count == 0 ? "none" : string.Join(",", reasons);
    }

    private static string TakeKernelRefusals()
    {
        var reasons = new List<string>();
        for (var index = 1; index < _kernelRefusals.Length; index++)
        {
            var count = _kernelRefusals[index].Take();
            if (count != 0)
            {
                reasons.Add(FormattableString.Invariant($"{(Gen5EvaluationRefusal)index}:{count}"));
            }
        }

        return reasons.Count == 0 ? "none" : string.Join(",", reasons);
    }

    private static KernelDemand? KernelFor(Gen5ShaderProgram program)
    {
        var evaluable = Gen5EvaluableProgram.Get(program);
        if (!evaluable.IsEvaluable)
        {
            _kernelsIneligible.Increment();
            return null;
        }

        return _kernelDemands.GetValue(program, _ => new KernelDemand(evaluable));
    }

    private ComputeProgram WithPrediction(
        ComputeProgram program,
        KernelDemand? kernel,
        ShaderSource? source,
        Gen5ComputeSystemRegisters systemRegisters,
        ComputeStageRegisters compute,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ,
        bool indirect)
    {
        if (kernel is null || source is null || !program.Available || program.Consumed ||
            source.Registered.Frontend?.NormalizeBufferDescriptor is not null)
        {
            return program;
        }

        var prediction = PredictDispatch(kernel, source, systemRegisters, compute, program.Input, dimensionX, dimensionY, dimensionZ, indirect);
        if (prediction is null)
        {
            return program;
        }

        if (TryConsume(prediction, source, compute))
        {
            return new ComputeProgram { Program = program.Program, Input = program.Input, Consumed = true };
        }

        var knownWrite = new KnownGpuWrite(prediction.Address, prediction.Buffer, prediction.Count, prediction.Pooled) { Demand = kernel };
        return new ComputeProgram { Program = program.Program, Input = program.Input, KnownWrite = knownWrite };
    }

    // A fully evaluated dispatch with one dense write lands as a store at its stream position and never reaches the GPU.
    private bool TryConsume(Gen5DispatchPrediction prediction, ShaderSource source, ComputeStageRegisters compute)
    {
        var refusal = prediction.Dense ? _host.TryPostKernelWrite(prediction.Address, prediction.Words) : KernelWriteRefusal.Sparse;
        if (refusal != KernelWriteRefusal.None)
        {
            _kernelConsumeRefusals[(int)refusal].Increment();
            return false;
        }

        var bytes = (long)prediction.Count * sizeof(uint);
        _kernelsConsumed.Increment();
        _kernelConsumedBytes.Add(bytes);
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(FormattableString.Invariant($"Consumed a compute dispatch on the CPU: shader=0x{compute.Address:X16} hash=0x{source.Hash:X16} ") +
                              FormattableString.Invariant($"dst=0x{prediction.Address:X16} bytes={bytes}"));
        }

        if (prediction.Pooled)
        {
            System.Buffers.ArrayPool<uint>.Shared.Return(prediction.Buffer);
        }

        return true;
    }

    private Gen5DispatchPrediction? PredictDispatch(
        KernelDemand kernel,
        ShaderSource source,
        Gen5ComputeSystemRegisters systemRegisters,
        ComputeStageRegisters compute,
        ComputeInputInfo input,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ,
        bool indirect)
    {
        if (!kernel.ShouldEvaluate())
        {
            _kernelMutedSkips.Increment();
            return null;
        }

        var refusal = indirect ? Gen5EvaluationRefusal.Indirect
            : input.ScratchDwords != 0 ? Gen5EvaluationRefusal.Scratch
            : input.LocalDataShareDwords != 0 ? Gen5EvaluationRefusal.LocalDataShare
            : (compute.FloatMode & 0xF) != 0 ? Gen5EvaluationRefusal.RoundingMode
            : input.ThreadsX == 0 || input.ThreadsY == 0 || input.ThreadsZ == 0 ? Gen5EvaluationRefusal.Threads
            : Gen5EvaluationRefusal.None;
        if (refusal != Gen5EvaluationRefusal.None)
        {
            return Refused(refusal);
        }

        var threadDimensions = input.DispatchThreadDimensions;
        var request = new Gen5DispatchRequest
        {
            UserData = source.UserData,
            SystemRegisters = systemRegisters,
            ThreadsX = input.ThreadsX,
            ThreadsY = input.ThreadsY,
            ThreadsZ = input.ThreadsZ,
            GroupsX = threadDimensions ? RenderExecutor.GroupsFromThreads(dimensionX, input.ThreadsX) : dimensionX,
            GroupsY = threadDimensions ? RenderExecutor.GroupsFromThreads(dimensionY, input.ThreadsY) : dimensionY,
            GroupsZ = threadDimensions ? RenderExecutor.GroupsFromThreads(dimensionZ, input.ThreadsZ) : dimensionZ,
            LimitsThreads = threadDimensions,
            ThreadLimitX = threadDimensions ? dimensionX : uint.MaxValue,
            ThreadLimitY = threadDimensions ? dimensionY : uint.MaxValue,
            ThreadLimitZ = threadDimensions ? dimensionZ : uint.MaxValue,
            WaveSize = input.WaveSize,
            PreservesFloatSpecials = _host.ShaderSignedZeroInfNanPreserveFloat32Supported,
        };

        refusal = Gen5DispatchEvaluator.Precheck(kernel.Evaluable, request, out _);
        if (refusal != Gen5EvaluationRefusal.None)
        {
            return Refused(refusal);
        }

        _kernelsEvaluated.Increment();
        var started = Stopwatch.GetTimestamp();
        var outcome = _kernelMemo.Evaluate(kernel.Evaluable, request, new PeekMemory(_host));
        _kernelEvaluationTicks.Add(Stopwatch.GetTimestamp() - started);
        _kernelMemoLookups[(int)outcome.Memo].Increment();
        Volatile.Write(ref _kernelMemoBytes, _kernelMemo.Bytes);
        _kernelLaneOperations.Add(outcome.LaneOperations);
        _kernelProbeRuns.Add(outcome.ProbeRuns);
        if (outcome.Prediction is not { } prediction)
        {
            kernel.NoteRefused();
            return Refused(outcome.Refusal);
        }

        var bytes = (long)prediction.Count * sizeof(uint);
        _kernelsPredicted.Increment();
        _kernelPredictedBytes.Add(bytes);
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write(FormattableString.Invariant($"Predicted a compute dispatch on the CPU: shader=0x{compute.Address:X16} hash=0x{source.Hash:X16} ") +
                              FormattableString.Invariant($"dst=0x{prediction.Address:X16} bytes={bytes} lane_ops={outcome.LaneOperations}"));
        }

        return prediction;
    }

    private static Gen5DispatchPrediction? Refused(Gen5EvaluationRefusal refusal)
    {
        _kernelsRefused.Increment();
        _kernelRefusals[(int)refusal].Increment();
        return null;
    }

    // A program whose evaluations keep being refused is evaluated only now and then, until one of its predictions is read.
    internal sealed class KernelDemand(Gen5EvaluableProgram evaluable) : IKnownWriteDemand
    {
        private int _refusalRun;
        private int _mutedDispatches;

        public Gen5EvaluableProgram Evaluable { get; } = evaluable;

        public bool ShouldEvaluate()
        {
            if (Volatile.Read(ref _refusalRun) < MuteAfterRefusals)
            {
                return true;
            }

            if (Interlocked.Increment(ref _mutedDispatches) % MutedRecheckPeriod != 0)
            {
                return false;
            }

            _kernelMutedRechecks.Increment();
            return true;
        }

        public void NoteRead()
        {
            _kernelPredictionsRead.Increment();
            if (Interlocked.Exchange(ref _refusalRun, 0) >= MuteAfterRefusals)
            {
                _kernelProgramsUnmuted.Increment();
            }
        }

        public void NoteRefused()
        {
            if (Interlocked.Increment(ref _refusalRun) == MuteAfterRefusals)
            {
                _kernelProgramsMuted.Increment();
            }
        }
    }

    private sealed class PeekMemory(IShaderPipelineHost host) : IGen5EvaluationMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) => host.TryPeekResidentGuestBytes(address, destination);
    }
}

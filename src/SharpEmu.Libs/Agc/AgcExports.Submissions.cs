// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial executes submitted command buffers and their ordered memory side effects.
public static partial class AgcExports
{
    // SharpEmu still has GPU label consumers that read guest memory. Mirror
    // the value after the producer completes. A value of 0 keeps the label in
    // the virtual GL2 view for diagnostics only.
    private static readonly bool _gpuLabelHostMirrorEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_LABEL_HOST_MIRROR"),
        "0",
        StringComparison.Ordinal);
    private static readonly bool _gpuLabelHostMirrorRetirementEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_LABEL_HOST_MIRROR_RETIRE"),
        "0",
        StringComparison.Ordinal);

    private static readonly HashSet<uint> _tracedDcbSizes = new();

    private static long _labelProducerSequence;
    private static readonly object _labelProducerGate = new();
    private static readonly List<LabelProducerTrace> _labelProducers = [];
    private const int LabelProducerSoftBound = 4096;
    // Raised when a compaction pass frees nothing because every record is still
    // active, so registration does not rescan the whole list on every add while
    // a queue is suspended. Reset once compaction can make progress again.
    private static int _labelProducerCompactionBound = LabelProducerSoftBound;
    private static readonly HashSet<(object Memory, ulong Address)>
        _tracedProducerlessWaits = new();

    private static long _standardDmaTraceCount;

    private sealed class LabelProducerTrace
    {
        public long Sequence;
        public required object Memory;
        public ulong Address;
        public ulong Length;
        public ulong PacketAddress;
        public ulong SubmissionId;
        public required string QueueName;
        public required string DebugName;
        public bool Completed;
    }

    [SysAbiExport(
        Nid = "UglJIZjGssM",
        ExportName = "sceAgcDriverSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitDcb(CpuContext ctx)
    {
        var profileEnabled = DcbSubmissionProfile.Enabled;
        var callStartTicks = profileEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        Interlocked.Increment(ref _dcbSubmitCount);
        Volatile.Write(ref _lastDcbSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            TraceAgc($"agc.driver_submit_dcb_rejected packet=0x{packetAddress:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }

            // Unconditional (unlike tracePackets above, not deduped by dwordCount):
            // every DriverSubmitDcb call's target address and size, so submission
            // history can be reconstructed even when most sizes repeat.
            TraceAgc($"agc.driver_submit_dcb_call addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        TraceAgc(
            $"agc.driver_submit_dcb packet=0x{packetAddress:X16} addr=0x{commandAddress:X16} " +
            $"dwords={dwordCount} end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        GuestGpu.Current.AttachGuestMemory(ctx.Memory);
        RecordGameSubmittedRange(commandAddress, dwordCount);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        var setupEndTicks = profileEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        var submittedIndexSnapshots = CaptureSubmittedIndexPackets(
            ctx,
            commandAddress,
            dwordCount,
            gpuState.Graphics.IndexSize,
            out var submittedVertexSnapshots);
        var snapshotEndTicks = profileEnabled
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        var lockStartTicks = snapshotEndTicks;
        var lockAcquiredTicks = 0L;
        var queuePumpEndTicks = 0L;
        var drainEndTicks = 0L;
        lock (gpuState.Gate)
        {
            if (profileEnabled)
            {
                lockAcquiredTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            gpuState.Graphics.QueueName = "dcb.graphics";
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                gpuState.Graphics,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets,
                submittedIndexSnapshots,
                submittedVertexSnapshots);
            if (profileEnabled)
            {
                queuePumpEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            DrainResumableDcbs(ctx, gpuState, tracePackets);
            if (profileEnabled)
            {
                drainEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            }
        }

        if (profileEnabled)
        {
            DcbSubmissionProfile.Record(
                dwordCount,
                setupEndTicks - callStartTicks,
                snapshotEndTicks - setupEndTicks,
                lockAcquiredTicks - lockStartTicks,
                queuePumpEndTicks - lockAcquiredTicks,
                drainEndTicks - queuePumpEndTicks,
                drainEndTicks - callStartTicks);
        }

        // No orphan-preamble drain here — this runs on a native guest worker
        // thread, where long managed work fail-fasts the runtime. The GPU
        // wait monitor drains this instead.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gSRnr79F8tQ",
        ExportName = "sceAgcDriverSubmitAcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitAcb(CpuContext ctx)
    {
        var ownerHandle = (uint)ctx[CpuRegister.Rdi];
        var packetAddress = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }

            // Unconditional (unlike tracePackets above, not deduped by dwordCount):
            // every DriverSubmitAcb call's target address and size.
            TraceAgc(
                $"agc.driver_submit_acb_call owner={ownerHandle} addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        TraceAgc(
            $"agc.driver_submit_acb owner={ownerHandle} packet=0x{packetAddress:X16} " +
            $"addr=0x{commandAddress:X16} dwords={dwordCount} " +
            $"end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        GuestGpu.Current.AttachGuestMemory(ctx.Memory);
        RecordGameSubmittedRange(commandAddress, dwordCount);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            if (!gpuState.ComputeQueues.TryGetValue(ownerHandle, out var queueState))
            {
                queueState = new SubmittedDcbState();
                gpuState.ComputeQueues.Add(ownerHandle, queueState);
            }

            queueState.QueueName = $"acb.compute[{ownerHandle}]";
            queueState.CompletionEventId = ownerHandle;
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                queueState,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets,
                indexSnapshots: null,
                vertexSnapshots: null);
            DrainResumableDcbs(ctx, gpuState, tracePackets);
        }

        // See DriverSubmitDcb: orphan drains run only on the wait monitor
        // thread, never in this guest-thread import window.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "h9z6+0hEydk",
        ExportName = "sceAgcSuspendPoint",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SuspendPoint(CpuContext ctx)
    {
        TraceAgc("agc.suspend_point");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "qj7QZpgr9Uw",
        ExportName = "sceAgcUnknownQj7QZpgr9Uw",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownQj7QZpgr9Uw(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 1, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, 0x8000_0000))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.unknown_qj7 buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"arg1=0x{ctx[CpuRegister.Rsi]:X16} arg2=0x{ctx[CpuRegister.Rdx]:X16}");
        return ReturnPointer(ctx, commandAddress);
    }
    #pragma warning restore SHEM006

    private static void EnqueueSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        ulong submissionId,
        bool tracePackets,
        Dictionary<ulong, SubmittedIndexSnapshot>? indexSnapshots,
        Dictionary<ulong, SubmittedVertexSnapshot>? vertexSnapshots)
    {
        if (state.IsFaulted)
        {
            ReportFaultedSubmissionRejected(state, submissionId);
            return;
        }

        var publicationGeneration = GpuWaitRegistry.BeginSubmission(ctx.Memory);
        state.PendingSubmissions.Enqueue(new SubmittedDcbState.PendingSubmission(
            commandAddress,
            dwordCount,
            submissionId,
            tracePackets,
            indexSnapshots,
            vertexSnapshots,
            publicationGeneration));
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void ReportFaultedSubmissionRejected(
        SubmittedDcbState state,
        ulong submissionId)
    {
        if (state.FaultedSubmissionReported)
        {
            return;
        }

        state.FaultedSubmissionReported = true;
        Console.Error.WriteLine(
            $"[LOADER][WARN] agc.queue_submit_rejected queue={state.QueueName} " +
            $"submission={submissionId} reason='{state.FaultReason}'");
    }

    private static void PumpSubmittedQueue(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state)
    {
        if (state.IsFaulted)
        {
            return;
        }

        if (state.IsSuspended)
        {
            // An explicit new submission supersedes a ring-tail park — the
            // game moved to a fresh ring, so abandon the park.
            if (state.RingTailParkAddress == 0 ||
                state.PendingSubmissions.Count == 0 ||
                !GpuWaitRegistry.TryRemoveByState(state, state.RingTailParkAddress))
            {
                return;
            }

            TraceAgc(
                $"agc.dcb.ring_tail_superseded addr=0x{state.RingTailParkAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId}");
            // Keeps its full recorded extent so the arena sweep doesn't
            // double-run the tail once the game's own re-parse reaches it.
            state.RingTailParkAddress = 0;
            state.IsSuspended = false;
            GpuWaitRegistry.EndSubmission(
                ctx.Memory,
                state.ActiveSubmissionPublicationGeneration);
            state.ActiveSubmissionPublicationGeneration = 0;
            state.HasActiveSubmission = false;
            state.ActiveIndexSnapshots = null;
            state.ActiveVertexSnapshots = null;
            NotifySubmittedDcbCompleted(gpuState, state, state.ActiveSubmissionId);
        }

        while (!state.HasActiveSubmission &&
               state.PendingSubmissions.TryDequeue(out var submission))
        {
            state.HasActiveSubmission = true;
            state.ActiveSubmissionId = submission.SubmissionId;
            state.ActiveCompletionState = new SubmittedCompletionState();
            state.ActiveSubmissionPublicationGeneration =
                submission.PublicationGeneration;
            state.ActiveIndexSnapshots = submission.IndexSnapshots;
            state.ActiveVertexSnapshots = submission.VertexSnapshots;
            state.RingChunkBase = state.IsForceSubmittedRing ? 0 : submission.CommandAddress;
            state.FollowedChunkAdvance = false;
            state.IndirectCallReturn = null;
            var isSuspended = ParseSubmittedDcb(
                ctx,
                gpuState,
                state,
                submission.CommandAddress,
                submission.DwordCount,
                submission.TracePackets);
            if (state.IsFaulted)
            {
                return;
            }

            state.IsSuspended = isSuspended;
            if (state.IsSuspended)
            {
                return;
            }

            state.HasActiveSubmission = false;
            GpuWaitRegistry.EndSubmission(
                ctx.Memory,
                state.ActiveSubmissionPublicationGeneration);
            state.ActiveSubmissionPublicationGeneration = 0;
            state.ActiveIndexSnapshots = null;
            state.ActiveVertexSnapshots = null;
            NotifySubmittedDcbCompleted(gpuState, state, submission.SubmissionId);
        }
    }

    private static void ApplySubmittedDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool compactLayout,
        bool tracePacket)
    {
        var byteCountOffset = compactLayout ? 20UL : 12UL;
        var destinationOffset = compactLayout ? 4UL : 16UL;
        var sourceOffset = compactLayout ? 12UL : 24UL;
        var selectorOffset = compactLayout ? 24UL : 4UL;
        if (!TryReadUInt32(ctx, packetAddress + byteCountOffset, out var byteCount) ||
            !TryReadUInt64(ctx, packetAddress + destinationOffset, out var destinationAddress) ||
            !TryReadUInt64(ctx, packetAddress + sourceOffset, out var sourceAddress) ||
            !TryReadUInt32(ctx, packetAddress + selectorOffset, out var selectorControl))
        {
            return;
        }

        var immediateFill = IsWrappedDmaGuestMemoryFill(compactLayout, selectorControl);
        if (immediateFill)
        {
            RegisterActiveHtile(gpuState, state.CxRegisters);
            MarkHtileMetadataClear(
                gpuState,
                destinationAddress,
                "agc-dma-fill");
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
                var copied =
                    byteCount != 0 &&
                    byteCount <= 256u * 1024u * 1024u &&
                    destinationAddress != 0 &&
                    (immediateFill
                        ? TryFillGuestMemory(ctx, (uint)sourceAddress, destinationAddress, byteCount)
                        : sourceAddress != 0 &&
                          TryCopyGuestMemory(ctx, sourceAddress, destinationAddress, byteCount));
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(
                        ctx,
                        destinationAddress,
                        byteCount,
                        immediateFill ? (uint)sourceAddress : null);
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.dma_data dst=0x{destinationAddress:X16} " +
                        $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                        $"fill={immediateFill} copied={copied}");
                }
            },
            $"agc_dma_data dst=0x{destinationAddress:X16} bytes={byteCount}",
            packetAddress,
            destinationAddress,
            byteCount,
            deferLabelCompletion: true);
    }

    internal static bool IsWrappedDmaGuestMemoryFill(
        bool compactLayout,
        uint selectorControl)
    {
        var sourceSelector = compactLayout
            ? selectorControl & 0xFFu
            : (selectorControl >> 16) & 0xFFu;
        var destinationSelector = compactLayout
            ? (selectorControl >> 8) & 0xFFu
            : selectorControl & 0xFFu;
        return sourceSelector == 2 && destinationSelector is 0 or 3;
    }

    private static bool PacketRequiresPendingAcquireFlush(
        uint op,
        uint register,
        uint length) =>
        op is ItDispatchDirect or ItDispatchIndirect ||
        op is ItDrawIndirect or
            ItDrawIndexIndirect or
            ItDrawIndexIndirectMulti or
            ItDrawIndex2 or
            ItDrawIndexAuto or
            ItDrawIndexMultiAuto or
            ItDrawIndexOffset2 ||
        op is ItAtomicMem or ItMemSemaphore or ItCopyData or ItCondWrite ||
        op == ItDmaData ||
        (op == ItNop && register == RDmaData && length >= 7) ||
        (op == ItNop && register == RFlip && length >= 6) ||
        (op == ItNop && register == RDrawIndexAuto && length >= 2) ||
        (op == ItNop && register == RWaitFlipDone && length >= 3);

    private static void SubmitOrderedGpuSideEffect(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        Action action,
        string debugName,
        ulong packetAddress,
        ulong producerAddress = 0,
        ulong producerLength = 0,
        bool deferLabelCompletion = false)
    {
        var producer = RegisterLabelProducer(
            ctx.Memory,
            state,
            packetAddress,
            producerAddress,
            producerLength,
            debugName);

        void CompleteAndWake()
        {
            CompleteLabelProducer(producer);
            lock (gpuState.WaitMonitorSignalGate)
            {
                gpuState.WaitMonitorSignalVersion++;
                Monitor.Pulse(gpuState.WaitMonitorSignalGate);
            }

            // Resuming a DCB can enqueue and wait for another dispatch, never
            // reentrantly on the Vulkan render thread. Drains are coalesced —
            // hundreds of completions per frame each queueing an independent
            // full drain turns the shared Gate into a thundering herd — via
            // one request flag and a single re-looping worker.
            RequestResumableDcbDrain(ctx, gpuState);
        }

        void ApplyAndQueueCompletion()
        {
            action();
            // No label producer → nothing to wake; skip the follow-up enqueue
            // that was doubling OrderedGuestAction traffic during load.
            if (producer is null)
            {
                CompleteAndWake();
                return;
            }

            // Release/write-data paths cannot enqueue Vulkan image mirrors, so
            // complete the producer in the same ordered action. DMA can enqueue
            // a mirror while applying; defer completion until after those
            // follow-ups so waiters see the mirrored image.
            if (!deferLabelCompletion)
            {
                CompleteAndWake();
                return;
            }

            if (GuestGpu.Current.SubmitOrderedGuestAction(
                    CompleteAndWake,
                    $"{debugName} completion") == 0)
            {
                CompleteAndWake();
            }
        }

        if (GuestGpu.Current.SubmitOrderedGuestAction(
                ApplyAndQueueCompletion,
                debugName) == 0)
        {
            // Headless/startup submissions have no Vulkan queue to order
            // against, so retaining the previous immediate behavior is exact.
            ApplyAndQueueCompletion();
        }
    }

    private static void RequestResumableDcbDrain(CpuContext ctx, SubmittedGpuState gpuState)
    {
        Volatile.Write(ref gpuState.PendingDrainContext, ctx);
        Interlocked.Exchange(ref gpuState.DrainPending, 1);
        if (Interlocked.CompareExchange(ref gpuState.DrainWorkerActive, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => RunResumableDcbDrainWorker(state),
                gpuState,
                preferLocal: false);
        }
    }

    private static void RunResumableDcbDrainWorker(SubmittedGpuState gpuState)
    {
        while (true)
        {
            Interlocked.Exchange(ref gpuState.DrainPending, 0);
            if (Volatile.Read(ref gpuState.PendingDrainContext) is { } drainContext)
            {
                lock (gpuState.Gate)
                {
                    DrainResumableDcbs(drainContext, gpuState, tracePackets: _traceAgc);
                }
            }

            if (Volatile.Read(ref gpuState.DrainPending) != 0)
            {
                continue;
            }

            Volatile.Write(ref gpuState.DrainWorkerActive, 0);
            // A request may have slipped in between the pending check and the
            // hand-back; re-claim the duty unless another worker already did.
            if (Volatile.Read(ref gpuState.DrainPending) == 0 ||
                Interlocked.CompareExchange(ref gpuState.DrainWorkerActive, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private static LabelProducerTrace? RegisterLabelProducer(
        object memory,
        SubmittedDcbState state,
        ulong packetAddress,
        ulong address,
        ulong length,
        string debugName)
    {
        if (address == 0 || length == 0)
        {
            return null;
        }

        memory = CanonicalMemory(memory);
        var producer = new LabelProducerTrace
        {
            Sequence = Interlocked.Increment(ref _labelProducerSequence),
            Memory = memory,
            Address = address,
            Length = length,
            PacketAddress = packetAddress,
            SubmissionId = state.ActiveSubmissionId,
            QueueName = state.QueueName,
            DebugName = debugName,
        };
        lock (_labelProducerGate)
        {
            if (_labelProducers.Count >= _labelProducerCompactionBound)
            {
                // Active producer records are synchronization state, not a
                // diagnostic cache. Removing one can hide an earlier
                // same-submission label write and make a valid in-stream fence
                // suspend forever. Compact only completed history; if all
                // records are active, correctness takes precedence over the
                // soft diagnostic bound.
                var removed = CompactCompletedEntries(
                    _labelProducers,
                    static candidate => candidate.Completed,
                    targetCount: LabelProducerSoftBound * 3 / 4);
                _labelProducerCompactionBound = removed == 0
                    ? _labelProducers.Count * 2
                    : LabelProducerSoftBound;
            }

            _labelProducers.Add(producer);
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(memory, address, length))
            {
                TraceAgc(
                    $"agc.wait_producer_scheduled label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"packet=0x{packetAddress:X16} action='{debugName}'");
            }
        }

        return producer;
    }

    internal static int CompactCompletedEntries<T>(
        List<T> entries,
        Func<T, bool> isCompleted,
        int targetCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(isCompleted);
        targetCount = Math.Max(0, targetCount);

        // Single order-preserving pass. Removing one-by-one would shift the
        // tail on every eviction, which is quadratic on a list this size and
        // runs while the label gate is held.
        var removable = entries.Count - targetCount;
        var removed = 0;
        var write = 0;
        for (var read = 0; read < entries.Count; read++)
        {
            if (removed < removable && isCompleted(entries[read]))
            {
                removed++;
                continue;
            }

            entries[write++] = entries[read];
        }

        entries.RemoveRange(write, entries.Count - write);
        return removed;
    }

    private static void CompleteLabelProducer(LabelProducerTrace? producer)
    {
        if (producer is null)
        {
            return;
        }

        lock (_labelProducerGate)
        {
            producer.Completed = true;
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(
                         producer.Memory,
                         producer.Address,
                         producer.Length))
            {
                TraceAgc(
                    $"agc.wait_producer_completed label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"action='{producer.DebugName}'");
            }
        }
    }

    private static void TraceWaitProducerState(
        object memory,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong commandAddress,
        ulong packetAddress,
        bool stale,
        ulong? currentValue = null)
    {
        memory = CanonicalMemory(memory);
        LabelProducerTrace? producer = null;
        lock (_labelProducerGate)
        {
            for (var index = _labelProducers.Count - 1; index >= 0; index--)
            {
                var candidate = _labelProducers[index];
                if (!ReferenceEquals(candidate.Memory, memory) ||
                    !RangesOverlap(
                        candidate.Address,
                        candidate.Length,
                        waiter.WaitAddress,
                        waiter.Is64Bit ? (ulong)sizeof(ulong) : sizeof(uint)))
                {
                    continue;
                }

                producer = candidate;
                break;
            }

            if (_tracedProducerlessWaits.Count >= 4096)
            {
                _tracedProducerlessWaits.Clear();
            }

            if (!stale)
            {
                // Count before the deduplication below: the warning fires once
                // per label, so on its own it cannot say how often a queue
                // actually suspends.
                GpuWaitProfile.RecordSuspend(producer is not null);
            }

            if (!stale && producer is null &&
                !_tracedProducerlessWaits.Add(
                    (memory, waiter.WaitAddress)))
            {
                return;
            }
        }

        // Producer-backed waits are trace-only. Keep the producer lookup above
        // because producerless waits are always warned, but do not build the
        // detailed condition strings when AGC tracing is disabled.
        if (producer is not null && !_traceAgc)
        {
            return;
        }

        var prefix = stale ? "agc.wait_stale" : "agc.wait_suspended";
        var current = currentValue.HasValue
            ? $"0x{currentValue.Value:X16}"
            : "unreadable";
        var condition =
            $"value={current} mask=0x{waiter.Mask:X16} " +
            $"ref=0x{waiter.ReferenceValue:X16} cmp={waiter.CompareFunction} " +
            $"control=0x{waiter.ControlValue:X8} bits={(waiter.Is64Bit ? 64 : 32)} " +
            $"form={(waiter.IsStandard ? "standard" : "agc-nop")}";
        if (producer is null)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] {prefix} label=0x{waiter.WaitAddress:X16} " +
                $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
                $"command=0x{commandAddress:X16} packet=0x{packetAddress:X16} " +
                condition + " " +
                "producer=none-observed; remaining-suspended");
            return;
        }

        TraceAgc(
            $"{prefix} label=0x{waiter.WaitAddress:X16} " +
            $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
            condition + " " +
            $"producer_seq={producer.Sequence} producer_state=" +
            $"{(producer.Completed ? "completed" : "queued")} " +
            $"producer_queue={producer.QueueName} " +
            $"producer_submission={producer.SubmissionId} " +
            $"producer_packet=0x{producer.PacketAddress:X16} " +
            $"action='{producer.DebugName}'");
    }

    private static void ApplySubmittedAcquireMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryDecodeSubmittedAcquireMem(ctx, packetAddress, out var acquire))
        {
            TraceAgc(
                $"agc.acquire_mem_decode_failed queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16}");
            return;
        }

        // The bulk PM4 read is itself a parser-side cache. Do not retain it
        // across a guest cache-invalidation point.
        DropCurrentDcbWindow();

        if (!acquire.InvalidatesGuestResources)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.acquire_mem_skip_no_invalidate queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
                    $"gcr=0x{acquire.GcrControl.Raw:X8}");
            }

            return;
        }

        var size = acquire.CoversAllGuestMemory ? ulong.MaxValue : acquire.SizeBytes;
        NotePendingAcquireInvalidation(
            state,
            acquire.BaseAddress,
            size,
            acquire.Semantics,
            acquire.CbDbControl,
            acquire.GcrControl.Raw);

        if (tracePacket)
        {
            TraceAgc(
                $"agc.acquire_mem_coalesce queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
                $"engine={acquire.Engine} cbdb=0x{acquire.CbDbControl:X8} " +
                $"base=0x{acquire.BaseAddress:X16} size=0x{acquire.SizeBytes:X16} " +
                $"scope={(acquire.CoversAllGuestMemory ? "all" : "range")} " +
                $"poll={acquire.PollInterval} gcr=0x{acquire.GcrControl.Raw:X8} " +
                $"domains={acquire.Semantics.Domains} actions={acquire.Semantics.Actions} " +
                $"cache_scope={acquire.Semantics.Scope} " +
                $"cache_order={acquire.Semantics.Order} " +
                $"pending_count={state.PendingAcquireInvalidations.Count}");
        }
    }

    private static void NotePendingAcquireInvalidation(
        SubmittedDcbState state,
        ulong baseAddress,
        ulong sizeBytes,
        AgcGpuCacheSemantics semantics,
        uint cbDbControl,
        uint gcrControl)
    {
        GuestGpuCacheOperationBatcher.AddOrMerge(
            state.PendingAcquireInvalidations,
            semantics.ToGuestOperation(
                baseAddress,
                sizeBytes,
                cbDbControl,
                gcrControl));
    }

    private static void FlushPendingAcquireInvalidation(
        CpuContext ctx,
        SubmittedDcbState state,
        bool tracePacket)
    {
        if (state.PendingAcquireInvalidations.Count == 0)
        {
            return;
        }

        var operations = state.PendingAcquireInvalidations.ToArray();
        state.PendingAcquireInvalidations.Clear();

        var queueName = state.QueueName;
        var submissionId = state.ActiveSubmissionId;
        var debugName = $"acquire_mem_flush count={operations.Length}";
        void ApplyAcquire()
        {
            foreach (var operation in operations)
            {
                SyncCpuWrittenGuestImages(
                    ctx,
                    operation.BaseAddress,
                    operation.SizeBytes);
            }

            if (tracePacket)
            {
                TraceAgc(
                    $"agc.acquire_mem_applied queue={queueName} " +
                    $"submission={submissionId} " +
                    $"work_sequence={GuestGpu.Current.CurrentGuestWorkSequenceForDiagnostics} " +
                    $"count={operations.Length}");
            }
        }

        if (tracePacket || _logGpuCacheOperations)
        {
            foreach (var operation in operations)
            {
                var semantics = new AgcGpuCacheSemantics(
                    (AgcGpuCacheDomain)(int)operation.Domains,
                    (AgcGpuCacheAction)(int)operation.Actions,
                    operation.CoversAllMemory,
                    operation.Scope,
                    operation.Order);
                TraceUniqueGpuCacheOperation(
                    "acquire_mem_flush",
                    state,
                    operation.RawCbDbControl,
                    operation.RawGcrControl,
                    operation.BaseAddress,
                    operation.SizeBytes,
                    semantics,
                    nextConsumer: "submission_boundary");
            }
        }

        if (!_gpuCacheHostEffectsEnabled)
        {
            if (Interlocked.Increment(ref _gpuCacheHostEffectsDisabledReportCount) == 1)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] AGC host cache effects disabled. " +
                    "Packet decode and logging remain active.");
            }

            return;
        }

        var sequence = GuestGpu.Current.SubmitGuestCacheOperations(
            operations,
            ApplyAcquire,
            debugName);
        if (sequence == 0)
        {
            ApplyAcquire();
        }
    }

    private static bool TryDecodeSubmittedAcquireMem(
        CpuContext ctx,
        ulong packetAddress,
        out SubmittedAcquireMem acquire)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var coherControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sizeLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sizeHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var baseLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var baseHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var pollInterval) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var gcrControl))
        {
            acquire = default;
            return false;
        }

        acquire = DecodeSubmittedAcquireMem(
            coherControl,
            sizeLow,
            sizeHigh,
            baseLow,
            baseHigh,
            pollInterval,
            gcrControl);
        return true;
    }

    private static SubmittedAcquireMem DecodeSubmittedAcquireMem(
        uint coherControl,
        uint sizeLow,
        uint sizeHigh,
        uint baseLow,
        uint baseHigh,
        uint pollInterval,
        uint gcrControl)
    {
        // GFX10 ACQUIRE_MEM expresses COHER_SIZE and COHER_BASE in 256-byte
        // units. SIZE_HI is 8 bits and BASE_HI is 24 bits in the packet.
        var sizeUnits = sizeLow | ((ulong)(sizeHigh & 0xFFu) << 32);
        var baseUnits = baseLow | ((ulong)(baseHigh & 0x00FF_FFFFu) << 32);
        return new SubmittedAcquireMem(
            Engine: coherControl >> 31,
            CbDbControl: coherControl & 0x7FFF_FFFFu,
            BaseAddress: baseUnits << 8,
            SizeBytes: sizeUnits << 8,
            PollInterval: pollInterval & 0xFFFFu,
            GcrControl: new AcquireMemGcrControl(gcrControl & 0x7FFFFu));
    }

    private static void ResetSubmittedParserState(SubmittedDcbState state)
    {
        // Queue ownership, pending submissions and suspension bookkeeping are
        // deliberately retained. Work emitted before this packet already owns
        // immutable snapshots; clearing these fields affects only commands
        // translated after RESET at this precise packet position.
        state.CxRegisters.Clear();
        state.ShRegisters.Clear();
        state.UcRegisters.Clear();
        state.CompositeDepthSizeXy = null;
        state.PresenterTexture = null;
        state.GuestDrawKind = GuestDrawKind.None;
        state.TranslatedDraw = null;
        state.RenderTargetWriters.Clear();
        state.IndirectArgsAddress = 0;
        state.SawIndexedDraw = false;
        state.IndexBufferAddress = 0;
        state.IndexBufferCount = 0;
        state.IndexSize = 0;
        state.InstanceCount = 1;
        state.DrawIndexOffset = 0;
        state.ConditionalWaitEnabled = false;
    }

    private static void ApplySubmittedPredication(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool tracePacket)
    {
        if (packetLength < 3 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var first) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var second))
        {
            return;
        }

        const uint flagsMask = 0x0007_1100u;
        uint flags;
        ulong predicateAddress;
        if (packetLength >= 4 &&
            (first & ~flagsMask) == 0 &&
            TryReadUInt32(ctx, packetAddress + 12, out var third) &&
            third <= 0xFFFFu)
        {
            flags = first;
            predicateAddress = ((ulong)third << 32) | (second & 0xFFFF_FFF0u);
        }
        else
        {
            flags = second;
            predicateAddress = (first & 0xFFFF_FFF0u) | ((ulong)(second & 0xFFu) << 32);
        }

        var operation = (flags >> 16) & 0x7u;
        if (operation == 0)
        {
            state.PredicateSkip = false;
            return;
        }

        if (operation != 3)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.predication_unsupported packet=0x{packetAddress:X16} " +
                    $"op={operation} addr=0x{predicateAddress:X16}");
            }

            return;
        }

        var waitOperation = (flags >> 12) & 1u;
        var value = 0UL;
        var readSucceeded = false;
        void ReadPredicate() =>
            readSucceeded = ctx.TryReadUInt64(predicateAddress, out value);

        if (waitOperation != 0)
        {
            var sequence = GuestGpu.Current.SubmitOrderedGuestAction(
                ReadPredicate,
                $"set_predication read 0x{predicateAddress:X16}");
            if (sequence == 0)
            {
                ReadPredicate();
            }
            else if (!GuestGpu.Current.WaitForGuestWork(sequence))
            {
                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.predication_wait_failed packet=0x{packetAddress:X16} " +
                        $"addr=0x{predicateAddress:X16} sequence={sequence}");
                }

                return;
            }
        }
        else
        {
            ReadPredicate();
        }

        if (!readSucceeded)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.predication_read_failed packet=0x{packetAddress:X16} " +
                    $"addr=0x{predicateAddress:X16}");
            }

            return;
        }

        var condition = (flags >> 8) & 1u;
        state.PredicateSkip = condition == 0 ? value != 0 : value == 0;
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.predication packet=0x{packetAddress:X16} " +
                $"addr=0x{predicateAddress:X16} value=0x{value:X16} " +
                $"condition={condition} wait={waitOperation} skip={state.PredicateSkip}");
        }
    }

    private static bool RangesOverlap(
        ulong leftAddress,
        ulong leftLength,
        ulong rightAddress,
        ulong rightLength)
    {
        var leftEnd = leftAddress > ulong.MaxValue - leftLength
            ? ulong.MaxValue
            : leftAddress + leftLength;
        var rightEnd = rightAddress > ulong.MaxValue - rightLength
            ? ulong.MaxValue
            : rightAddress + rightLength;
        return leftAddress < rightEnd && rightAddress < leftEnd;
    }

    /// <summary>
    /// Mirrors guest-side DMA/CPU writes to a render target's surface into
    /// our separate Vulkan image once per flip (Dreaming Sarah's fog layer,
    /// Chowdren's fog-noise memset). Surfaces only the GPU writes are skipped.
    /// </summary>
    private static void SyncCpuWrittenGuestImages(
        CpuContext ctx,
        ulong scopeAddress = 0,
        ulong scopeByteCount = ulong.MaxValue)
    {
        // Uploads used to copy full planes here and SubmitGuestImageWrite on the
        // AGC producer thread, which hit the payload guest-work caps and
        // soft-locked titles (GTA). The presenter's render drain owns the
        // read/upload/re-arm; this call is only a scoped wake.
        _ = ctx;
        if (!SharpEmu.HLE.GuestImageWriteTracker.Enabled || scopeByteCount == 0)
        {
            return;
        }

        GuestGpu.Current.RequestCpuWrittenGuestImageSync(scopeAddress, scopeByteCount);
    }

    private static long _dmaMirrorTraceCount;

    private static void MirrorDmaWriteToGuestImage(
        CpuContext ctx,
        ulong destinationAddress,
        ulong byteCount,
        uint? fillValue)
    {
        var hasImage = GuestGpu.Current.TryGetGuestImageExtent(
            destinationAddress,
            out var width,
            out var height,
            out var imageBytes);
        if (_traceDraws && Interlocked.Increment(ref _dmaMirrorTraceCount) <= 400)
        {
            Console.Error.WriteLine(
                $"[DMA] dst=0x{destinationAddress:X} bytes={byteCount} " +
                $"fill={(fillValue is { } f ? $"0x{f:X8}" : "copy")} image={hasImage}");
        }

        if (!hasImage)
        {
            return;
        }

        if (imageBytes == 0 || byteCount < imageBytes)
        {
            return;
        }

        if (fillValue is { } fill)
        {
            GuestGpu.Current.SubmitGuestImageFill(destinationAddress, fill);
            return;
        }

        var pixels = new byte[imageBytes];
        if (ctx.Memory.TryRead(destinationAddress, pixels))
        {
            GuestGpu.Current.SubmitGuestImageWrite(destinationAddress, pixels);
        }
    }

    private static void ApplySubmittedStandardDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sourceLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sourceHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var destinationHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var command))
        {
            return;
        }

        var byteCount = command & 0x1F_FFFFu;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var sourceSelect = (control >> 29) & 0x3u;
        var sourceAddressIncrement = (command >> 28) & 0x1u;
        var destinationAddress = destinationLow | ((ulong)destinationHigh << 32);
        var writesGuestMemory =
            byteCount != 0 &&
            destinationSwap == 0 &&
            destinationSelect is 0 or 3 &&
            (destinationSelect == 3 || destinationAddressSpace == 0);
        var fillsGuestMemory =
            sourceSelect == 2 ||
            (sourceSelect is 0 or 3 && sourceAddressIncrement != 0);
        if (writesGuestMemory && fillsGuestMemory)
        {
            RegisterActiveHtile(gpuState, state.CxRegisters);
            MarkHtileMetadataClear(
                gpuState,
                destinationAddress,
                "dma-fill");
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () => ApplySubmittedStandardDmaDataSnapshot(
                ctx,
                control,
                sourceLow,
                sourceHigh,
                destinationLow,
                destinationHigh,
                command),
            $"dma_data dst=0x{destinationHigh:X8}{destinationLow:X8} bytes={byteCount}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? byteCount : 0,
            deferLabelCompletion: true);
    }

    private static void ApplySubmittedStandardDmaDataSnapshot(
        CpuContext ctx,
        uint control,
        uint sourceLow,
        uint sourceHigh,
        uint destinationLow,
        uint destinationHigh,
        uint command)
    {
        var byteCount = command & 0x1F_FFFFu;
        var sourceSelect = (control >> 29) & 0x3u;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var sourceAddressSpace = (command >> 26) & 0x1u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var sourceAddressIncrement = (command >> 28) & 0x1u;
        if (byteCount == 0 ||
            destinationSwap != 0 ||
            destinationSelect is not (0 or 3) ||
            (destinationSelect == 0 && destinationAddressSpace != 0))
        {
            return;
        }

        var destinationAddress =
            destinationLow | ((ulong)destinationHigh << 32);
        InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
        bool copied;
        ulong sourceAddress;
        if (sourceSelect is 0 or 3 &&
            (sourceSelect == 3 || sourceAddressSpace == 0))
        {
            sourceAddress = sourceLow | ((ulong)sourceHigh << 32);
            if (sourceAddressIncrement != 0)
            {
                copied =
                    TryReadUInt32(ctx, sourceAddress, out var fillValue) &&
                    TryFillGuestMemory(
                        ctx,
                        fillValue,
                        destinationAddress,
                        byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, fillValue);
                }
            }
            else
            {
                copied = TryCopyGuestMemory(
                    ctx,
                    sourceAddress,
                    destinationAddress,
                    byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, fillValue: null);
                }
            }
        }
        else if (sourceSelect == 2)
        {
            sourceAddress = 0;
            copied = TryFillGuestMemory(
                ctx,
                sourceLow,
                destinationAddress,
                byteCount);
            if (copied)
            {
                MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, sourceLow);
            }
        }
        else
        {
            return;
        }

        if (ShouldTraceHotPath(ref _standardDmaTraceCount))
        {
            TraceAgcShader(
                $"agc.dma_packet dst=0x{destinationAddress:X16} " +
                $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                $"src_sel={sourceSelect} fill={sourceAddressIncrement != 0 || sourceSelect == 2} " +
                $"copied={copied}");
        }
    }

    private static void ApplySubmittedWriteData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool standardPacket,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var destinationAddress))
        {
            return;
        }

        var (destination, incrementAddress, writeConfirm, cachePolicy) = standardPacket
            ? DecodeStandardWriteDataControl(control)
            : DecodeAgcWriteDataControl(control);
        RecordFenceWritePacketSite(packetAddress, destinationAddress);
        var dwordCount = packetLength - 4;
        var values = new uint[dwordCount];
        for (uint index = 0; index < dwordCount; index++)
        {
            var sourceAddress = packetAddress + 16 + ((ulong)index * sizeof(uint));
            if (!TryReadUInt32(ctx, sourceAddress, out values[index]))
            {
                return;
            }
        }

        if (TrySubmitGpuOnlyWriteDataLabel(
                ctx,
                gpuState,
                state,
                packetAddress,
                destinationAddress,
                destination,
                incrementAddress,
                writeConfirm,
                cachePolicy,
                values,
                tracePacket))
        {
            return;
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(
                    destinationAddress,
                    incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint));
                var wroteData = destination is 1 or 2 or 4 or 5;
                for (uint index = 0; wroteData && index < dwordCount; index++)
                {
                    var targetAddress = destinationAddress +
                        (incrementAddress ? (ulong)index * sizeof(uint) : 0);
                    wroteData = TryWriteUInt32(ctx, targetAddress, values[index]);
                    if (wroteData)
                    {
                        GpuWaitRegistry.RecordProduced(
                            ctx.Memory, targetAddress, values[index]);
                    }
                }

                // Like ReleaseMem dataSel=2: a 64-bit WAIT_REG_MEM watches an
                // 8-byte label written as two 32-bit dwords. Record the combined
                // 64-bit value so a 64-bit EQ can latch even though the writes
                // landed as two 32-bit stores.
                if (wroteData && dwordCount >= 2 && incrementAddress)
                {
                    var combined = ((ulong)values[1] << 32) | values[0];
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory, destinationAddress, combined);
                    // Also latch the high half's address for symmetry: a stray
                    // 32-bit wait on the high dword should not be confused, but
                    // recording it does not hurt and mirrors the per-dword stores.
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.write_data dst={destination} " +
                        $"addr=0x{destinationAddress:X16} count={dwordCount} " +
                        $"increment={incrementAddress} confirm={writeConfirm} " +
                        $"cache={cachePolicy} standard={standardPacket} wrote={wroteData}");
                }
            },
            $"write_data dst=0x{destinationAddress:X16} count={dwordCount}",
            packetAddress,
            destination is 1 or 2 or 4 or 5 ? destinationAddress : 0,
            destination is 1 or 2 or 4 or 5
                ? incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint)
                : 0);
    }

    private static bool TrySubmitGpuOnlyWriteDataLabel(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        ulong destinationAddress,
        uint destination,
        bool incrementAddress,
        bool writeConfirm,
        uint cachePolicy,
        uint[] values,
        bool tracePacket)
    {
        if (!TryClassifyGpuLabelWrite(
                state.QueueName,
                destination,
                incrementAddress,
                writeConfirm,
                cachePolicy,
                out var producerEngine) ||
            values.Length == 0)
        {
            return false;
        }

        var byteCount = checked((ulong)values.Length * sizeof(uint));
        var debugName =
            $"gpu_label dst=0x{destinationAddress:X16} count={values.Length}";
        var producer = RegisterLabelProducer(
            ctx.Memory,
            state,
            packetAddress,
            destinationAddress,
            byteCount,
            debugName);
        GpuWaitRegistry.VirtualLabelPublication publication = default;

        void Publish(GuestGpuLabelDependency dependency)
        {
            publication = GpuWaitRegistry.RecordVirtualProducedRange(
                ctx.Memory,
                destinationAddress,
                values,
                dependency,
                cachePolicy,
                producerEngine);

            CompleteLabelProducer(producer);
            lock (gpuState.WaitMonitorSignalGate)
            {
                gpuState.WaitMonitorSignalVersion++;
                Monitor.Pulse(gpuState.WaitMonitorSignalGate);
            }

            RequestResumableDcbDrain(ctx, gpuState);
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.write_data_gpu_label dst={destination} " +
                    $"addr=0x{destinationAddress:X16} count={values.Length} " +
                    $"queue={state.QueueName} dependency=" +
                    $"g{dependency.GraphicsTimeline}/c{dependency.ComputeTimeline}");
            }
        }

        void PublishHost()
        {
            if (!GpuWaitRegistry.IsCurrentVirtualPublication(
                    ctx.Memory,
                    publication))
            {
                return;
            }

            var wroteAll = true;
            for (var index = 0; index < values.Length; index++)
            {
                wroteAll &= TryWriteUInt32(
                    ctx,
                    destinationAddress + ((ulong)index * sizeof(uint)),
                    values[index]);
            }

            if (wroteAll)
            {
                // Invalidate after the full write. A parser that drops its
                // cached window must only see the complete new value.
                InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
                if (_gpuLabelHostMirrorRetirementEnabled)
                {
                    GpuWaitRegistry.RetireVirtualPublication(
                        ctx.Memory,
                        publication);
                }
            }
        }

        if (GuestGpu.Current.SubmitGpuLabelSignal(
                Publish,
                _gpuLabelHostMirrorEnabled ? PublishHost : null,
                debugName) != 0)
        {
            return true;
        }

        // The backend cannot keep this label on the GPU. Remove the trace
        // producer and let the existing CPU-visible ordered action handle it.
        CompleteLabelProducer(producer);
        return false;
    }

    internal static bool TryClassifyGpuLabelWrite(
        string queueName,
        uint destination,
        bool incrementAddress,
        bool writeConfirm,
        uint cachePolicy,
        out GpuWaitRegistry.VirtualLabelEngine engine)
    {
        var computeQueue = queueName.StartsWith("acb.", StringComparison.Ordinal);
        engine = computeQueue
            ? GpuWaitRegistry.VirtualLabelEngine.Mec
            : destination == 5u
                ? GpuWaitRegistry.VirtualLabelEngine.Me
                : GpuWaitRegistry.VirtualLabelEngine.Pfp;

        return cachePolicy <= 2 &&
            incrementAddress &&
            writeConfirm &&
            (computeQueue ? destination == 2u : destination is 4u or 5u);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeStandardWriteDataControl(uint control)
    {
        // GFX10 PKT3_WRITE_DATA is not byte-packed like sceAgcDcbWriteData's
        // NOP wrapper: DST_SEL is 11:8, ADDR_INCR is bit 16 (0 increments),
        // WR_CONFIRM is bit 20, and CACHE_POLICY is 26:25. In particular, the
        // low byte is reserved and must never be interpreted as DST_SEL.
        return (
            Destination: (control >> 8) & 0xFu,
            IncrementAddress: (control & (1u << 16)) == 0,
            WriteConfirm: (control & (1u << 20)) != 0,
            CachePolicy: (control >> 25) & 0x3u);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeAgcWriteDataControl(uint control) =>
        (
            Destination: control & 0xFFu,
            IncrementAddress: ((control >> 16) & 0xFFu) == 0,
            WriteConfirm: ((control >> 24) & 0xFFu) != 0,
            CachePolicy: (control >> 8) & 0xFFu);

#if DEBUG
    private static void ValidateWriteDataControlDecoders()
    {
        // Regression vector: reserved low-byte noise previously decoded 0xA5
        // as DST_SEL, causing a valid standard memory write to be discarded.
        const uint standardControl = 0xA5u | (5u << 8) | (1u << 16) | (1u << 20) | (2u << 25);
        var standard = DecodeStandardWriteDataControl(standardControl);
        System.Diagnostics.Debug.Assert(standard.Destination == 5u);
        System.Diagnostics.Debug.Assert(!standard.IncrementAddress);
        System.Diagnostics.Debug.Assert(standard.WriteConfirm);
        System.Diagnostics.Debug.Assert(standard.CachePolicy == 2u);

        const uint agcControl = 4u | (3u << 8) | (1u << 24);
        var agc = DecodeAgcWriteDataControl(agcControl);
        System.Diagnostics.Debug.Assert(agc.Destination == 4u);
        System.Diagnostics.Debug.Assert(agc.IncrementAddress);
        System.Diagnostics.Debug.Assert(agc.WriteConfirm);
        System.Diagnostics.Debug.Assert(agc.CachePolicy == 3u);
    }

    private static void ValidateSubmittedQueueAndReleaseMemDecoders()
    {
        var nggRegisters = new Dictionary<uint, uint>
        {
            [GsUserDataRegister - 1] = 3u << 1,
        };
        System.Diagnostics.Debug.Assert(
            SelectExportUserDataRegister(nggRegisters) == GsUserDataRegister);

        var queue = new SubmittedDcbState();
        queue.PendingSubmissions.Enqueue(new(0x1000, 8, 11, false, null, null, 0));
        queue.PendingSubmissions.Enqueue(new(0x2000, 16, 12, true, null, null, 0));
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 11);
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 12);

        var control = (1u << 16) | (2u << 29);
        var decoded = DecodeStandardReleaseMemControl(control);
        System.Diagnostics.Debug.Assert(decoded.Destination == 1u);
        System.Diagnostics.Debug.Assert(decoded.DataSelection == 2u);
        System.Diagnostics.Debug.Assert(
            PatchUInt32Bits(0xABCD_1234u, 0x00FF_0000u, 3u << 16) ==
            0xAB03_1234u);
    }

    private static void ValidateAcquireMemAndQueueResetDecoders()
    {
        var range = DecodeSubmittedAcquireMem(
            0x8000_7FC0u,
            0x0000_0123u,
            0x45u,
            0x89AB_CDEFu,
            0x0012_3456u,
            0x1_000Au,
            0x0001_0388u);
        System.Diagnostics.Debug.Assert(range.Engine == 1u);
        System.Diagnostics.Debug.Assert(range.CbDbControl == 0x7FC0u);
        System.Diagnostics.Debug.Assert(range.SizeBytes == 0x0000_4500_0001_2300UL);
        System.Diagnostics.Debug.Assert(range.BaseAddress == 0x1234_5689_ABCD_EF00UL);
        System.Diagnostics.Debug.Assert(range.PollInterval == 0xAu);
        System.Diagnostics.Debug.Assert(range.InvalidatesGuestResources);
        System.Diagnostics.Debug.Assert(!range.CoversAllGuestMemory);

        var all = DecodeSubmittedAcquireMem(0, 0, 0, 0, 0, 0, 0x280u);
        System.Diagnostics.Debug.Assert(all.CoversAllGuestMemory);
        System.Diagnostics.Debug.Assert(all.InvalidatesGuestResources);
        var explicitAll = DecodeSubmittedAcquireMem(0, 1, 0, 0, 0, 0, 0x103C0u);
        System.Diagnostics.Debug.Assert(explicitAll.CoversAllGuestMemory);

        var queue = new SubmittedDcbState
        {
            QueueName = "validator",
            ActiveSubmissionId = 7,
            HasActiveSubmission = true,
            IsSuspended = true,
            IndexBufferAddress = 0x1000,
            IndexBufferCount = 12,
            IndexSize = 1,
            InstanceCount = 4,
            DrawIndexOffset = 2,
            IndirectArgsAddress = 0x2000,
            SawIndexedDraw = true,
            GuestDrawKind = GuestDrawKind.FullscreenBarycentric,
        };
        queue.CxRegisters.Add(1, 2);
        queue.ShRegisters.Add(3, 4);
        queue.UcRegisters.Add(5, 6);
        queue.PendingSubmissions.Enqueue(new(0x3000, 2, 8, false, null, null, 0));
        ResetSubmittedParserState(queue);
        System.Diagnostics.Debug.Assert(queue.CxRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.ShRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.UcRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferAddress == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferCount == 0);
        System.Diagnostics.Debug.Assert(queue.IndexSize == 0);
        System.Diagnostics.Debug.Assert(queue.InstanceCount == 1);
        System.Diagnostics.Debug.Assert(queue.DrawIndexOffset == 0);
        System.Diagnostics.Debug.Assert(queue.IndirectArgsAddress == 0);
        System.Diagnostics.Debug.Assert(!queue.SawIndexedDraw);
        System.Diagnostics.Debug.Assert(queue.GuestDrawKind == GuestDrawKind.None);
        System.Diagnostics.Debug.Assert(queue.QueueName == "validator");
        System.Diagnostics.Debug.Assert(queue.ActiveSubmissionId == 7);
        System.Diagnostics.Debug.Assert(queue.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(queue.IsSuspended);
        System.Diagnostics.Debug.Assert(queue.PendingSubmissions.Count == 1);
    }

#endif

    // Distinguishes a stopped render loop from an orphan-mechanism gap.
    private static long _dcbSubmitCount;
    private static long _lastDcbSubmitTimestamp;

    public static (long Count, double SecondsSinceLastSubmit) DcbSubmitHeartbeat()
    {
        var count = Volatile.Read(ref _dcbSubmitCount);
        var lastTicks = Volatile.Read(ref _lastDcbSubmitTimestamp);
        var seconds = lastTicks == 0
            ? -1
            : (System.Diagnostics.Stopwatch.GetTimestamp() - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return (count, seconds);
    }

    private static void ApplySubmittedStandardReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var releaseControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var interruptContextId))
        {
            return;
        }

        var cacheControl = DecodeStandardReleaseMemCacheControl(releaseControl);
        var cacheSemantics = cacheControl.GcrControl.ToSemantics();
        var (destination, dataSelection) = DecodeStandardReleaseMemControl(control);
        var interruptSelection = (control >> 24) & 0x7u;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var isAsyncCompute = state.QueueName.StartsWith("acb.", StringComparison.Ordinal);
        var staticDecision = EvaluateQueuedInterrupt(
            interruptSelection,
            isAsyncCompute,
            dataSelection,
            conditionReadable: false,
            conditionValue: 0,
            data);
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 or 4 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        var expectsGuestMemoryWrite = staticDecision.WritesData &&
                                      destination is 0 or 1 &&
                                      writeLength != 0;
        var writesGuestMemory = expectsGuestMemoryWrite && destinationAddress != 0;
        var submissionCompletionState = state.ActiveCompletionState;

        if (tracePacket || _logGpuCacheOperations)
        {
            TraceUniqueGpuCacheOperation(
                "release_mem_standard",
                state,
                cbDbAction: 0,
                cacheControl.GcrControl.Raw,
                baseAddress: 0,
                sizeBytes: ulong.MaxValue,
                cacheSemantics);
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                var conditionReadable = TryReadQueuedInterruptCondition(
                    ctx,
                    interruptSelection,
                    destinationAddress,
                    out var conditionValue);
                var interruptDecision = EvaluateQueuedInterrupt(
                    interruptSelection,
                    isAsyncCompute,
                    dataSelection,
                    conditionReadable,
                    conditionValue,
                    data);
                if (writesGuestMemory)
                {
                    InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                }

                var wroteData = writesGuestMemory && (dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    2 => ctx.TryWriteUInt64(destinationAddress, data),
                    // Hardware counter writes are timing values sampled at the
                    // release point, not the immediate payload in ordinal 6/7.
                    3 or 4 => ctx.TryWriteUInt64(
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                });

                // Record + latch the written value so a same-frame label reset
                // cannot lose the wakeup, and so the deadlock breaker can release
                // a cross-queue waiter later (see ApplySubmittedReleaseMem).
                if (wroteData && dataSelection is 1 or 2)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory,
                        destinationAddress,
                        dataSelection == 1 ? dataLo : data,
                        hasHighDword: dataSelection == 2);
                }
                else if (expectsGuestMemoryWrite && !wroteData && dataSelection is 1 or 2)
                {
                    // See ApplySubmittedReleaseMem: a dropped label write strands
                    // every waiter on this label permanently.
                    ReportLabelWriteFailure(
                        "release_mem_standard", destinationAddress, data, dataSelection);
                }

                // Only deliver a kevent when int_sel requests one — the
                // driver's completion refcount signals on an exact zero
                // crossing, and an unrequested kevent drives it negative and
                // permanently loses the frame-graph kick.
                var wokenQueues = interruptDecision.RaisesInterrupt
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        interruptContextId & 0x07FF_FFFFu)
                    : 0;
                if (interruptDecision.RaisesInterrupt &&
                    submissionCompletionState is not null)
                {
                    submissionCompletionState.RaisedQueuedInterrupt = true;
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem_standard dst_sel={destination} " +
                        $"dst=0x{destinationAddress:X16} data_sel={dataSelection} " +
                        $"data=0x{data:X16} wrote={wroteData} " +
                        $"event=0x{cacheControl.EventType:X2} " +
                        $"event_index={cacheControl.EventIndex} " +
                        $"cache={cacheControl.CachePolicy} " +
                        $"gcr=0x{cacheControl.GcrControl.Raw:X4} " +
                        $"int={interruptSelection} condition_read={conditionReadable} " +
                        $"condition=0x{conditionValue:X16} woken={wokenQueues}");
                }
            },
            $"release_mem_standard dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? writeLength : 0);
    }

    private static long _labelWriteFailureCount;

    /// <summary>
    /// Reports a GPU release-label write that could not reach guest memory.
    /// Rate-limited (first 16, then powers of two) because a wedged queue can
    /// retry, but never silenced: this is the difference between a diagnosable
    /// fault and a permanently suspended graphics queue with no explanation.
    /// </summary>
    private static void ReportLabelWriteFailure(
        string packet,
        ulong destinationAddress,
        ulong data,
        uint dataSelection)
    {
        var count = Interlocked.Increment(ref _labelWriteFailureCount);
        if (count > 16 && (count & (count - 1)) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][ERROR] agc.label_write_failed packet={packet} " +
            $"dst=0x{destinationAddress:X16} data=0x{data:X16} " +
            $"data_sel={dataSelection} count={count} — a suspended WAIT_REG_MEM " +
            $"on this label can no longer be satisfied or deadlock-broken.");
    }

    private static (uint Destination, uint DataSelection)
        DecodeStandardReleaseMemControl(uint control) =>
        (
            Destination: (control >> 16) & 0x3u,
            DataSelection: (control >> 29) & 0x7u);

    private static void ApplySubmittedReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var actionControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var interruptContextId))
        {
            return;
        }

        var cacheControl = DecodeAgcReleaseMemCacheControl(actionControl, control);
        var gcrSemantics = cacheControl.GcrControl.ToSemantics();
        var actionSemantics = cacheControl.ActionSemantics;
        var cacheSemantics = new AgcGpuCacheSemantics(
            gcrSemantics.Domains | actionSemantics.Domains,
            gcrSemantics.Actions | actionSemantics.Actions,
            gcrSemantics.CoversAllMemory || actionSemantics.CoversAllMemory);
        var dataSelection = (control >> 16) & 0xFFu;
        var interrupt = (control >> 24) & 0xFFu;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var isAsyncCompute = state.QueueName.StartsWith("acb.", StringComparison.Ordinal);
        var staticDecision = EvaluateQueuedInterrupt(
            interrupt,
            isAsyncCompute,
            dataSelection,
            conditionReadable: false,
            conditionValue: 0,
            data);
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        var expectsGuestMemoryWrite = staticDecision.WritesData && writeLength != 0;
        var writesGuestMemory = expectsGuestMemoryWrite && destinationAddress != 0;
        var submissionCompletionState = state.ActiveCompletionState;
        if (tracePacket || _logGpuCacheOperations)
        {
            TraceUniqueGpuCacheOperation(
                "release_mem",
                state,
                cacheControl.RawAction,
                cacheControl.GcrControl.Raw,
                baseAddress: 0,
                sizeBytes: ulong.MaxValue,
                cacheSemantics,
                completionAction: cacheControl.ActionName);
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                var conditionReadable = TryReadQueuedInterruptCondition(
                    ctx,
                    interrupt,
                    destinationAddress,
                    out var conditionValue);
                var interruptDecision = EvaluateQueuedInterrupt(
                    interrupt,
                    isAsyncCompute,
                    dataSelection,
                    conditionReadable,
                    conditionValue,
                    data);
                if (writesGuestMemory)
                {
                    InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                }

                var wroteData = writesGuestMemory && dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    2 => ctx.TryWriteUInt64(destinationAddress, data),
                    // Data selection 3 samples the GPU clock at the release
                    // point. The packet payload is ignored by hardware; Unity
                    // uses the nonzero timestamp as submit-completion state.
                    3 => ctx.TryWriteUInt64(
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                };

                // Latch waiters against the value we just wrote: the guest reuses
                // these labels and can reset them to 0 before the wake pass reads
                // memory, which otherwise loses the wakeup and stalls at a black
                // screen (Astro Bot: graphics queue waiting on a compute EOP label).
                if (wroteData && dataSelection is 1 or 2)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory,
                        destinationAddress,
                        dataSelection == 1 ? dataLo : data,
                        hasHighDword: dataSelection == 2);
                }
                else if (expectsGuestMemoryWrite && !wroteData && dataSelection is 1 or 2)
                {
                    // A label write that fails is not a benign miss: this packet
                    // is the producer a suspended WAIT_REG_MEM is waiting for, and
                    // RecordProduced above is skipped, so the deadlock breaker has
                    // no value to replay either. The queue then never resumes.
                    // Never let that happen quietly.
                    ReportLabelWriteFailure("release_mem", destinationAddress, data, dataSelection);
                }

                // Same interrupt gating as the standard form above.
                var wokenQueues = interruptDecision.RaisesInterrupt
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        interruptContextId & 0x07FF_FFFFu)
                    : 0;
                if (interruptDecision.RaisesInterrupt &&
                    submissionCompletionState is not null)
                {
                    submissionCompletionState.RaisedQueuedInterrupt = true;
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem dst=0x{destinationAddress:X16} " +
                        $"data_sel={dataSelection} data=0x{data:X16} wrote={wroteData} " +
                        $"completion={cacheControl.ActionName} " +
                        $"action=0x{cacheControl.RawAction:X2} " +
                        $"cache={cacheControl.CachePolicy} " +
                        $"gcr=0x{cacheControl.GcrControl.Raw:X4} " +
                        $"int={interrupt} condition_read={conditionReadable} " +
                        $"condition=0x{conditionValue:X16} woken={wokenQueues}");
                }
            },
            $"release_mem dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? writeLength : 0);
    }

    // ABI (reversed from Quake): rdi = array of DCB base addresses (u64 each),
    // rsi = array of DCB sizes in dwords (u32 each), rdx = buffer count.
    [SysAbiExport(
        Nid = "6UzEidRZwkg",
        ExportName = "sceAgcDriverSubmitMultiDcbs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitMultiDcbs(CpuContext ctx)
    {
        Interlocked.Increment(ref _dcbSubmitCount);
        Volatile.Write(ref _lastDcbSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        var addressArray = ctx[CpuRegister.Rdi];
        var sizeArray = ctx[CpuRegister.Rsi];
        var bufferCount = (uint)ctx[CpuRegister.Rdx];
        if (addressArray == 0 || sizeArray == 0 || bufferCount == 0 || bufferCount > 4096)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal);

        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            Gen5ShaderScalarEvaluator.BeginGlobalMemoryReadScope();
            try
            {
                for (uint i = 0; i < bufferCount; i++)
                {
                    if (gpuState.Graphics.IsFaulted)
                    {
                        ReportFaultedSubmissionRejected(
                            gpuState.Graphics,
                            gpuState.Graphics.ActiveSubmissionId);
                        break;
                    }

                    if (!ctx.TryReadUInt64(addressArray + i * 8, out var commandAddress) ||
                        commandAddress == 0 ||
                        !ctx.TryReadUInt32(sizeArray + i * 4, out var dwordCount) ||
                        dwordCount == 0)
                    {
                        continue;
                    }

                    if (tracePackets)
                    {
                        TraceAgc(
                            $"agc.driver_submit_multi_dcbs index={i}/{bufferCount} " +
                            $"addr=0x{commandAddress:X16} dwords={dwordCount}");
                    }

                    ParseSubmittedDcb(
                        ctx,
                        gpuState,
                        gpuState.Graphics,
                        commandAddress,
                        dwordCount,
                        tracePackets);
                }

                DrainResumableDcbs(ctx, gpuState, tracePackets);
            }
            finally
            {
                Gen5ShaderScalarEvaluator.EndGlobalMemoryReadScope();
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryCopyGuestMemory(
        CpuContext ctx,
        ulong sourceAddress,
        ulong destinationAddress,
        uint byteCount)
    {
        if (sourceAddress == destinationAddress)
        {
            return true;
        }

        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        ulong offset = 0;
        while (offset < byteCount)
        {
            var chunkLength = (int)Math.Min((ulong)buffer.Length, byteCount - offset);
            var chunk = buffer.AsSpan(0, chunkLength);
            if (!ctx.Memory.TryRead(sourceAddress + offset, chunk) ||
                !ctx.Memory.TryWrite(destinationAddress + offset, chunk))
            {
                return false;
            }

            offset += (uint)chunkLength;
        }

        return true;
    }

    private static bool TryFillGuestMemory(
        CpuContext ctx,
        uint value,
        ulong destinationAddress,
        uint byteCount)
    {
        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            var remaining = Math.Min(sizeof(uint), buffer.Length - offset);
            encoded[..remaining].CopyTo(buffer.AsSpan(offset, remaining));
        }

        ulong destinationOffset = 0;
        while (destinationOffset < byteCount)
        {
            var chunkLength = (int)Math.Min(
                (ulong)buffer.Length,
                byteCount - destinationOffset);
            if (!ctx.Memory.TryWrite(
                    destinationAddress + destinationOffset,
                    buffer.AsSpan(0, chunkLength)))
            {
                return false;
            }

            destinationOffset += (uint)chunkLength;
        }

        return true;
    }
}

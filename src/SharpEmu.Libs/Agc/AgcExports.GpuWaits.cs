// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.Agc;

// This partial owns submitted GPU wait suspension and resumption.
public static partial class AgcExports
{
    private static long _unsatisfiedWaitTraceCount;

    // SHARPEMU_GPU_WAIT_MODE=force reverts to the legacy behaviour of faking a
    // satisfying value at parse time. Default (suspend) properly suspends the
    // DCB on an unmet WAIT_REG_MEM and resumes it once the awaited completion
    // label is genuinely written by a later submit — preserving cross-submit
    // ordering so the work after a wait (e.g. the final composite) does not run
    // ahead of the compute it samples.
    private static readonly bool _gpuWaitSuspendEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_MODE"),
        "force",
        StringComparison.OrdinalIgnoreCase);

    // Optional age for one-shot missing-producer diagnostics. Stale waits are
    // never removed or force-satisfied in the default suspend mode: doing so
    // advances a queue without its real cross-queue producer and can publish
    // incomplete CPU/GPU state. Only SHARPEMU_GPU_WAIT_MODE=force retains the
    // explicit legacy mutation path above. Default 0 disables age diagnostics.
    private static readonly long _gpuWaitStaleTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_FALLBACK_MS"),
             out var fallbackMs) && fallbackMs >= 0
            ? fallbackMs
            : 0L) * System.Diagnostics.Stopwatch.Frequency / 1000L;

    // How long a suspended GPU wait may sit before the deadlock breaker may
    // release it using the last value a real producer wrote to its label. Long
    // enough that legitimate GPU work (which completes within a frame) never
    // trips it; short enough that a wedged cross-queue cycle unblocks quickly.
    private static readonly long _gpuDeadlockBreakTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_GPU_DEADLOCK_BREAK_MS"),
             out var deadlockMs) && deadlockMs > 0
            ? deadlockMs
            : 500L) * System.Diagnostics.Stopwatch.Frequency / 1000L;

    // Reads the WAIT_REG_MEM watched address, reference, mask, and 3-bit compare
    // function for both the AGC NOP-encapsulated (RWaitMem32/64) and the standard
    // ItWaitRegMem packet layouts.
    private static bool TryParseSubmittedWait(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        bool is64Bit,
        bool isStandard,
        out ulong waitAddress,
        out ulong reference,
        out ulong mask,
        out uint compareFunction,
        out uint controlValue)
    {
        waitAddress = 0;
        reference = 0;
        mask = 0;
        compareFunction = 0;
        controlValue = 0;
        if (isStandard)
        {
            if (!TryReadUInt32(ctx, packetAddress + 4, out var stdControl) ||
                !TryReadUInt64(ctx, packetAddress + 8, out waitAddress) ||
                !TryReadUInt32(ctx, packetAddress + 16, out var stdRef) ||
                !TryReadUInt32(ctx, packetAddress + 20, out var stdMask))
            {
                return false;
            }

            compareFunction = stdControl & 0x7u;
            controlValue = stdControl;
            reference = stdRef;
            mask = stdMask;
            return true;
        }

        var legacyWait32 = !is64Bit && packetLength == 6;
        var controlOffset = is64Bit ? 28u : legacyWait32 ? 16u : 20u;
        if (!TryReadUInt64(ctx, packetAddress + 4, out waitAddress) ||
            !TryReadUInt32(ctx, packetAddress + controlOffset, out var control))
        {
            return false;
        }

        compareFunction = control & 0x7u;
        controlValue = control;
        if (is64Bit)
        {
            return TryReadUInt64(ctx, packetAddress + 12, out mask) &&
                   TryReadUInt64(ctx, packetAddress + 20, out reference);
        }

        var referenceOffset = legacyWait32 ? 20u : 16u;
        if (!TryReadUInt32(ctx, packetAddress + 12, out var mask32) ||
            !TryReadUInt32(ctx, packetAddress + referenceOffset, out var reference32))
        {
            return false;
        }

        mask = mask32;
        reference = reference32;
        return true;
    }

    // Parks on ring memory not yet written by the game; resumes once it appends more.
    private static bool SuspendOnUnwrittenRingWord(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong wordAddress,
        uint offset,
        bool tracePacket)
    {
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = wordAddress,
            TotalDwords = offset + RingResumeWindowDwords,
            ResumeOffset = offset,
            ReferenceValue = 0,
            Mask = 0xFFFF_FFFFu,
            CompareFunction = 4, // resume once the dword becomes nonzero
            ControlValue = 0,
            Is64Bit = false,
            IsStandard = false,
            WaitAddress = wordAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);
        state.RingTailParkAddress = wordAddress;
        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.ring_tail_pending addr=0x{wordAddress:X16} " +
                $"queue={state.QueueName}");
        }

        return true;
    }

    // Returns true when the DCB should suspend parsing at this wait (its
    // continuation was registered into GpuWaitRegistry); false to keep parsing
    // (already satisfied, unreadable, or legacy force-satisfy mode).
    // How long an indirect dispatch may wait for its producing dispatch to write
    // non-zero dimensions before we give up and drop it (matching the pre-existing
    // reject behavior). The producer runs on the render thread within a frame or
    // two; this only bounds the pathological/legitimately-empty case.
    private const long IndirectDimsRetryBudgetMs = 150;

    private static readonly object _indirectDimsGate = new();
    // Keys (memory, packetAddress) whose retry deadline elapsed. Added by
    // DrainResumableDcbs when it resumes an expired retry, consumed by the very
    // next re-parse of that packet so it drops instead of re-suspending. Never
    // persists across frames — a fresh submit of the same packet retries anew.
    private static readonly HashSet<(object, ulong)> _indirectDimsExpired = new();

    // Suspends an indirect-dispatch DCB until the guest buffer holding its
    // thread-group dimensions becomes non-zero (written by a prior GPU dispatch),
    // then re-parses the dispatch. Returns false — so the caller drops the work —
    // when the dims already expired once (genuinely empty dispatch).
    private static bool HandleSubmittedIndirectDimsWait(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint dwordCount,
        ulong dimsAddress,
        bool tracePacket)
    {
        if (!_gpuWaitSuspendEnabled ||
            dimsAddress == 0 ||
            dimsAddress % sizeof(uint) != 0)
        {
            return false;
        }

        var key = (ctx.Memory, packetAddress);
        lock (_indirectDimsGate)
        {
            // This is the re-parse right after the deadline elapsed: drop the
            // dispatch instead of suspending again.
            if (_indirectDimsExpired.Remove(key))
            {
                return false;
            }
        }

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress, // re-parse this dispatch packet
            ResumeOffset = offset,
            TotalDwords = dwordCount,
            WaitAddress = dimsAddress,
            ReferenceValue = 0,
            Mask = 0xFFFFFFFF,
            CompareFunction = 4, // NOT_EQUAL: dims became available
            Is64Bit = false,
            IsStandard = false,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            RetryDeadlineTicks = System.Diagnostics.Stopwatch.GetTimestamp() +
                (IndirectDimsRetryBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000L),
            State = state,
        };

        GpuWaitRegistry.Register(dimsAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dispatch_indirect_wait dims=0x{dimsAddress:X16} " +
                $"packet=0x{packetAddress:X16} queue={state.QueueName}");
        }

        return true;
    }

    private static bool HandleSubmittedRewind(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint length,
        uint dwordCount,
        bool tracePacket)
    {
        var bodyAddress = packetAddress + sizeof(uint);
        if (!TryReadUInt32(ctx, bodyAddress, out var body))
        {
            return false;
        }

        if ((body & RewindValidBit) != 0)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.rewind_valid queue={state.QueueName} " +
                    $"packet=0x{packetAddress:X16} body=0x{body:X8}");
            }

            return false; // already valid — keep parsing
        }

        if (!_gpuWaitSuspendEnabled)
        {
            return false;
        }

        // Suspend until RewindPatchSetRewindState sets bit 31 on the body dword.
        const uint compareEqual = 3;
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)length * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + length,
            ReferenceValue = RewindValidBit,
            Mask = RewindValidBit,
            CompareFunction = compareEqual,
            ControlValue = 0,
            Is64Bit = false,
            IsStandard = true,
            WaitAddress = bodyAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        GpuWaitRegistry.Register(bodyAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(
            ctx.Memory,
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        TraceAgcShader(
            $"agc.rewind_suspend queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} " +
            $"packet=0x{packetAddress:X16} body=0x{bodyAddress:X16}");
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.rewind_suspend queue={state.QueueName} " +
                $"packet=0x{packetAddress:X16} body=0x{body:X8}");
        }

        return true;
    }

    private static bool HandleSubmittedWaitRegMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint length,
        uint dwordCount,
        bool is64Bit,
        bool isStandard,
        bool tracePacket)
    {
        if (!TryParseSubmittedWait(
                ctx, packetAddress, length, is64Bit, isStandard,
                out var waitAddress, out var reference, out var mask, out var compareFunction,
                out var controlValue))
        {
            return false;
        }

        var waitOperation = DecodeWaitOperation(controlValue, is64Bit);
        if (!IsValidWaitOperation(waitOperation))
        {
            TraceAgc(
                $"agc.dcb.wait_reject addr=0x{waitAddress:X16} " +
                $"operation={waitOperation} bits={(is64Bit ? 64 : 32)} " +
                $"standard={isStandard} packet=0x{packetAddress:X16} " +
                "reason=invalid-operation");
            return false;
        }

        if (!ShouldExecuteWaitOperation(waitOperation, state.ConditionalWaitEnabled))
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.conditional_wait_skipped addr=0x{waitAddress:X16} " +
                    $"packet=0x{packetAddress:X16} scratch=0");
            }

            return false;
        }

        // COMPARE_FUNC=0 is the hardware "always" condition. Reserved 7 is
        // also fail-open; neither condition may register a waiter. Validate
        // the watched memory before any read so null/malformed packets cannot
        // become permanent entries keyed by address zero.
        if (compareFunction is 0 or 7)
        {
            TraceSubmittedWait(
                waitAddress,
                0,
                mask,
                reference,
                compareFunction,
                is64Bit ? 64 : 32,
                tracePacket);
            return false;
        }

        var requiredAlignment = is64Bit ? sizeof(ulong) : sizeof(uint);
        if (waitAddress == 0 ||
            mask == 0 ||
            waitAddress % (ulong)requiredAlignment != 0)
        {
            TraceAgc(
                $"agc.dcb.wait_reject addr=0x{waitAddress:X16} " +
                $"mask=0x{mask:X16} compare={compareFunction} bits=" +
                $"{(is64Bit ? 64 : 32)} standard={isStandard} " +
                $"packet=0x{packetAddress:X16} reason=invalid-address-or-mask");
            return false;
        }

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)length * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + length,
            ReferenceValue = reference,
            Mask = mask,
            CompareFunction = compareFunction,
            ControlValue = controlValue,
            Is64Bit = is64Bit,
            IsStandard = isStandard,
            WaitAddress = waitAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            SubmissionPublicationGeneration =
                state.ActiveSubmissionPublicationGeneration,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        ulong? ReadGuestLabel(ulong address, bool read64Bit)
        {
            if (read64Bit)
            {
                return TryReadUInt64(ctx, address, out var value64)
                    ? value64
                    : null;
            }

            return TryReadUInt32(ctx, address, out var value32)
                ? value32
                : null;
        }

        if (!_gpuWaitSuspendEnabled)
        {
            var waitCachePolicy = (controlValue >> 25) & 0x3u;
            ulong currentValue = 0;
            GuestGpuLabelDependency currentDependency = default;
            var hasCurrent = waitCachePolicy <= 2 &&
                             GpuWaitRegistry.TryReadVirtual(
                                 ctx.Memory,
                                 waitAddress,
                                 is64Bit,
                                 out currentValue,
                                 out currentDependency,
                                 waitCachePolicy);
            if (!hasCurrent)
            {
                var current = ReadGuestLabel(waitAddress, is64Bit);
                hasCurrent = current.HasValue;
                currentValue = current.GetValueOrDefault();
            }

            TraceSubmittedWait(
                waitAddress,
                currentValue,
                mask,
                reference,
                compareFunction,
                is64Bit ? 64 : 32,
                tracePacket);
            if (hasCurrent && GpuWaitRegistry.Compare(waiter, currentValue))
            {
                GuestGpu.Current.RequireGpuLabelDependency(currentDependency);
                return false;
            }

            if (hasCurrent)
            {
                ForceSatisfyGpuWait(ctx, waiter, currentValue);
            }

            return false;
        }

        var registration = GpuWaitRegistry.RegisterIfUnsatisfied(
            waiter,
            ReadGuestLabel,
            out var observedValue,
            out var observedDependency);
        TraceSubmittedWait(
            waitAddress,
            observedValue,
            mask,
            reference,
            compareFunction,
            is64Bit ? 64 : 32,
            tracePacket);
        if (registration is GpuWaitRegistry.WaitRegistrationResult.Satisfied or
            GpuWaitRegistry.WaitRegistrationResult.SatisfiedByHistory)
        {
            GuestGpu.Current.RequireGpuLabelDependency(observedDependency);
            return false;
        }

        if (registration == GpuWaitRegistry.WaitRegistrationResult.Unreadable)
        {
            return false; // cannot evaluate the label — do not stall the DCB
        }

        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        TryForceSubmitOrphanPreamble(ctx, gpuState, waitAddress);
        TraceWaitProducerState(
            ctx.Memory,
            waiter,
            commandAddress,
            packetAddress,
            stale: false,
            observedValue);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.suspended addr=0x{waitAddress:X16} ref=0x{reference:X16} " +
                $"mask=0x{mask:X16} cur=0x{observedValue:X16} cmp={compareFunction}");
        }

        return true;
    }

    private static void ApplySubmittedCondWrite(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var readLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var readHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var reference) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var mask) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var writeLow) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var writeHigh) ||
            !TryReadUInt32(ctx, packetAddress + 32, out var writeValue))
        {
            return;
        }

        var compareFunction = control & 0x7u;
        var pollsMemory = (control & (1u << 4)) != 0;
        var writesMemory = (control & (1u << 8)) != 0;
        var readAddress = ((ulong)(readHigh & 0xFFFFu) << 32) |
                          (readLow & 0xFFFF_FFFCu);
        var writeAddress = ((ulong)(writeHigh & 0xFFFFu) << 32) |
                           (writeLow & 0xFFFF_FFFCu);
        if (!pollsMemory ||
            compareFunction == 7 ||
            readAddress == 0 ||
            (writesMemory && writeAddress == 0))
        {
            TraceAgc(
                $"agc.dcb.cond_write_reject packet=0x{packetAddress:X16} " +
                $"compare={compareFunction} poll_memory={pollsMemory} " +
                $"read=0x{readAddress:X16}");
            return;
        }

        var readSucceeded = false;
        var conditionPassed = false;
        var wroteData = false;
        var observed = 0u;

        void TraceResult()
        {
            if (!tracePacket)
            {
                return;
            }

            TraceAgc(
                $"agc.dcb.cond_write packet=0x{packetAddress:X16} " +
                $"read=0x{readAddress:X16} value=0x{observed:X8} " +
                $"ref=0x{reference:X8} mask=0x{mask:X8} compare={compareFunction} " +
                $"read_ok={readSucceeded} pass={conditionPassed} " +
                $"space={(writesMemory ? "gl2" : "scratch")} " +
                $"write=0x{writeAddress:X16} data=0x{writeValue:X8} wrote={wroteData}");
        }

        void ApplyCondition()
        {
            readSucceeded = TryReadLiveUInt32(ctx, readAddress, out observed);
            conditionPassed = readSucceeded &&
                CompareConditionalValue(observed, reference, mask, compareFunction);
            if (!conditionPassed)
            {
                TraceResult();
                return;
            }

            if (writesMemory)
            {
                InvalidateDcbWindowIfOverlaps(writeAddress, sizeof(uint));
                wroteData = TryWriteUInt32(ctx, writeAddress, writeValue);
                if (wroteData)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory,
                        writeAddress,
                        writeValue);
                }

                TraceResult();
                return;
            }

            state.ConditionalWaitEnabled = writeValue != 0;
            TraceResult();
        }

        if (writesMemory)
        {
            SubmitOrderedGpuSideEffect(
                ctx,
                gpuState,
                state,
                ApplyCondition,
                $"cond_write dst=0x{writeAddress:X16}",
                packetAddress,
                writeAddress,
                sizeof(uint));
            return;
        }

        // The parser needs the CP scratch result before it handles the next wait.
        var sequence = GuestGpu.Current.SubmitOrderedGuestAction(
            ApplyCondition,
            $"cond_write scratch read=0x{readAddress:X16}");
        if (sequence == 0)
        {
            ApplyCondition();
        }
        else if (!GuestGpu.Current.WaitForGuestWork(sequence))
        {
            TraceAgc(
                $"agc.dcb.cond_write_wait_failed packet=0x{packetAddress:X16} " +
                $"sequence={sequence}");
        }
    }

    internal static bool CompareConditionalValue(
        uint value,
        uint reference,
        uint mask,
        uint compareFunction)
    {
        var maskedValue = value & mask;
        return compareFunction switch
        {
            0 => true,
            1 => maskedValue < reference,
            2 => maskedValue <= reference,
            3 => maskedValue == reference,
            4 => maskedValue != reference,
            5 => maskedValue >= reference,
            6 => maskedValue > reference,
            _ => true,
        };
    }

    /// <summary>
    /// Direct guest CPU stores can satisfy a GPU wait without crossing another
    /// AGC import. Keep one low-frequency monitor per guest memory while waits
    /// exist so those real stores wake their queues. The monitor never changes
    /// a label: it uses the same masked comparison as submission-time parsing
    /// and resumes only after the guest value genuinely satisfies the packet.
    /// </summary>
    private static void EnsureGpuWaitMonitor(
        CpuContext submitContext,
        SubmittedGpuState gpuState)
    {
        if (gpuState.WaitMonitorRunning)
        {
            return;
        }

        gpuState.WaitMonitorRunning = true;
        var monitorContext = new CpuContext(
            submitContext.Memory,
            submitContext.TargetGeneration);
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => MonitorGpuWaits(state.Context, state.GpuState),
            (Context: monitorContext, GpuState: gpuState),
            preferLocal: false);
    }

    // Lets a stall snapshot show whether MonitorGpuWaits' loop is still alive.
    private static long _gpuWaitMonitorHeartbeatCount;
    private static long _gpuWaitMonitorHeartbeatTimestamp;

    public static (long Count, double SecondsSinceLastIteration) GpuWaitMonitorHeartbeat()
    {
        var count = Volatile.Read(ref _gpuWaitMonitorHeartbeatCount);
        var lastTicks = Volatile.Read(ref _gpuWaitMonitorHeartbeatTimestamp);
        var seconds = lastTicks == 0
            ? -1
            : (System.Diagnostics.Stopwatch.GetTimestamp() - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return (count, seconds);
    }

    private static void MonitorGpuWaits(
        CpuContext ctx,
        SubmittedGpuState gpuState)
    {
        var delayMilliseconds = 1;
        long observedSignal;
        lock (gpuState.WaitMonitorSignalGate)
        {
            observedSignal = gpuState.WaitMonitorSignalVersion;
        }

        while (true)
        {
            Interlocked.Increment(ref _gpuWaitMonitorHeartbeatCount);
            Volatile.Write(ref _gpuWaitMonitorHeartbeatTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            try
            {
            var madeProgress = false;
            lock (gpuState.Gate)
            {
                var before = GpuWaitRegistry.CountForMemory(ctx.Memory);
                // Under orphan force-submit, the monitor must outlive the
                // waits — the arena sweep below is the only thing that
                // catches CPU-side usleep polling with no WAIT_REG_MEM.
                if (before == 0 && !_forceSubmitOrphanPreamblesEnabled)
                {
                    gpuState.WaitMonitorRunning = false;
                    return;
                }

                var remaining = before;
                if (before != 0)
                {
                    var resumed = DrainResumableDcbs(ctx, gpuState, tracePackets: _traceAgc);
                    remaining = GpuWaitRegistry.CountForMemory(ctx.Memory);
                    madeProgress = resumed != 0;
                    if (_traceAgc && resumed != 0)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] agc.wait_monitor_resumed count={resumed} " +
                            $"remaining={remaining}");
                    }

                    SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TraceGpuWaitSnapshot(
                        ctx.Memory);
                    GpuWaitProfile.RecordMonitorPoll(resumed != 0);
                    GpuWaitProfile.ReportIfDue(remaining);
                    if (remaining == 0 && !_forceSubmitOrphanPreamblesEnabled)
                    {
                        gpuState.WaitMonitorRunning = false;
                        return;
                    }
                }
            }

            // Re-offering every live wait also covers producers the game
            // builds after the wait registered.
            if (_forceSubmitOrphanPreamblesEnabled)
            {
                foreach (var (address, _) in
                         GpuWaitRegistry.SnapshotInRange(ctx.Memory, 0, ulong.MaxValue))
                {
                    TryForceSubmitOrphanPreamble(ctx, gpuState, address);
                }

                DrainPendingOrphanPreambles(ctx, gpuState);
                SweepBuilderArenas(ctx, gpuState);
                SalvageStuckFenceWrites(ctx, gpuState);
            }

            delayMilliseconds = madeProgress
                ? 1
                : Math.Min(delayMilliseconds * 2, 16);
            lock (gpuState.WaitMonitorSignalGate)
            {
                if (gpuState.WaitMonitorSignalVersion == observedSignal)
                {
                    Monitor.Wait(gpuState.WaitMonitorSignalGate, delayMilliseconds);
                }

                observedSignal = gpuState.WaitMonitorSignalVersion;
            }
            }
            catch (Exception ex)
            {
                // No other supervisor: an unlogged exception here would
                // silently end AGC activity forever. Log and keep looping.
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] agc.wait_monitor_iteration_exception " +
                    $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                Thread.Sleep(16);
            }
        }
    }

    /// <summary>
    /// Writes a value that satisfies the waiter's comparison. This deliberately
    /// exists only behind SHARPEMU_GPU_WAIT_MODE=force for legacy A/B testing;
    /// normal and stale waits must never mutate their watched label.
    /// </summary>
    private static void ForceSatisfyGpuWait(
        CpuContext ctx,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong value)
    {
        var address = waiter.WaitAddress;
        var mask = waiter.Mask;
        if (address == 0 || mask == 0)
        {
            return;
        }

        var maskedRef = waiter.ReferenceValue & mask;
        ulong? satisfyMasked = waiter.CompareFunction switch
        {
            1 => maskedRef == 0 ? null : (maskedRef - 1) & mask,            // <
            2 => maskedRef,                                                 // <=
            3 => maskedRef,                                                 // ==
            4 => (~maskedRef) & mask,                                       // !=
            5 => maskedRef,                                                 // >=
            6 => maskedRef == mask ? null : (maskedRef + 1) & mask,         // >
            _ => null,
        };

        if (satisfyMasked is not { } satisfy)
        {
            return;
        }

        var newValue = (value & ~mask) | (satisfy & mask);
        if (waiter.Is64Bit)
        {
            ctx.TryWriteUInt64(address, newValue);
        }
        else
        {
            TryWriteUInt32(ctx, address, unchecked((uint)newValue));
        }
    }

    // WAIT_REG_MEM packets whose condition is not met suspend their DCB into
    // GpuWaitRegistry. Each submit re-checks every suspended DCB against current
    // guest memory (labels are advanced by ReleaseMem/WriteData/DmaData packets
    // or direct CPU writes) and resumes the ones now satisfied. A resumed DCB
    // can itself write labels that unblock others, so loop to a fixed point.
    private static int DrainResumableDcbs(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        bool tracePackets)
    {
        if (!_gpuWaitSuspendEnabled)
        {
            return 0;
        }

        var resumedCount = 0;
        for (var pass = 0; pass < 256; pass++)
        {
            var woken = GpuWaitRegistry.CollectSatisfied(ctx.Memory, (address, is64Bit) =>
                is64Bit
                    ? TryReadUInt64(ctx, address, out var value64) ? value64 : (ulong?)null
                    : TryReadUInt32(ctx, address, out var value32) ? value32 : (ulong?)null);

            // Indirect-dispatch dimension retries whose deadline elapsed are
            // resumed so they drop instead of stalling. Flag each so its immediate
            // re-parse drops the dispatch rather than suspending again.
            var expiredRetries = GpuWaitRegistry.CollectExpiredRetries(
                ctx.Memory, System.Diagnostics.Stopwatch.GetTimestamp());
            if (expiredRetries is not null)
            {
                lock (_indirectDimsGate)
                {
                    foreach (var retry in expiredRetries)
                    {
                        _indirectDimsExpired.Add((ctx.Memory, retry.ResumeAddress));
                    }
                }

                foreach (var retry in expiredRetries)
                {
                    ResumeSuspendedDcb(ctx, gpuState, retry, tracePackets);
                }
            }

            // Break cross-queue deadlocks: a waiter stuck past the deadline whose
            // label a real producer already signalled (but guest memory has since
            // been reset for reuse) is released using that produced value. Only
            // fires for genuinely wedged waits, so fast-resolving ones on working
            // titles are untouched.
            var deadlockBroken = GpuWaitRegistry.CollectDeadlockBroken(
                ctx.Memory, System.Diagnostics.Stopwatch.GetTimestamp(), _gpuDeadlockBreakTicks);
            if (deadlockBroken is not null)
            {
                foreach (var waiter in deadlockBroken)
                {
                    if (tracePackets)
                    {
                        TraceAgc(
                            $"agc.deadlock_break label=0x{waiter.WaitAddress:X16} " +
                            $"queue={waiter.QueueName} submission={waiter.SubmissionId}");
                    }

                    ResumeSuspendedDcb(ctx, gpuState, waiter, tracePackets);
                }
            }

            if (woken is null && expiredRetries is null && deadlockBroken is null)
            {
                if (_gpuWaitStaleTicks > 0 &&
                    GpuWaitRegistry.CollectUnreportedStale(
                        ctx.Memory,
                        System.Diagnostics.Stopwatch.GetTimestamp(),
                        _gpuWaitStaleTicks) is { } stale)
                {
                    foreach (var waiter in stale)
                    {
                        ulong? currentValue = waiter.Is64Bit
                            ? TryReadUInt64(ctx, waiter.WaitAddress, out var value64)
                                ? value64
                                : null
                            : TryReadUInt32(ctx, waiter.WaitAddress, out var value32)
                                ? value32
                                : null;
                        TraceWaitProducerState(
                            ctx.Memory,
                            waiter,
                            waiter.CommandBufferAddress,
                            waiter.ResumeAddress,
                            stale: true,
                            currentValue);
                    }
                }

                return resumedCount;
            }

            if (woken is not null)
            {
                foreach (var waiter in woken)
                {
                    ResumeSuspendedDcb(ctx, gpuState, waiter, tracePackets);
                    resumedCount++;
                }
            }
        }

        return resumedCount;
    }

    private static void ResumeSuspendedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        in GpuWaitRegistry.WaitingDcb waiter,
        bool tracePackets)
    {
        var state = waiter.State as SubmittedDcbState ?? gpuState.Graphics;
        if (state.IsFaulted)
        {
            return;
        }

        // Any resume ends a ring-tail park; SuspendOnUnwrittenRingWord re-arms
        // it if the continued parse parks again.
        state.RingTailParkAddress = 0;
        var remainingDwords = waiter.TotalDwords - waiter.ResumeOffset;
        var waitedMilliseconds = waiter.RegisteredTicks == 0
            ? 0.0
            : (System.Diagnostics.Stopwatch.GetTimestamp() - waiter.RegisteredTicks) *
              1000.0 / System.Diagnostics.Stopwatch.Frequency;
        TraceAgcShader(
            $"agc.queue_resumed queue={waiter.QueueName} " +
            $"submission={waiter.SubmissionId} label=0x{waiter.WaitAddress:X16} " +
            $"resume=0x{waiter.ResumeAddress:X16} remaining_dwords={remainingDwords} " +
            $"waited_ms={waitedMilliseconds:F3}");
        GpuWaitProfile.RecordResume(waiter.WaitAddress, waitedMilliseconds);
        if (remainingDwords == 0)
        {
            state.IsSuspended = false;
            state.HasActiveSubmission = false;
            GpuWaitRegistry.EndSubmission(
                waiter.Memory ?? ctx.Memory,
                state.ActiveSubmissionPublicationGeneration);
            state.ActiveSubmissionPublicationGeneration = 0;
            state.ActiveIndexSnapshots = null;
            state.ActiveVertexSnapshots = null;
            NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
            PumpSubmittedQueue(ctx, gpuState, state);
            return;
        }

        if (tracePackets)
        {
            TraceAgc(
                $"agc.dcb.resumed addr=0x{waiter.WaitAddress:X16} " +
                $"resume=0x{waiter.ResumeAddress:X16} dwords={remainingDwords} forced=False");
        }

        System.Diagnostics.Debug.Assert(state.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(state.IsSuspended);
        state.QueueName = waiter.QueueName ?? state.QueueName;
        state.ActiveSubmissionId = waiter.SubmissionId;
        state.IsSuspended = false;
        using var guestQueueScope = GuestGpu.Current.EnterGuestQueue(
            state.QueueName,
            state.ActiveSubmissionId);
        GuestGpu.Current.RequireGpuLabelDependency(waiter.Dependency);
        var isSuspended = ParseSubmittedDcb(
            ctx,
            gpuState,
            state,
            waiter.ResumeAddress,
            remainingDwords,
            tracePackets);
        if (state.IsFaulted)
        {
            return;
        }

        if (isSuspended)
        {
            state.IsSuspended = true;
            return;
        }

        state.HasActiveSubmission = false;
        GpuWaitRegistry.EndSubmission(
            waiter.Memory ?? ctx.Memory,
            state.ActiveSubmissionPublicationGeneration);
        state.ActiveSubmissionPublicationGeneration = 0;
        state.ActiveIndexSnapshots = null;
        state.ActiveVertexSnapshots = null;
        NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void TraceSubmittedWait(
        ulong address,
        ulong value,
        ulong mask,
        ulong reference,
        uint compareFunction,
        int bits,
        bool tracePacket)
    {
        var maskedValue = value & mask;
        var satisfied = compareFunction switch
        {
            0 => true,
            1 => maskedValue < reference,
            2 => maskedValue <= reference,
            3 => maskedValue == reference,
            4 => maskedValue != reference,
            5 => maskedValue >= reference,
            6 => maskedValue > reference,
            _ => true,
        };
        if (!tracePacket && (satisfied || !ShouldTraceHotPath(ref _unsatisfiedWaitTraceCount)))
        {
            return;
        }

        TraceAgc(
            $"agc.dcb.wait_reg_mem bits={bits} addr=0x{address:X16} " +
            $"value=0x{value:X16} mask=0x{mask:X16} ref=0x{reference:X16} " +
            $"compare={compareFunction} satisfied={satisfied}");
    }

}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.VideoOut;

// Keep each flip until completion and presentation finish.
// Cancellation removes an abandoned request without publishing completion.
public static partial class VideoOutExports
{
    private enum FlipRequestState
    {
        Reserved,
        Completed,
    }

    internal enum FlipOutcome
    {
        Pending,
        Presented,
        Discarded,
        Cancelled,
    }

    private sealed class FlipRequest
    {
        public ulong RequestId;
        public int Handle;
        public int BufferIndex;
        public int FlipMode;
        public long FlipArg;
        public bool GpuQueued;
        public ulong EventHint;
        public FlipEventRegistration[]? FlipEvents;
        public int FlipEventCount;
        public FlipRequestState State;
        public FlipOutcome Outcome;
    }

    private static readonly Dictionary<ulong, FlipRequest> _flipRequests = new();
    private static ulong _nextFlipRequestId;
    private static long _discardedFlipCount;

    // The flip target of hosts that present without a GPU tick: a completed flip is presented at once.
    private sealed class CommandStreamFlipTarget : ICommandStreamFlipTarget
    {
        public ulong Prepare(int handle, int index, int flipMode, long flipArgument)
        {
            var result = TryReserveFlipRequest(handle, index, flipMode, flipArgument, gpuQueued: true, out var requestId);
            if (result != 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"Could not submit the GPU flip: result=0x{result:X8} handle={handle} index={index} mode={flipMode} arg={flipArgument}");
            }

            return requestId;
        }

        public void Complete(ulong requestId)
        {
            CompleteFlip(requestId);
            MarkFlipPresented(requestId);
        }

        public void Presented(ulong requestId) => MarkFlipPresented(requestId);

        public bool IsDone(int handle, int index) => IsFlipDone(handle, index);
    }

    internal static ICommandStreamFlipTarget HeadlessFlipTarget { get; } = new CommandStreamFlipTarget();

    // Validates the flip and takes its counters; returns a video-out error or zero.
    internal static int TryReserveFlipRequest(
        int handle,
        int bufferIndex,
        int flipMode,
        long flipArg,
        bool gpuQueued,
        out ulong requestId)
    {
        requestId = 0;
        if (bufferIndex < -1 || bufferIndex >= MaxDisplayBuffers)
        {
            return OrbisVideoOutErrorInvalidIndex;
        }

        FlipRequest request;
        lock (_stateGate)
        {
            if (!_ports.TryGetValue(handle, out var port))
            {
                return OrbisVideoOutErrorInvalidHandle;
            }

            if (bufferIndex != -1 && port.BufferSlots[bufferIndex].GroupIndex < 0)
            {
                return OrbisVideoOutErrorInvalidIndex;
            }

            var submittedAt = Stopwatch.GetTimestamp();
            if (port.LatencyStartPoints.Remove(flipArg, out var startedAt))
            {
                port.LastLatencyFirstSectionUsec = (long)Math.Max(
                    0,
                    Math.Floor((submittedAt - startedAt) * 1_000_000d / Stopwatch.Frequency));
            }

            port.FlipPendingCount++;
            if (gpuQueued)
            {
                port.GpuQueueCount++;
            }

            port.SubmitProcessTimeCounter = KernelRuntimeCompatExports.ReadProcessTimeCounter();
            request = new FlipRequest
            {
                RequestId = ++_nextFlipRequestId,
                Handle = handle,
                BufferIndex = bufferIndex,
                FlipMode = flipMode,
                FlipArg = flipArg,
                GpuQueued = gpuQueued,
                EventHint = SceVideoOutInternalEventFlip |
                    ((unchecked((ulong)flipArg) & 0x0000_FFFF_FFFF_FFFFUL) << 16),
                FlipEventCount = port.FlipEvents.Count,
                State = FlipRequestState.Reserved,
                Outcome = FlipOutcome.Pending,
            };
            if (request.FlipEventCount != 0)
            {
                // Reuse event storage. Trigger events outside the state lock.
                request.FlipEvents = ArrayPool<FlipEventRegistration>.Shared.Rent(request.FlipEventCount);
                port.FlipEvents.CopyTo(request.FlipEvents);
            }

            _flipRequests.Add(request.RequestId, request);
        }

        if (bufferIndex >= 0 && TryGetDisplayBufferInfo(handle, bufferIndex, out var displayBuffer))
        {
            Interlocked.Exchange(ref _hdrOutputRequested, IsHdrPixelFormat(displayBuffer.PixelFormat) ? 1 : 0);
        }

        requestId = request.RequestId;
        return 0;
    }

    internal static bool IsFlipPresentationPending(ulong requestId)
    {
        if (requestId == 0)
        {
            return true;
        }
        lock (_stateGate)
        {
            return _flipRequests.TryGetValue(requestId, out var request) &&
                request.Outcome == FlipOutcome.Pending && _ports.ContainsKey(request.Handle);
        }
    }

    // Submission does not reserve a future refresh. Only presentation consumes a refresh interval.
    internal static bool CanPresentFlip(ulong requestId, long timestamp)
    {
        if (requestId == 0)
        {
            return true;
        }
        lock (_stateGate)
        {
            if (!_flipRequests.TryGetValue(requestId, out var request) ||
                request.Outcome != FlipOutcome.Pending || !_ports.TryGetValue(request.Handle, out var port))
            {
                return false;
            }
            return _flipPacingDisabled || !request.GpuQueued ||
                IsRefreshAvailable(port.OpenTimestamp, port.LastPresentationTimestamp, timestamp, port.RefreshRate, port.FlipRate);
        }
    }

    internal static bool IsRefreshAvailable(long openedAt, long lastPresentedAt, long timestamp, uint refreshRate, int flipRate)
    {
        if (lastPresentedAt < 0)
        {
            return true;
        }
        var refreshInterval = Math.Max(1L, Stopwatch.Frequency / Math.Max(1L, refreshRate));
        var flipInterval = refreshInterval * Math.Max(1L, (long)flipRate + 1);
        return Math.Max(0, timestamp - openedAt) / flipInterval >
            Math.Max(0, lastPresentedAt - openedAt) / flipInterval;
    }

    // Publish completion counters and events unless the request was cancelled.
    internal static void CompleteFlip(ulong requestId)
    {
        FlipRequest request;
        lock (_stateGate)
        {
            if (!_flipRequests.TryGetValue(requestId, out request!) || request.State != FlipRequestState.Reserved)
            {
                return;
            }

            request.State = FlipRequestState.Completed;
            if (_ports.TryGetValue(request.Handle, out var port))
            {
                port.FlipCount++;
                port.FlipProcessTime = KernelRuntimeCompatExports.ReadProcessTimeMicroseconds();
                port.FlipProcessTimeCounter = KernelRuntimeCompatExports.ReadProcessTimeCounter();
                port.FlipArg = request.FlipArg;
                port.CurrentBuffer = request.BufferIndex;
                port.FlipPendingCount = Math.Max(0, port.FlipPendingCount - 1);
                if (request.GpuQueued)
                {
                    port.GpuQueueCount = Math.Max(0, port.GpuQueueCount - 1);
                }

                var completedAt = Stopwatch.GetTimestamp();
                port.CompletedLatencyFlipArgs[request.FlipArg] = completedAt;
                port.CompletedLatencyFlipArgOrder.Enqueue((request.FlipArg, completedAt));
                PruneTimestampHistory(port.CompletedLatencyFlipArgs, port.CompletedLatencyFlipArgOrder);
            }

            RemoveIfFinishedLocked(request);
            Monitor.PulseAll(_stateGate);
        }

        var flipEvents = request.FlipEvents;
        if (flipEvents is null)
        {
            return;
        }

        try
        {
            for (var eventIndex = 0; eventIndex < request.FlipEventCount; eventIndex++)
            {
                _ = KernelEventQueueCompatExports.TriggerDisplayEvent(
                    flipEvents[eventIndex].Equeue,
                    SceVideoOutInternalEventFlip,
                    OrbisKernelEventFilterVideoOut,
                    request.EventHint,
                    flipEvents[eventIndex].UserData);
            }
        }
        finally
        {
            ArrayPool<FlipEventRegistration>.Shared.Return(flipEvents);
            request.FlipEvents = null;
        }
    }

    // The presenter showed the frame.
    internal static void MarkFlipPresented(ulong requestId) => SetFlipOutcome(requestId, FlipOutcome.Presented);

    // The presenter will never show the frame (dropped, trimmed, device lost, window gone).
    internal static void DiscardFlip(ulong requestId)
    {
        if (SetFlipOutcome(requestId, FlipOutcome.Discarded))
        {
            Interlocked.Increment(ref _discardedFlipCount);
        }
    }

    private static bool SetFlipOutcome(ulong requestId, FlipOutcome outcome)
    {
        lock (_stateGate)
        {
            if (!_flipRequests.TryGetValue(requestId, out var request) || request.Outcome != FlipOutcome.Pending)
            {
                return false;
            }

            request.Outcome = outcome;
            if (outcome == FlipOutcome.Presented && request.BufferIndex >= 0 &&
                _ports.TryGetValue(request.Handle, out var port))
            {
                port.LastPresentationTimestamp = Stopwatch.GetTimestamp();
            }
            RemoveIfFinishedLocked(request);
            Monitor.PulseAll(_stateGate);
            return true;
        }
    }

    // Both responsibilities are done: the retirement ran and the presenter released the frame.
    private static void RemoveIfFinishedLocked(FlipRequest request)
    {
        if (request.State == FlipRequestState.Completed && request.Outcome != FlipOutcome.Pending)
        {
            _flipRequests.Remove(request.RequestId);
        }
    }

    // True when no flip still waits for the presenter on the buffer; a discarded or cancelled flip no longer does.
    internal static bool IsFlipDone(int handle, int bufferIndex)
    {
        lock (_stateGate)
        {
            foreach (var request in _flipRequests.Values)
            {
                if (request.Handle == handle && request.BufferIndex == bufferIndex && request.Outcome == FlipOutcome.Pending)
                {
                    return false;
                }
            }

            return true;
        }
    }

    // Teardown cancels reservations without publishing successful completion.
    internal static void CancelOutstandingFlips()
    {
        lock (_stateGate)
        {
            foreach (var request in _flipRequests.Values.ToArray())
            {
                CancelFlipLocked(request);
            }

            Monitor.PulseAll(_stateGate);
        }
    }

    internal static void CancelFlip(ulong requestId)
    {
        lock (_stateGate)
        {
            if (_flipRequests.TryGetValue(requestId, out var request))
            {
                CancelFlipLocked(request);
            }
            Monitor.PulseAll(_stateGate);
        }
    }

    private static void CancelFlipLocked(FlipRequest request)
    {
        if (request.State == FlipRequestState.Reserved)
        {
            if (_ports.TryGetValue(request.Handle, out var port))
            {
                port.FlipPendingCount = Math.Max(0, port.FlipPendingCount - 1);
                if (request.GpuQueued)
                {
                    port.GpuQueueCount = Math.Max(0, port.GpuQueueCount - 1);
                }
            }
            if (request.FlipEvents is { } flipEvents)
            {
                ArrayPool<FlipEventRegistration>.Shared.Return(flipEvents);
                request.FlipEvents = null;
            }
        }
        // Completed requests leave event-array ownership with the completion caller.
        request.Outcome = FlipOutcome.Cancelled;
        _flipRequests.Remove(request.RequestId);
    }

    internal static bool ReleaseNoBufferFlip(int bufferIndex, ulong requestId)
    {
        if (bufferIndex != -1) return false;
        MarkFlipPresented(requestId);
        return true;
    }

    internal static int PendingFlipRequestCount
    {
        get
        {
            lock (_stateGate)
            {
                return _flipRequests.Count;
            }
        }
    }

    internal static long DiscardedFlipCount => Interlocked.Read(ref _discardedFlipCount);

    internal static FlipOutcome? GetFlipOutcomeForTests(ulong requestId)
    {
        lock (_stateGate)
        {
            return _flipRequests.TryGetValue(requestId, out var request) ? request.Outcome : null;
        }
    }

    // Diagnostics for a GPU flip; the CPU export path traces in its own body.
    internal static void TraceGpuFlip(ICpuMemory memory, int handle, int bufferIndex, int flipMode, long flipArg, ulong address, int flipEventCount)
    {
        if (_dumpVideoOut && TryGetPort(handle, out var port))
        {
            _ = TryDumpFrame(memory, port, bufferIndex, flipMode, flipArg);
        }

        PerfOverlay.RecordSubmit();
        TraceVideoOut(
            $"videoout.submit_flip handle={handle} index={bufferIndex} mode={flipMode} " +
            $"arg={flipArg} addr=0x{address:X16} submitted=True events={flipEventCount} ordered_completion=True");
        LoadProgressDiagnostics.TraceFlipSubmit(handle, bufferIndex, flipMode, false, true, address, flipEventCount);
        LoadProgressDiagnostics.TraceGpuWaitSnapshot(memory);
        ReportFrameRate(presented: false);
    }

    internal static int GetFlipEventCount(ulong requestId)
    {
        lock (_stateGate)
        {
            return _flipRequests.TryGetValue(requestId, out var request) ? request.FlipEventCount : 0;
        }
    }
}

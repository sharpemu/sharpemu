// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Threading;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Diagnostics;

/// <summary>
/// Rate-limited progress probes armed when GTA's 'North Audio Update' thread
/// starts. Used to classify North Yankton freezes (flip vs present vs GPU wait)
/// without enabling full AGC/VideoOut trace.
/// </summary>
public static class LoadProgressDiagnostics
{
    // Keep probes live long enough to cover a stuck Yankton session.
    private const long ActiveWindowMs = 120_000;

    private static long _armedTicks;
    private static long _flipSubmitTraceCount;
    private static long _orderedFlipEnqueueTraceCount;
    private static long _presentTakenTraceCount;
    private static long _presentNotTakenTraceCount;
    private static long _gpuWaitSnapshotTraceCount;

    public static void ArmIfNorthAudioThread(string? threadName)
    {
        if (string.IsNullOrEmpty(threadName) ||
            threadName.IndexOf("North Audio", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _armedTicks,
                Stopwatch.GetTimestamp(),
                0) == 0)
        {
            Console.Error.WriteLine(
                "[LOADER][TRACE] load_progress.armed reason=north_audio " +
                $"window_ms={ActiveWindowMs}");
        }
    }

    public static bool IsActive
    {
        get
        {
            var armed = Volatile.Read(ref _armedTicks);
            if (armed == 0)
            {
                return false;
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - armed) * 1000L /
                Stopwatch.Frequency;
            return elapsedMs <= ActiveWindowMs;
        }
    }

    public static void TraceFlipSubmit(
        int handle,
        int bufferIndex,
        int flipMode,
        bool submitGpuImage,
        bool guestImageSubmitted,
        ulong guestImageAddress,
        int flipEventCount)
    {
        if (!IsActive || !ShouldTrace(ref _flipSubmitTraceCount, out var count))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] load_progress.flip_submit count={count} " +
            $"handle={handle} index={bufferIndex} mode={flipMode} " +
            $"gpu_image={submitGpuImage} submitted={guestImageSubmitted} " +
            $"addr=0x{guestImageAddress:X16} events={flipEventCount}");
    }

    public static void TraceOrderedFlipEnqueue(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        long version,
        bool enqueued)
    {
        if (!IsActive ||
            !ShouldTrace(ref _orderedFlipEnqueueTraceCount, out var count))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] load_progress.ordered_flip count={count} " +
            $"handle={videoOutHandle} index={displayBufferIndex} " +
            $"addr=0x{address:X16} version={version} enqueued={enqueued}");
    }

    public static void TracePresentTaken(
        long presentedSequence,
        ulong guestImageAddress,
        long guestImageVersion)
    {
        if (!IsActive || !ShouldTrace(ref _presentTakenTraceCount, out var count))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] load_progress.present_taken count={count} " +
            $"seq={presentedSequence} addr=0x{guestImageAddress:X16} " +
            $"version={guestImageVersion}");
    }

    public static void TracePresentNotTaken(
        long presentedSequence,
        bool hasPendingPresentation)
    {
        if (!IsActive ||
            !ShouldTrace(ref _presentNotTakenTraceCount, out var count))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] load_progress.present_not_taken count={count} " +
            $"seq={presentedSequence} pending={hasPendingPresentation}");
    }

    public static void TraceGpuWaitSnapshot(object? memory = null)
    {
        if (!IsActive ||
            !ShouldTrace(ref _gpuWaitSnapshotTraceCount, out var count))
        {
            return;
        }

        // Blocked command-stream heads replaced the wait registry; the trace keeps its shape.
        var snapshot = VulkanVideoPresenter.SnapshotBlockedCommandStream(memory as SharpEmu.HLE.ICpuMemory);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] load_progress.gpu_waits count={count} " +
            $"outstanding={snapshot?.Outstanding ?? 0} latched=0 " +
            $"oldest_ms={snapshot?.OldestAgeMilliseconds ?? 0:F0} " +
            $"sample_addr=0x{snapshot?.SampleWaitAddress ?? 0:X16} " +
            $"sample_queue={(snapshot is { SampleQueueId: >= 0 } sample ? (sample.SampleQueueId == 0 ? "dcb.graphics" : $"acb.compute[{0x20 + sample.SampleQueueId - 1}]") : "-")}");
    }

    private static bool ShouldTrace(ref long counter, out long count)
    {
        count = Interlocked.Increment(ref counter);
        return count <= 16 || (count & (count - 1)) == 0;
    }
}

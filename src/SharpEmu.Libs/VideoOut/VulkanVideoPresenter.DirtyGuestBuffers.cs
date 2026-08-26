// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial publishes dirty guest buffer ranges to guest memory.

    private static readonly bool _traceGlobalWritebackTiming =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GLOBAL_WRITEBACK_TIMING"),
            "1",
            StringComparison.Ordinal);

    private static readonly bool _indexDirtyGuestBuffers = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_INDEX_DIRTY_GUEST_BUFFERS"),
        "0",
        StringComparison.Ordinal);

    private sealed partial class Presenter
    {
        private readonly DirtyGuestBufferIndex<GuestBufferAllocation>
            _dirtyGuestBufferIndex = new();
        private readonly List<GuestBufferAllocation> _dirtyGuestBufferCandidates = [];

        private readonly HashSet<(ulong Address, ulong Size)> _tracedGlobalWritebacks = new();
        private int _tracedSmallGlobalWritebackEvents;
        private int _tracedLargeGlobalWritebackEvents;

        private IReadOnlyList<GuestBufferAllocation> GetDirtyGuestBufferCandidates(
            string? queueName)
        {
            if (!_indexDirtyGuestBuffers)
            {
                return _guestBufferAllocations;
            }

            _dirtyGuestBufferIndex.CopyCandidates(queueName, _dirtyGuestBufferCandidates);
            return _dirtyGuestBufferCandidates;
        }

        private void IndexDirtyGuestBuffer(
            GuestBufferAllocation allocation,
            string queueName)
        {
            if (_indexDirtyGuestBuffers)
            {
                _dirtyGuestBufferIndex.Mark(allocation, queueName);
            }
        }

        private void RefreshDirtyGuestBufferIndex(
            GuestBufferAllocation allocation,
            string? queueName)
        {
            if (!_indexDirtyGuestBuffers)
            {
                return;
            }

            if (queueName is not null)
            {
                if (!allocation.DirtyRanges.Any(range =>
                        string.Equals(range.QueueName, queueName, StringComparison.Ordinal)))
                {
                    _dirtyGuestBufferIndex.Remove(allocation, queueName);
                }

                return;
            }

            _dirtyGuestBufferIndex.Remove(allocation);
            foreach (var remainingQueueName in allocation.DirtyRanges
                         .Select(static range => range.QueueName)
                         .Distinct(StringComparer.Ordinal))
            {
                _dirtyGuestBufferIndex.Mark(allocation, remainingQueueName);
            }
        }

        private void ForgetDirtyGuestBuffer(GuestBufferAllocation allocation)
        {
            if (_indexDirtyGuestBuffers)
            {
                _dirtyGuestBufferIndex.Remove(allocation);
            }
        }

        private void MarkGuestBufferDirty(
            GuestBufferAllocation allocation,
            ulong offset,
            ulong length,
            string queueName,
            ulong timeline)
        {
            if (length == 0)
            {
                return;
            }

            var start = offset;
            var end = checked(offset + length);
            for (var index = allocation.DirtyRanges.Count - 1; index >= 0; index--)
            {
                var existing = allocation.DirtyRanges[index];
                if (!string.Equals(existing.QueueName, queueName, StringComparison.Ordinal))
                {
                    continue;
                }

                var existingEnd = existing.Offset + existing.Length;
                if (end < existing.Offset || existingEnd < start)
                {
                    continue;
                }

                start = Math.Min(start, existing.Offset);
                end = Math.Max(end, existingEnd);
                timeline = Math.Max(timeline, existing.Timeline);
                allocation.DirtyRanges.RemoveAt(index);
            }

            allocation.DirtyRanges.Add(
                new DirtyGuestBufferRange(start, end - start, queueName, timeline));
            IndexDirtyGuestBuffer(allocation, queueName);
        }

        private void WriteBackAllDirtyGuestBuffers(string? queueName = null)
        {
            var memory = _guestMemory;
            if (memory is null)
            {
                return;
            }

            var candidates = GetDirtyGuestBufferCandidates(queueName);
            foreach (var allocation in candidates)
            {
                for (var index = allocation.DirtyRanges.Count - 1; index >= 0; index--)
                {
                    var range = allocation.DirtyRanges[index];
                    if ((queueName is not null &&
                         !string.Equals(range.QueueName, queueName, StringComparison.Ordinal)) ||
                        range.Timeline > _completedTimeline)
                    {
                        continue;
                    }

                    if (range.Length == 0 || range.Length > int.MaxValue)
                    {
                        continue;
                    }

                    var rangeEnd = checked(range.Offset + range.Length);
                    var overlapsInFlightWrite = false;
                    for (var otherIndex = 0;
                         otherIndex < allocation.DirtyRanges.Count;
                         otherIndex++)
                    {
                        if (otherIndex == index)
                        {
                            continue;
                        }

                        var other = allocation.DirtyRanges[otherIndex];
                        if (other.Timeline <= _completedTimeline)
                        {
                            continue;
                        }

                        var otherEnd = checked(other.Offset + other.Length);
                        if (range.Offset < otherEnd && other.Offset < rangeEnd)
                        {
                            overlapsInFlightWrite = true;
                            break;
                        }
                    }

                    if (overlapsInFlightWrite)
                    {
                        continue;
                    }

                    var mappedBytes = new ReadOnlySpan<byte>(
                        (void*)(allocation.Mapped + checked((nint)range.Offset)),
                        checked((int)range.Length));
                    var shadowBytes = allocation.Shadow.AsSpan(
                        checked((int)range.Offset),
                        mappedBytes.Length);
                    var guestAddress = allocation.BaseAddress + range.Offset;
                    var changedBytes = 0UL;
                    var changedRuns = 0;
                    var changedPages = 0;
                    var writtenRuns = 0;
                    var writtenPages = 0;
                    var failedRuns = 0;
                    var unreadablePages = 0;
                    var fallbackWrites = 0;
                    var firstChangedOffset = -1;
                    var scanTicks = 0L;
                    var ioTicks = 0L;
                    allocation.DirtyRanges.RemoveAt(index);

                    // A writable descriptor only identifies a potential write
                    // range. Publishing the entire mapped view would overwrite
                    // unrelated live CPU data with its old snapshot. Compare
                    // against the last synchronized image. For each changed
                    // page, start with current guest bytes and overlay only the
                    // shader changes before one bounded write. This preserves
                    // live CPU changes in unchanged bytes without degenerating
                    // into millions of writes for alternating output patterns.
                    const int pageSize = 4096;
                    const int unreadableMergeGap = 16;
                    const int FragmentationRunThreshold = 64;
                    var livePageBuffer = GuestDataPool.Shared.Rent(pageSize);
                    var mappedPageBuffer = GuestDataPool.Shared.Rent(pageSize);
                    var pageRuns = new List<(int Start, int Length)>(64);
                    var coalescedPageRun = new List<(int Start, int Length)>(1);
                    try
                    {
                        for (var pageStart = 0;
                             pageStart < mappedBytes.Length;
                             pageStart += pageSize)
                        {
                            var pageEnd = Math.Min(pageStart + pageSize, mappedBytes.Length);
                            var pageLength = pageEnd - pageStart;
                            var mappedPageSource = mappedBytes.Slice(pageStart, pageLength);
                            var shadowPage = shadowBytes.Slice(pageStart, pageLength);
                            if (mappedPageSource.SequenceEqual(shadowPage))
                            {
                                continue;
                            }

                            // HOST_COHERENT mappings are commonly uncached or
                            // write-combined on the CPU. Read each changed page
                            // once with a bulk copy, then perform the byte-level
                            // merge against ordinary cached memory.
                            var mappedPage = mappedPageBuffer.AsSpan(0, pageLength);
                            mappedPageSource.CopyTo(mappedPage);
                            pageRuns.Clear();
                            var scanStartTicks = _traceGlobalWritebackTiming
                                ? System.Diagnostics.Stopwatch.GetTimestamp()
                                : 0L;

                            const int coarseBlockSize = 128;
                            var coarseBlockCount = 0;
                            var coarseDiffBlockCount = 0;
                            for (var blockStart = 0; blockStart < pageLength; blockStart += coarseBlockSize)
                            {
                                var blockEnd = Math.Min(blockStart + coarseBlockSize, pageLength);
                                coarseBlockCount++;
                                if (!mappedPage.Slice(blockStart, blockEnd - blockStart).SequenceEqual(
                                        shadowPage.Slice(blockStart, blockEnd - blockStart)))
                                {
                                    coarseDiffBlockCount++;
                                }
                            }

                            if (coarseBlockCount > 0 && coarseDiffBlockCount * 4 >= coarseBlockCount)
                            {
                                pageRuns.Add((pageStart, pageLength));
                                changedRuns++;
                                changedBytes += (ulong)pageLength;
                                if (firstChangedOffset < 0)
                                {
                                    firstChangedOffset = pageStart;
                                }
                            }
                            else if (coarseDiffBlockCount > 0)
                            {
                                var cursor = 0;
                                while (cursor < pageLength)
                                {
                                    cursor = SkipEqualBytes(mappedPage, shadowPage, cursor, pageLength);

                                    if (cursor == pageLength)
                                    {
                                        break;
                                    }

                                    var runStart = cursor;
                                    cursor = SkipDifferentBytes(mappedPage, shadowPage, cursor, pageLength);

                                    var runLength = cursor - runStart;
                                    pageRuns.Add((pageStart + runStart, runLength));
                                    changedRuns++;
                                    changedBytes += (ulong)runLength;
                                    if (firstChangedOffset < 0)
                                    {
                                        firstChangedOffset = pageStart + runStart;
                                    }
                                }
                            }

                            if (_traceGlobalWritebackTiming)
                            {
                                scanTicks += System.Diagnostics.Stopwatch.GetTimestamp() - scanStartTicks;
                            }

                            if (pageRuns.Count == 0)
                            {
                                continue;
                            }

                            List<(int Start, int Length)> runsToWrite;
                            if (pageRuns.Count > FragmentationRunThreshold)
                            {
                                coalescedPageRun.Clear();
                                coalescedPageRun.Add((
                                    pageRuns[0].Start,
                                    pageRuns[^1].Start + pageRuns[^1].Length - pageRuns[0].Start));
                                runsToWrite = coalescedPageRun;
                            }
                            else
                            {
                                runsToWrite = pageRuns;
                            }

                            changedPages++;
                            var livePage = livePageBuffer.AsSpan(0, pageLength);
                            var ioStartTicks = _traceGlobalWritebackTiming
                                ? System.Diagnostics.Stopwatch.GetTimestamp()
                                : 0L;
                            var readOk = memory.TryRead(guestAddress + (ulong)pageStart, livePage);
                            if (_traceGlobalWritebackTiming)
                            {
                                ioTicks += System.Diagnostics.Stopwatch.GetTimestamp() - ioStartTicks;
                            }

                            if (readOk)
                            {
                                foreach (var run in runsToWrite)
                                {
                                    mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
                                        livePage.Slice(run.Start - pageStart, run.Length));
                                }

                                var writeStartTicks = _traceGlobalWritebackTiming
                                    ? System.Diagnostics.Stopwatch.GetTimestamp()
                                    : 0L;
                                var writeOk = memory.TryWrite(guestAddress + (ulong)pageStart, livePage);
                                if (_traceGlobalWritebackTiming)
                                {
                                    ioTicks += System.Diagnostics.Stopwatch.GetTimestamp() - writeStartTicks;
                                }

                                if (writeOk)
                                {
                                    foreach (var run in runsToWrite)
                                    {
                                        mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
                                            shadowBytes.Slice(run.Start, run.Length));
                                    }

                                    writtenPages++;
                                    writtenRuns += runsToWrite.Count;
                                    continue;
                                }

                                foreach (var run in runsToWrite)
                                {
                                    failedRuns++;
                                    MarkGuestBufferDirty(
                                        allocation,
                                        range.Offset + (ulong)run.Start,
                                        (ulong)run.Length,
                                        range.QueueName,
                                        range.Timeline);
                                }

                                continue;
                            }

                            // A partial/unreadable edge cannot be safely
                            // reconstructed as a page. Fall back to bounded
                            // changed spans, coalescing only tiny gaps.
                            unreadablePages++;
                            for (var runIndex = 0; runIndex < pageRuns.Count; runIndex++)
                            {
                                var firstRunIndex = runIndex;
                                var mergedStart = pageRuns[runIndex].Start;
                                var mergedEnd = mergedStart + pageRuns[runIndex].Length;
                                while (runIndex + 1 < pageRuns.Count &&
                                       pageRuns[runIndex + 1].Start - mergedEnd <=
                                       unreadableMergeGap)
                                {
                                    runIndex++;
                                    mergedEnd = pageRuns[runIndex].Start +
                                        pageRuns[runIndex].Length;
                                }

                                var lastRunIndex = runIndex;
                                var mergedLength = mergedEnd - mergedStart;
                                var mergedLive = livePageBuffer.AsSpan(0, mergedLength);
                                if (memory.TryRead(
                                        guestAddress + (ulong)mergedStart,
                                        mergedLive))
                                {
                                    for (var overlayIndex = firstRunIndex;
                                         overlayIndex <= lastRunIndex;
                                         overlayIndex++)
                                    {
                                        var run = pageRuns[overlayIndex];
                                        mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
                                            mergedLive.Slice(
                                                run.Start - mergedStart,
                                                run.Length));
                                    }

                                    fallbackWrites++;
                                    if (memory.TryWrite(
                                            guestAddress + (ulong)mergedStart,
                                            mergedLive))
                                    {
                                        for (var overlayIndex = firstRunIndex;
                                             overlayIndex <= lastRunIndex;
                                             overlayIndex++)
                                        {
                                            var run = pageRuns[overlayIndex];
                                            mappedPage.Slice(run.Start - pageStart, run.Length).CopyTo(
                                                shadowBytes.Slice(run.Start, run.Length));
                                        }

                                        writtenRuns += lastRunIndex - firstRunIndex + 1;
                                        continue;
                                    }
                                }

                                // Even the merged span crosses an unreadable
                                // edge. Exact changed runs remain safe because
                                // they never carry stale gap bytes.
                                for (var exactIndex = firstRunIndex;
                                     exactIndex <= lastRunIndex;
                                     exactIndex++)
                                {
                                    var run = pageRuns[exactIndex];
                                    var changed = mappedPage.Slice(
                                        run.Start - pageStart,
                                        run.Length);
                                    fallbackWrites++;
                                    if (memory.TryWrite(
                                            guestAddress + (ulong)run.Start,
                                            changed))
                                    {
                                        changed.CopyTo(shadowBytes.Slice(
                                            run.Start,
                                            run.Length));
                                        writtenRuns++;
                                    }
                                    else
                                    {
                                        failedRuns++;
                                        MarkGuestBufferDirty(
                                            allocation,
                                            range.Offset + (ulong)run.Start,
                                            (ulong)run.Length,
                                            range.QueueName,
                                            range.Timeline);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        GuestDataPool.Shared.Return(livePageBuffer);
                        GuestDataPool.Shared.Return(mappedPageBuffer);
                    }

                    var probe = mappedBytes[..Math.Min(mappedBytes.Length, 256)];
                    var nonzero = 0;
                    foreach (var value in probe)
                    {
                        nonzero += value == 0 ? 0 : 1;
                    }

                    var firstForRange = _tracedGlobalWritebacks.Count < 256 &&
                        _tracedGlobalWritebacks.Add((guestAddress, range.Length));
                    var traceSmallMutation = range.Length <= 4096 &&
                        _tracedSmallGlobalWritebackEvents++ < 1024;
                    var traceLargeMutation = range.Length >= 1024 * 1024 &&
                        _tracedLargeGlobalWritebackEvents++ < 256;
                    if (firstForRange || traceSmallMutation || traceLargeMutation)
                    {
                        var head = firstChangedOffset >= 0
                            ? mappedBytes.Slice(
                                firstChangedOffset,
                                Math.Min(mappedBytes.Length - firstChangedOffset, 32))
                            : ReadOnlySpan<byte>.Empty;
                        TraceVulkanShader(
                            $"vk.global_writeback base=0x{guestAddress:X16} " +
                            $"potential_bytes={mappedBytes.Length} changed_bytes={changedBytes} " +
                            $"changed_runs={changedRuns} changed_pages={changedPages} " +
                            $"written_pages={writtenPages} written_runs={writtenRuns} " +
                            $"unreadable_pages={unreadablePages} " +
                            $"fallback_writes={fallbackWrites} failed_runs={failedRuns} " +
                            $"probe_nonzero={nonzero}/{probe.Length} " +
                            $"changed_head={Convert.ToHexString(head)}");
                    }

                    if (_traceGlobalWritebackTiming && changedRuns > 0)
                    {
                        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
                        Console.Error.WriteLine(
                            $"[LOADER][ERROR] vk.global_writeback_timing base=0x{guestAddress:X16} " +
                            $"changed_runs={changedRuns} changed_pages={changedPages} " +
                            $"scan_ms={(scanTicks * 1000.0 / freq).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
                            $"io_ms={(ioTicks * 1000.0 / freq).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                    }
                }

                RefreshDirtyGuestBufferIndex(allocation, queueName);
            }
        }

        private static int SkipEqualBytes(
            ReadOnlySpan<byte> a,
            ReadOnlySpan<byte> b,
            int start,
            int end)
        {
            var cursor = start;
            var vectorSize = System.Numerics.Vector<byte>.Count;
            while (cursor + vectorSize <= end)
            {
                var va = new System.Numerics.Vector<byte>(a.Slice(cursor, vectorSize));
                var vb = new System.Numerics.Vector<byte>(b.Slice(cursor, vectorSize));
                if (va != vb)
                {
                    break;
                }

                cursor += vectorSize;
            }

            while (cursor < end && a[cursor] == b[cursor])
            {
                cursor++;
            }

            return cursor;
        }

        private static int SkipDifferentBytes(
            ReadOnlySpan<byte> a,
            ReadOnlySpan<byte> b,
            int start,
            int end)
        {
            var cursor = start;
            while (cursor < end && a[cursor] != b[cursor])
            {
                cursor++;
            }

            return cursor;
        }



    }
}

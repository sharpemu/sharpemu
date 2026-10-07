// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Buffers;

// A mapped ring; each commit records the tick that may still read the committed bytes.
public sealed class GpuRingBuffer : GpuBuffer
{
    private const int WatchesInitialReserve = 0x4000;
    private const int WatchesReserveChunk = 0x1000;

    private struct RingWatch
    {
        public ulong Tick;
        public ulong UpperBound;
        public ulong LastReservation;
    }

    private ulong _offset;
    private ulong _mappedSize;
    private RingWatch[] _currentWatches = new RingWatch[WatchesInitialReserve];
    private RingWatch[] _previousWatches = new RingWatch[WatchesInitialReserve];
    private int _currentWatchCursor;
    private int? _invalidationMark;
    private int _waitCursor;
    private ulong _waitBound;
    private int _retainedContents;
    private ulong _committedReservations;
    private readonly List<AllocationRetention> _allocationRetentions = [];

    private sealed class AllocationRetention(GpuRingBuffer owner, ulong startingReservation) : IDisposable
    {
        private GpuRingBuffer? _owner = owner;
        public ulong StartingReservation { get; } = startingReservation;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._allocationRetentions)
            {
                owner.RecordRetainedAllocationUse(StartingReservation);
                owner._allocationRetentions.Remove(this);
            }
        }
    }

    private sealed class ContentRetention(GpuRingBuffer owner) : IDisposable
    {
        private GpuRingBuffer? _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
            {
                Interlocked.Decrement(ref owner._retainedContents);
            }
        }
    }

    public GpuRingBuffer(GpuDeviceInfo device, SubmissionScheduler scheduler, GpuBufferUsage usage, ulong size)
        : base(device, scheduler, usage, 0, AllFlags, size)
    {
    }

    // Keep prepared bindings intact until their final consumer has completed.
    internal IDisposable RetainContents()
    {
        Interlocked.Increment(ref _retainedContents);
        return new ContentRetention(this);
    }

    // A new preparation owns no ring bytes until its first allocation is committed.
    internal IDisposable RetainUpcomingAllocations()
    {
        var retention = new AllocationRetention(this, _committedReservations);
        lock (_allocationRetentions)
        {
            _allocationRetentions.Add(retention);
        }
        return retention;
    }

    private bool HasRetainedContents()
    {
        if (Volatile.Read(ref _retainedContents) != 0) return true;
        lock (_allocationRetentions)
        {
            foreach (var retention in _allocationRetentions)
            {
                if (retention.StartingReservation != _committedReservations) return true;
            }
        }
        return false;
    }

    // Preparation can submit the upload before recording its consumer on a later tick.
    private void RecordRetainedAllocationUse(ulong startingReservation)
    {
        for (var index = _currentWatchCursor - 1; index >= 0; index--)
        {
            ref var watch = ref _currentWatches[index];
            if (watch.LastReservation <= startingReservation) break;
            watch.Tick = Math.Max(watch.Tick, Scheduler.CurrentTick);
        }
    }

    // False when the ring cannot serve the request, or when waiting is refused and needed.
    public bool TryMap(ulong size, out ulong offset, ulong alignment = 0, bool allowWait = true)
    {
        offset = 0;
        if (Mapped.IsEmpty)
        {
            return false;
        }

        var mappedSize = size;
        if (!NormalizeReservation(IsCoherent, Device.NonCoherentAtomSize, ref mappedSize, ref alignment) || mappedSize > Size)
        {
            return false;
        }

        if (!TryAlignUp(_offset, alignment, out var alignedOffset))
        {
            return false;
        }

        var wrap = alignedOffset > Size - mappedSize;
        if (wrap && HasRetainedContents())
        {
            return false;
        }

        if (wrap)
        {
            alignedOffset = 0;
        }

        var waitCursor = wrap ? 0 : _waitCursor;
        var waitBound = wrap ? 0 : _waitBound;
        var invalidationMark = wrap ? _currentWatchCursor : _invalidationMark;
        var pendingWatches = wrap ? _currentWatches : _previousWatches;
        if (!WaitForPendingRanges(pendingWatches, invalidationMark, alignedOffset + mappedSize, allowWait, ref waitCursor, ref waitBound))
        {
            return false;
        }

        if (wrap)
        {
            _invalidationMark = invalidationMark;
            _currentWatchCursor = 0;
            (_previousWatches, _currentWatches) = (_currentWatches, _previousWatches);
        }

        _waitCursor = waitCursor;
        _waitBound = waitBound;
        _offset = alignedOffset;
        _mappedSize = mappedSize;
        offset = _offset;
        return true;
    }

    public void Commit()
    {
        if (!IsCoherent && Usage != GpuBufferUsage.Download && _mappedSize != 0)
        {
            Flush(_offset, _mappedSize);
        }

        _offset += _mappedSize;
        _committedReservations++;
        var tick = Scheduler.CurrentTick;
        if (_currentWatchCursor != 0 && _currentWatches[_currentWatchCursor - 1].Tick == tick)
        {
            _currentWatches[_currentWatchCursor - 1].UpperBound = _offset;
            _currentWatches[_currentWatchCursor - 1].LastReservation = _committedReservations;
            return;
        }

        if (_currentWatchCursor + 1 >= _currentWatches.Length)
        {
            Array.Resize(ref _currentWatches, _currentWatches.Length + WatchesReserveChunk);
        }

        ref var watch = ref _currentWatches[_currentWatchCursor++];
        watch.UpperBound = _offset;
        watch.Tick = tick;
        watch.LastReservation = _committedReservations;
    }

    public ulong Copy(ReadOnlySpan<byte> source, ulong alignment = 0)
    {
        if (!TryMap((ulong)source.Length, out var offset, alignment))
        {
            throw SubmissionScheduler.Fatal("The copy does not fit in the ring buffer.");
        }

        source.CopyTo(Mapped[(int)offset..]);
        Commit();
        return offset;
    }

    // Non-coherent memory rounds the size to whole atoms and the alignment to a common multiple.
    internal static bool NormalizeReservation(bool coherent, ulong atom, ref ulong size, ref ulong alignment)
    {
        if (coherent)
        {
            return true;
        }

        if (!TryAlignUp(size, atom, out size))
        {
            return false;
        }

        var divisor = GreatestCommonDivisor(alignment, atom);
        if (alignment != 0 && alignment / divisor > ulong.MaxValue / atom)
        {
            return false;
        }

        alignment = alignment == 0 ? atom : alignment / divisor * atom;
        return true;
    }

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    private static bool TryAlignUp(ulong value, ulong alignment, out ulong result)
    {
        result = value;
        if (alignment == 0 || value % alignment == 0)
        {
            return true;
        }

        var increment = alignment - value % alignment;
        if (value > ulong.MaxValue - increment)
        {
            return false;
        }

        result = value + increment;
        return true;
    }

    private bool WaitForPendingRanges(RingWatch[] watches, int? invalidationMark, ulong requestedUpperBound, bool allowWait, ref int waitCursor, ref ulong waitBound)
    {
        if (invalidationMark is not { } mark)
        {
            return true;
        }

        while (requestedUpperBound > waitBound && waitCursor < mark)
        {
            var watch = watches[waitCursor];
            if (!Scheduler.IsTickComplete(watch.Tick) && !allowWait)
            {
                return false;
            }

            Scheduler.Wait(watch.Tick);
            if (Usage == GpuBufferUsage.Download)
            {
                Scheduler.WaitForPriorityOperations(watch.Tick);
            }

            waitBound = watch.UpperBound;
            waitCursor++;
        }

        return true;
    }
}

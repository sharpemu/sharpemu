// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

public enum PendingWriteKind : byte
{
    Other,
    Target,
    Buffer,
    Image,
    Range,
    Fill,
    Copy,
    Count,
}

public sealed class PendingGpuWrites
{
    private const int FineShift = 14;
    private const int CoarseShift = 20;
    private const ulong MaxFineBlocks = 16;
    private const ulong MaxFineBlocksPerRead = 256;
    private const int BucketBits = 20;
    private const ulong MaxBlocksPerMark = 1ul << 14;
    private const int SweepThreshold = 512;

    private readonly Level _fine = new(FineShift);
    private readonly Level _coarse = new(CoarseShift);
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<(ulong Address, ulong Size), Entry> _entries = new();
    private long _everythingSince;
    private int _sweepAt = SweepThreshold;
    private int _fineEntries;
    private long _released;
    private long _generation;

    private static long _marked;
    private static long _remarked;
    private static long _markedBlocks;
    private static long _markedEverything;
    private static long _swept;
    private static long _readsChecked;
    private static long _readsPending;
    private static long _lockedScans;
    private static long _scannedEntries;
    private static readonly long[] _pendingByKind = new long[(int)PendingWriteKind.Count];

    private sealed class Entry(ulong address, ulong size, Level level, long sequence)
    {
        public readonly ulong Address = address;
        public readonly ulong Size = size;
        public readonly Level Level = level;
        public long Sequence = sequence;
        public long Oldest = sequence;
        public PendingWriteKind Kind;
        private readonly List<long> _sequences = [sequence];
        private long _prunedThrough;

        public void NoteSequence(long value, long released)
        {
            lock (_sequences)
            {
                if (_sequences.Count != 0 && _sequences[0] <= released)
                {
                    var through = _sequences.BinarySearch(released);
                    var count = through >= 0 ? through + 1 : ~through;
                    _prunedThrough = Math.Max(_prunedThrough, _sequences[count - 1]);
                    _sequences.RemoveRange(0, count);
                }

                if (_sequences.Count == 0 || value > _sequences[^1])
                {
                    _sequences.Add(value);
                }
                else
                {
                    var index = _sequences.BinarySearch(value);
                    if (index < 0) _sequences.Insert(~index, value);
                }
            }
        }

        public bool HasBetween(long after, long before)
        {
            var oldest = Volatile.Read(ref Oldest);
            var newest = Volatile.Read(ref Sequence);
            if ((oldest > after && oldest < before) || (newest > after && newest < before)) return true;
            lock (_sequences)
            {
                if (after < _prunedThrough) return true;
                var index = _sequences.BinarySearch(after);
                index = index >= 0 ? index + 1 : ~index;
                return index < _sequences.Count && _sequences[index] < before;
            }
        }

        public ulong FirstBlock => Address >> Level.Shift;

        public ulong LastBlock => (Address + Size - 1) >> Level.Shift;
    }

    private sealed class Level(int shift)
    {
        public readonly int Shift = shift;
        public readonly int[] Buckets = new int[1 << BucketBits];
        public readonly long[] Newest = new long[1 << BucketBits];
        public readonly Dictionary<ulong, List<Entry>> ByBlock = new();

        public void NoteNewest(int bucket, long sequence) => RaiseTo(ref Newest[bucket], sequence);

        public bool MayHavePending(ulong address, ulong size, long released)
        {
            var first = address >> Shift;
            var last = (address + size - 1) >> Shift;
            if (last < first)
            {
                last = ulong.MaxValue >> Shift;
            }

            for (var block = first; block <= last; block++)
            {
                var index = Bucket(block);
                if (Volatile.Read(ref Buckets[index]) != 0 && Volatile.Read(ref Newest[index]) > released)
                {
                    return true;
                }
            }

            return false;
        }

        public bool AnyBetween(ulong address, ulong size, long afterSequence, long beforeSequence)
        {
            var first = address >> Shift;
            var last = (address + size - 1) >> Shift;
            if (last < first)
            {
                last = ulong.MaxValue >> Shift;
            }

            for (var block = first; block <= last; block++)
            {
                if (!ByBlock.TryGetValue(block, out var entries))
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    var entrySequence = Volatile.Read(ref entry.Sequence);
                    if (entrySequence > afterSequence && Volatile.Read(ref entry.Oldest) < beforeSequence &&
                        entry.Address < address + size && address < entry.Address + entry.Size &&
                        entry.HasBetween(afterSequence, beforeSequence))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void Scan(ulong address, ulong size, long released, ref long sequence, ref PendingWriteKind kind)
        {
            var first = address >> Shift;
            var last = (address + size - 1) >> Shift;
            if (last < first)
            {
                last = ulong.MaxValue >> Shift;
            }

            for (var block = first; block <= last; block++)
            {
                if (!ByBlock.TryGetValue(block, out var entries))
                {
                    continue;
                }

                _scannedEntries += entries.Count;
                foreach (var entry in entries)
                {
                    var entrySequence = Volatile.Read(ref entry.Sequence);
                    if (entrySequence > released && entrySequence > sequence &&
                        entry.Address < address + size && address < entry.Address + entry.Size)
                    {
                        sequence = entrySequence;
                        kind = entry.Kind;
                    }
                }
            }
        }
    }

    public long Released => Volatile.Read(ref _released);

    public long Generation => Volatile.Read(ref _generation);

    public void Mark(ulong address, ulong size, long sequence, PendingWriteKind kind = PendingWriteKind.Other)
    {
        if (size == 0)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        var coarseFirst = address >> CoarseShift;
        var coarseLast = (address + size - 1) >> CoarseShift;
        if (coarseLast < coarseFirst || coarseLast - coarseFirst >= MaxBlocksPerMark)
        {
            MarkEverything(sequence);
            return;
        }

        _gate.EnterReadLock();
        try
        {
            if (_entries.TryGetValue((address, size), out var remarked))
            {
                remarked.NoteSequence(sequence, Volatile.Read(ref _released));
                if (Volatile.Read(ref remarked.Sequence) <= Volatile.Read(ref _released))
                {
                    Volatile.Write(ref remarked.Oldest, sequence);
                }
                else
                {
                    LowerTo(ref remarked.Oldest, sequence);
                }

                RaiseTo(ref remarked.Sequence, sequence);
                remarked.Kind = kind;
                for (var block = remarked.FirstBlock; block <= remarked.LastBlock; block++)
                {
                    remarked.Level.NoteNewest(Bucket(block), sequence);
                }

                Interlocked.Increment(ref _remarked);
                return;
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        _gate.EnterWriteLock();
        try
        {
            if (_entries.TryGetValue((address, size), out var remarked))
            {
                remarked.NoteSequence(sequence, Volatile.Read(ref _released));
                if (Volatile.Read(ref remarked.Sequence) <= Volatile.Read(ref _released))
                {
                    Volatile.Write(ref remarked.Oldest, sequence);
                }
                else
                {
                    LowerTo(ref remarked.Oldest, sequence);
                }

                RaiseTo(ref remarked.Sequence, sequence);
                remarked.Kind = kind;
                for (var block = remarked.FirstBlock; block <= remarked.LastBlock; block++)
                {
                    remarked.Level.NoteNewest(Bucket(block), sequence);
                }

                Interlocked.Increment(ref _remarked);
                return;
            }

            var fine = ((address + size - 1) >> FineShift) - (address >> FineShift) < MaxFineBlocks;
            var entry = new Entry(address, size, fine ? _fine : _coarse, sequence) { Kind = kind };
            _entries.Add((address, size), entry);
            if (fine)
            {
                Volatile.Write(ref _fineEntries, _fineEntries + 1);
            }

            var level = entry.Level;
            for (var block = entry.FirstBlock; block <= entry.LastBlock; block++)
            {
                if (!level.ByBlock.TryGetValue(block, out var entries))
                {
                    entries = new List<Entry>();
                    level.ByBlock.Add(block, entries);
                }

                entries.Add(entry);
                var index = Bucket(block);
                level.NoteNewest(index, sequence);
                ref var bucket = ref level.Buckets[index];
                Volatile.Write(ref bucket, bucket + 1);
                _markedBlocks++;
            }

            _marked++;
            if (_entries.Count > _sweepAt)
            {
                Sweep();
                _sweepAt = Math.Max(SweepThreshold, _entries.Count * 2);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private static void LowerTo(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value < current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private static void RaiseTo(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    public void MarkEverything(long sequence)
    {
        Interlocked.Increment(ref _generation);
        RaiseTo(ref _everythingSince, sequence);
        Interlocked.Increment(ref _markedEverything);
    }

    public void Release(long sequence)
    {
        if (sequence > Volatile.Read(ref _released))
        {
            Volatile.Write(ref _released, sequence);
        }
    }

    private void Sweep()
    {
        var released = Volatile.Read(ref _released);
        var stale = new List<Entry>();
        foreach (var entry in _entries.Values)
        {
            if (entry.Sequence <= released)
            {
                stale.Add(entry);
            }
        }

        foreach (var entry in stale)
        {
            _entries.Remove((entry.Address, entry.Size));
            var level = entry.Level;
            if (level == _fine)
            {
                Volatile.Write(ref _fineEntries, _fineEntries - 1);
            }

            for (var block = entry.FirstBlock; block <= entry.LastBlock; block++)
            {
                var entries = level.ByBlock[block];
                entries.Remove(entry);
                if (entries.Count == 0)
                {
                    level.ByBlock.Remove(block);
                }

                ref var bucket = ref level.Buckets[Bucket(block)];
                Volatile.Write(ref bucket, bucket - 1);
            }
        }

        _swept += stale.Count;
    }

    public bool MayHavePending(ulong address, ulong size)
    {
        if (size == 0)
        {
            return false;
        }

        var released = Volatile.Read(ref _released);
        if (Volatile.Read(ref _everythingSince) > released)
        {
            return true;
        }

        var wideRead = ((address + size - 1) >> FineShift) - (address >> FineShift) >= MaxFineBlocksPerRead || address + size < address;
        return (wideRead ? Volatile.Read(ref _fineEntries) != 0 : _fine.MayHavePending(address, size, released)) ||
            _coarse.MayHavePending(address, size, released);
    }

    // A marked write with a sequence strictly between the two, released or not.
    public bool HasPendingBetween(ulong address, ulong size, long afterSequence, long beforeSequence)
    {
        if (size == 0)
        {
            return false;
        }

        var everything = Volatile.Read(ref _everythingSince);
        if (everything > afterSequence && everything < beforeSequence)
        {
            return true;
        }

        var wideRead = ((address + size - 1) >> FineShift) - (address >> FineShift) >= MaxFineBlocksPerRead || address + size < address;
        _gate.EnterReadLock();
        try
        {
            if (wideRead)
            {
                foreach (var entry in _entries.Values)
                {
                    var entrySequence = Volatile.Read(ref entry.Sequence);
                    if (entrySequence > afterSequence && Volatile.Read(ref entry.Oldest) < beforeSequence &&
                        entry.Address < address + size && address < entry.Address + entry.Size &&
                        entry.HasBetween(afterSequence, beforeSequence))
                    {
                        return true;
                    }
                }

                return false;
            }

            return _fine.AnyBetween(address, size, afterSequence, beforeSequence) || _coarse.AnyBetween(address, size, afterSequence, beforeSequence);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public PendingWriteKind? KindBetween(ulong address, ulong size, long afterSequence, long beforeSequence)
    {
        _gate.EnterReadLock();
        try
        {
            foreach (var entry in _entries.Values)
            {
                if (Volatile.Read(ref entry.Sequence) > afterSequence && Volatile.Read(ref entry.Oldest) < beforeSequence &&
                    entry.Address < address + size && address < entry.Address + entry.Size &&
                    entry.HasBetween(afterSequence, beforeSequence))
                {
                    return entry.Kind;
                }
            }

            return null;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool TryGetPending(ulong address, ulong size, out long sequence)
    {
        sequence = 0;
        if (size == 0)
        {
            return false;
        }

        _readsChecked++;
        var released = Volatile.Read(ref _released);
        var everything = Volatile.Read(ref _everythingSince);
        if (everything > released)
        {
            sequence = everything;
            Interlocked.Increment(ref _readsPending);
            return true;
        }

        var wideRead = ((address + size - 1) >> FineShift) - (address >> FineShift) >= MaxFineBlocksPerRead || address + size < address;
        var fine = wideRead ? Volatile.Read(ref _fineEntries) != 0 : _fine.MayHavePending(address, size, released);
        var coarse = _coarse.MayHavePending(address, size, released);
        if (!fine && !coarse)
        {
            return false;
        }

        var kind = PendingWriteKind.Other;
        _gate.EnterReadLock();
        try
        {
            _lockedScans++;
            released = Volatile.Read(ref _released);
            if (fine && wideRead)
            {
                foreach (var entry in _entries.Values)
                {
                    _scannedEntries++;
                    var entrySequence = Volatile.Read(ref entry.Sequence);
                    if (entry.Level == _fine && entrySequence > released && entrySequence > sequence &&
                        entry.Address < address + size && address < entry.Address + entry.Size)
                    {
                        sequence = entrySequence;
                        kind = entry.Kind;
                    }
                }
            }
            else if (fine)
            {
                _fine.Scan(address, size, released, ref sequence, ref kind);
            }

            if (coarse)
            {
                _coarse.Scan(address, size, released, ref sequence, ref kind);
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        if (sequence == 0)
        {
            return false;
        }

        Interlocked.Increment(ref _readsPending);
        Interlocked.Increment(ref _pendingByKind[(int)kind]);
        return true;
    }

    private static int Bucket(ulong block) => (int)((block * 0x9E3779B97F4A7C15ul) >> (64 - BucketBits));

    public static string TakeReport()
    {
        var line = new System.Text.StringBuilder(FormattableString.Invariant(
            $"[PERF][PENDING_WRITES] marked={Interlocked.Exchange(ref _marked, 0)} remarked={Interlocked.Exchange(ref _remarked, 0)} blocks={Interlocked.Exchange(ref _markedBlocks, 0)} "));
        line.Append(FormattableString.Invariant(
            $"everything={Interlocked.Exchange(ref _markedEverything, 0)} swept={Interlocked.Exchange(ref _swept, 0)} reads_checked={Interlocked.Exchange(ref _readsChecked, 0)} reads_pending={Interlocked.Exchange(ref _readsPending, 0)} "));
        line.Append(FormattableString.Invariant(
            $"locked_scans={Interlocked.Exchange(ref _lockedScans, 0)} scanned_entries={Interlocked.Exchange(ref _scannedEntries, 0)} pending_by_kind="));
        for (var kind = 0; kind < (int)PendingWriteKind.Count; kind++)
        {
            line.Append(FormattableString.Invariant($"{(kind == 0 ? "" : ",")}{(PendingWriteKind)kind}:{Interlocked.Exchange(ref _pendingByKind[kind], 0)}"));
        }

        return line.ToString();
    }
}

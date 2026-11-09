// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

internal sealed class PendingGuestPublications
{
    internal sealed class Entry(int queue, ulong address, ulong size)
    {
        internal readonly int Queue = queue;
        internal readonly ulong Address = address;
        internal readonly ulong Size = size;
        internal ulong Tick;
        internal bool Publishing;
        internal bool Registered;
        internal volatile bool Completed;
        internal bool DeferredLabel;
        // A held-back command write: command-processor reads wait for it, unlike a label they forward.
        internal bool HeldWrite;
        internal ulong Value;
        // A dump with a GPU slot can be copied into a reading buffer instead of waited for.
        internal long GpuSlot = -1;
        // Add order across all queues; the scheduler publishes entries in this order.
        internal long Ordinal;
        internal uint DumpMask;
        internal uint DumpStride;
    }

    private readonly object _gate = new();
    private const int PageShift = 12;
    private const int RegionShift = 21;
    private const int PageFilterBits = 16;
    private readonly List<Entry> _entries = new();
    // Per-queue entry count and highest registered tick; each deferred label needs both.
    private readonly Dictionary<int, int> _queueEntries = new();
    private readonly Dictionary<int, ulong> _queueTicks = new();
    private readonly Dictionary<int, long> _queueNewest = new();
    private long _nextOrdinal;

    internal long Ordinal => Interlocked.Read(ref _nextOrdinal);

    private void Track(Entry entry)
    {
        entry.Ordinal = ++_nextOrdinal;
        _queueEntries[entry.Queue] = _queueEntries.GetValueOrDefault(entry.Queue) + 1;
        _queueNewest[entry.Queue] = entry.Ordinal;
    }

    // True when the queue has a pending entry added after the given one, so it publishes later.
    internal bool QueueFollows(int queue, long ordinal)
    {
        lock (_gate) return _queueNewest.TryGetValue(queue, out var newest) && newest > ordinal;
    }
    private readonly int[] _pageFilter = new int[1 << PageFilterBits];
    private readonly int[] _regionFilter = new int[1 << PageFilterBits];
    private int _rangedEntries;
    // Pending dumps are few; resource reads scan this copy without the lock.
    private volatile Entry[] _dumps = [];
    private volatile Exception? _failure;

    private static int FilterSlot(ulong page) => (int)((page * 0x9E37_79B9_7F4A_7C15UL) >> (64 - PageFilterBits));

    // Descriptor and buffer lookups call this often; a range with no pending page skips the lock.
    private bool MayOverlap(ulong address, ulong size)
    {
        if (_failure is not null) return true;
        if (Volatile.Read(ref _rangedEntries) == 0) return false;
        var first = address >> PageShift;
        var last = (address + size - 1) >> PageShift;
        if (last - first < 64)
        {
            for (var page = first; page <= last; page++)
                if (Volatile.Read(ref _pageFilter[FilterSlot(page)]) != 0) return true;
            return false;
        }
        var lastRegion = (address + size - 1) >> RegionShift;
        if (lastRegion - (address >> RegionShift) >= 256) return true;
        for (var region = address >> RegionShift; region <= lastRegion; region++)
            if (Volatile.Read(ref _regionFilter[FilterSlot(region)]) != 0) return true;
        return false;
    }

    private void Index(Entry entry, int delta)
    {
        if (entry.Size == 0) return;
        var last = (entry.Address + entry.Size - 1) >> PageShift;
        for (var page = entry.Address >> PageShift; page <= last; page++)
            Interlocked.Add(ref _pageFilter[FilterSlot(page)], delta);
        var lastRegion = (entry.Address + entry.Size - 1) >> RegionShift;
        for (var region = entry.Address >> RegionShift; region <= lastRegion; region++)
            Interlocked.Add(ref _regionFilter[FilterSlot(region)], delta);
        Interlocked.Add(ref _rangedEntries, delta);
        if (entry.DeferredLabel) return;
        _dumps = delta > 0 ? [.. _dumps, entry] : Array.FindAll(_dumps, dump => !ReferenceEquals(dump, entry));
    }

    // Read without the lock; Index replaces the array under the lock.
    internal Entry[] PendingDumps => _dumps;

    private bool DumpOverlap(ulong address, ulong size, bool skipGpuSlots, out ulong tick)
    {
        var found = false;
        tick = 0;
        foreach (var dump in _dumps)
        {
            if (address >= dump.Address + dump.Size || dump.Address >= address + size) continue;
            if (skipGpuSlots && dump.GpuSlot >= 0) continue;
            found = true;
            if (!dump.Publishing) tick = Math.Max(tick, dump.Tick);
        }
        return found;
    }

    // GPU resource reads pass includeLabels false; a deferred label only orders later writes.
    internal bool HasOverlap(ulong address, ulong size, bool includeLabels = true, bool skipGpuSlots = false)
    {
        if (size == 0 || address > ulong.MaxValue - size) return false;
        if (!includeLabels && _failure is null) return DumpOverlap(address, size, skipGpuSlots, out _);
        if (!MayOverlap(address, size)) return false;
        lock (_gate)
        {
            ThrowIfFailed();
            foreach (var entry in _entries)
                if (entry.Size != 0 && (includeLabels || !entry.DeferredLabel) && address < entry.Address + entry.Size && entry.Address < address + size) return true;
            return false;
        }
    }

    internal int Count { get { lock (_gate) return _entries.Count; } }

    internal bool HasLabelOverlap(ulong address, ulong size)
    {
        if (size == 0 || address > ulong.MaxValue - size || !MayOverlap(address, size)) return false;
        lock (_gate)
        {
            ThrowIfFailed();
            if (size == 0 || address > ulong.MaxValue - size) return false;
            foreach (var entry in _entries)
                if ((entry.DeferredLabel || entry.HeldWrite) && address < entry.Address + entry.Size && entry.Address < address + size) return true;
            return false;
        }
    }

    // The command processor sees deferred label bytes at once; a pending dump hides the range.
    internal bool TryOverlayLabels(ulong address, Span<byte> bytes, out int coveredMask) =>
        TryOverlayLabels(-1, address, bytes, out coveredMask, out _);

    // newestForeign is the newest used label of another queue; the reader must keep its order after it.
    internal bool TryOverlayLabels(int readerQueue, ulong address, Span<byte> bytes, out int coveredMask,
        out Entry? newestForeign)
    {
        coveredMask = 0;
        newestForeign = null;
        var size = (ulong)bytes.Length;
        if (size == 0 || address > ulong.MaxValue - size || !MayOverlap(address, size)) return true;
        lock (_gate)
        {
            ThrowIfFailed();
            if (size == 0 || address > ulong.MaxValue - size) return true;
            foreach (var entry in _entries)
            {
                if (entry.Size == 0 || address >= entry.Address + entry.Size || entry.Address >= address + size) continue;
                if (!entry.DeferredLabel) return false;
                if (readerQueue >= 0 && entry.Queue != readerQueue) newestForeign = entry;
                var first = Math.Max(address, entry.Address);
                var last = Math.Min(address + size, entry.Address + entry.Size);
                for (var byteAddress = first; byteAddress < last; byteAddress++)
                {
                    var index = (int)(byteAddress - address);
                    bytes[index] = (byte)(entry.Value >> (int)(8 * (byteAddress - entry.Address)));
                    coveredMask |= 1 << index;
                }
            }
            return true;
        }
    }

    internal Entry Add(int queue, ulong address, ulong size)
    {
        if (size == 0 || address > ulong.MaxValue - size)
            throw new ArgumentOutOfRangeException(nameof(size));
        lock (_gate)
        {
            ThrowIfFailed();
            var entry = new Entry(queue, address, size);
            _entries.Add(entry);
            Track(entry);
            Index(entry, 1);
            return entry;
        }
    }

    // A zero-size entry that holds a queue's later signals behind publications already queued.
    internal Entry AddOrdering(int queue)
    {
        lock (_gate)
        {
            ThrowIfFailed();
            var entry = new Entry(queue, 0, 0);
            _entries.Add(entry);
            Track(entry);
            return entry;
        }
    }

    // A held-back command-processor write; readers wait for it like a dump.
    internal Entry AddWrite(int queue, ulong address, ulong size)
    {
        if (size == 0 || address > ulong.MaxValue - size)
            throw new ArgumentOutOfRangeException(nameof(size));
        lock (_gate)
        {
            ThrowIfFailed();
            var entry = new Entry(queue, address, size) { HeldWrite = true };
            _entries.Add(entry);
            Track(entry);
            Index(entry, 1);
            return entry;
        }
    }

    internal bool HasQueue(int queue)
    {
        lock (_gate)
        {
            ThrowIfFailed();
            return _queueEntries.ContainsKey(queue);
        }
    }

    internal void Register(Entry entry, ulong tick)
    {
        lock (_gate)
        {
            entry.Tick = tick;
            entry.Registered = true;
            if (_queueEntries.ContainsKey(entry.Queue))
                _queueTicks[entry.Queue] = Math.Max(_queueTicks.GetValueOrDefault(entry.Queue), tick);
        }
    }

    internal Entry? AddAfterPending(int queue, ulong address, ulong size, ulong value = 0, bool requireCompletion = false)
    {
        if (address > ulong.MaxValue - size) throw new ArgumentOutOfRangeException(nameof(size));
        lock (_gate)
        {
            ThrowIfFailed();
            if (!requireCompletion && !_queueEntries.ContainsKey(queue)) return null;
            var entry = new Entry(queue, address, size)
            {
                DeferredLabel = size != 0,
                Value = size == sizeof(uint) ? (uint)value : value,
            };
            _entries.Add(entry);
            Track(entry);
            Index(entry, 1);
            return entry;
        }
    }

    internal Entry? LastRecorded(int queue)
    {
        lock (_gate)
        {
            ThrowIfFailed();
            for (var index = _entries.Count - 1; index >= 0; index--)
                if (_entries[index].Queue == queue && _entries[index].Registered) return _entries[index];
            return null;
        }
    }

    // Entries register in queue order, so the running maximum covers every earlier entry.
    // Published entries can raise it, but their ticks are already complete.
    internal ulong PredecessorTick(Entry target)
    {
        lock (_gate)
        {
            ThrowIfFailed();
            return _queueTicks.GetValueOrDefault(target.Queue);
        }
    }

    internal ulong OverlapTick(ulong address, ulong size, bool includeLabels = true, bool skipGpuSlots = false)
    {
        if (size == 0 || address > ulong.MaxValue - size) return 0;
        if (!includeLabels && _failure is null)
        {
            DumpOverlap(address, size, skipGpuSlots, out var dumpTick);
            return dumpTick;
        }
        if (!MayOverlap(address, size)) return 0;
        lock (_gate)
        {
            ThrowIfFailed();
            ulong tick = 0;
            foreach (var entry in _entries)
            {
                if (entry.Size != 0 && !entry.Publishing && (includeLabels || !entry.DeferredLabel) &&
                    address < entry.Address + entry.Size && entry.Address < address + size)
                    tick = Math.Max(tick, entry.Tick);
            }
            return tick;
        }
    }

    // Diagnostic only: names the newest unpublished entry that a wait covers.
    internal void Publish(Entry entry, Action write)
    {
        lock (_gate)
        {
            ThrowIfFailed();
            entry.Publishing = true;
        }
        try
        {
            write();
            lock (_gate)
            {
                if (_entries.Remove(entry))
                {
                    Index(entry, -1);
                    if (--_queueEntries[entry.Queue] == 0)
                    {
                        _queueEntries.Remove(entry.Queue);
                        _queueTicks.Remove(entry.Queue);
                        _queueNewest.Remove(entry.Queue);
                    }
                }
                entry.Completed = true;
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
    }

    internal void Fail(Exception exception)
    {
        lock (_gate)
        {
            _failure ??= exception;
            _entries.Clear();
            _queueEntries.Clear();
            _queueTicks.Clear();
            _queueNewest.Clear();
            Array.Clear(_pageFilter);
            Array.Clear(_regionFilter);
            Volatile.Write(ref _rangedEntries, 0);
            _dumps = [];
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure is { } failure)
            throw new InvalidOperationException("Guest publication failed.", failure);
    }
}

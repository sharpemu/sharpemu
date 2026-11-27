// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.VideoOut;

// The pages the command stream's queued stores land on, for lock-free tests from any thread. The owner adds a store under its
// lock when it queues the store and removes it under the same lock after the store landed, so a test that starts after a store
// was queued (on the queueing thread, or after a hand-off through the queue) sees it. A store longer than LargePages pages is
// kept as one range instead of page by page, and a count per bucket of pages answers most page tests before the dictionary.
internal sealed class PendingStorePages
{
    public const int PageShift = 12;
    public const int LargePages = 16;
    private const int BucketShift = 52;

    private struct DirectPage
    {
        public long Tag;
        public int Count;
    }
    private DirectPage[]? _direct;

    private readonly ConcurrentDictionary<ulong, int> _pages = new();
    private readonly int[] _buckets = new int[1 << (64 - BucketShift)];
    private readonly List<(ulong Address, ulong End)> _large = new();
    private volatile (ulong Address, ulong End)[] _largeSnapshot = [];

    public static bool IsLarge(ulong address, ulong length) =>
        length != 0 && ((address + length - 1) >> PageShift) - (address >> PageShift) >= LargePages;

    private static int Bucket(ulong page) => (int)((page * 0x9E3779B97F4A7C15UL) >> BucketShift);

    public bool HasLarge => _largeSnapshot.Length != 0;

    internal bool IsEmpty => _pages.IsEmpty && _largeSnapshot.Length == 0 && Array.TrueForAll(_buckets, static count => count == 0);

    // The owner, under its lock; a store is removed with the large flag it was added with.
    public void Add(ulong address, ulong length, bool large) => Note(address, length, large, 1);

    public void Remove(ulong address, ulong length, bool large) => Note(address, length, large, -1);

    // The owner, under its lock: whether a store kept as a range overlaps the bytes.
    public bool OverlapsLarge(ulong address, ulong end)
    {
        foreach (var range in _large)
        {
            if (range.Address < end && address < range.End)
            {
                return true;
            }
        }

        return false;
    }

    private void Note(ulong address, ulong length, bool large, int delta)
    {
        if (large)
        {
            var range = (address, address + length);
            if (delta > 0)
            {
                _large.Add(range);
            }
            else if (!_large.Remove(range))
            {
                throw new InvalidOperationException("The command stream removed a queued store range it does not hold.");
            }

            _largeSnapshot = _large.ToArray();
            return;
        }

        var last = (address + length - 1) >> PageShift;
        for (var page = address >> PageShift; page <= last; page++)
        {
            var bucket = Bucket(page);
            var count = _buckets[bucket];
            var direct = _direct;
            if (delta > 0 && count == 0)
            {
                if (direct is null) Volatile.Write(ref _direct, direct = new DirectPage[_buckets.Length]);
                Volatile.Write(ref direct[bucket].Tag, (long)page + 1);
            }
            // Writers already own the store queue lock. A tag first installed into an empty
            // bucket accounts for this page until its last removal, including across live flips.
            if (direct is not null && direct[bucket].Tag == (long)page + 1)
            {
                direct[bucket].Count += delta;
                if (direct[bucket].Count == 0) Volatile.Write(ref direct[bucket].Tag, 0);
                Volatile.Write(ref _buckets[bucket], count + delta);
                continue;
            }
            if (delta > 0)
            {
                Volatile.Write(ref _buckets[bucket], _buckets[bucket] + 1);
                _pages.AddOrUpdate(page, 1, static (_, count) => count + 1);
            }
            else
            {
                if (_pages.AddOrUpdate(page, -1, static (_, count) => count - 1) == 0)
                {
                    _pages.TryRemove(new KeyValuePair<ulong, int>(page, 0));
                }

                Volatile.Write(ref _buckets[bucket], _buckets[bucket] - 1);
            }
        }
    }

    // Any thread: whether a queued store lands on a page of the range. A store kept as a range is compared by page as well,
    // so the answer is the one a page index alone would give.
    public bool Overlaps(ulong address, ulong size) => Overlaps(address, size, prefilter: true);

    internal bool Overlaps(ulong address, ulong size, bool prefilter)
    {
        if (size == 0)
        {
            return false;
        }

        var first = address >> PageShift;
        var last = (address + size - 1) >> PageShift;
        for (var page = first; page <= last; page++)
        {
            var bucket = Bucket(page);
            if (prefilter && Volatile.Read(ref _buckets[bucket]) == 0) continue;
            var direct = Volatile.Read(ref _direct);
            if ((direct is not null && Volatile.Read(ref direct[bucket].Tag) == (long)page + 1) || _pages.ContainsKey(page))
            {
                return true;
            }
        }

        var large = _largeSnapshot;
        if (large.Length != 0)
        {
            var start = first << PageShift;
            var end = (last + 1) << PageShift;
            foreach (var range in large)
            {
                if (range.Address < end && start < range.End)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;

// The boot prewarm's records, shared by its workers (VulkanVideoPresenter.ShaderPrewarm). The permutations of one linked
// caller write the same linked cache file, so they compile one at a time. A worker that takes a record of a caller another
// worker is compiling puts it back and takes the next record instead; only after a whole pass found nothing but busy
// callers does it wait, until a caller is released. Each caller's linked records take that caller's places in the list
// newest first: the last recorded permutation is the one the last run asked for. Every record is kept and taken once.
internal sealed class ShaderPrewarmQueue<T>
{
    private readonly ConcurrentQueue<T> _work;
    private readonly Func<T, ulong?> _caller;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _gates = new();
    private readonly object _released = new();
    private long _releases;
    private long _deferrals;

    // caller: the linked caller's hash of a record, or null for a record without linked callees. keepOrder takes the
    // records in the order given (the background lane's LinkedLaneOrder) instead of each caller's newest first.
    public ShaderPrewarmQueue(IReadOnlyList<T> records, Func<T, ulong?> caller, bool keepOrder = false)
    {
        _caller = caller;
        _work = new ConcurrentQueue<T>(keepOrder ? records : NewestLinkedFirst(records, caller));
    }

    public int Count => _work.Count;

    // Records put back because their caller was busy (tests).
    internal long Deferrals => Interlocked.Read(ref _deferrals);

    // Waits for a release at most this long before it looks again, so stopping is seen.
    internal const int ReleaseWaitMilliseconds = 250;

    // The next record without taking it (PrewarmPool orders the prewarm's lanes by it); false when none is left.
    public bool TryPeek(out T record) => _work.TryPeek(out record!);

    // The next record this worker may compile now, with the gate it then holds (null for a plain record); false when none
    // is left or stopping says so, and, without mayWait, when every record left waits on a busy caller. Release(gate) after
    // the compile.
    public bool TryTake(Func<bool> stopping, out T record, out SemaphoreSlim? gate, bool mayWait = true)
    {
        var deferred = 0;
        var passStart = Interlocked.Read(ref _releases);
        while (!stopping() && _work.TryDequeue(out record!))
        {
            gate = _caller(record) is { } caller ? _gates.GetOrAdd(caller, static _ => new SemaphoreSlim(1, 1)) : null;
            if (gate is null || gate.Wait(0))
            {
                return true;
            }

            _work.Enqueue(record);
            Interlocked.Increment(ref _deferrals);
            if (++deferred <= _work.Count)
            {
                continue;
            }

            // Every record of the pass waits on a busy caller.
            if (!mayWait)
            {
                break;
            }

            lock (_released)
            {
                while (Interlocked.Read(ref _releases) == passStart && !stopping())
                {
                    Monitor.Wait(_released, ReleaseWaitMilliseconds);
                }
            }

            deferred = 0;
            passStart = Interlocked.Read(ref _releases);
        }

        record = default!;
        gate = null;
        return false;
    }

    public void Release(SemaphoreSlim? gate)
    {
        if (gate is null)
        {
            return;
        }

        gate.Release();
        Interlocked.Increment(ref _releases);
        lock (_released)
        {
            Monitor.PulseAll(_released);
        }
    }

    internal static T[] NewestLinkedFirst(IReadOnlyList<T> records, Func<T, ulong?> caller)
    {
        var ordered = records.ToArray();
        var places = new Dictionary<ulong, List<int>>();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (caller(ordered[index]) is not { } hash)
            {
                continue;
            }

            if (!places.TryGetValue(hash, out var list))
            {
                places.Add(hash, list = []);
            }

            list.Add(index);
        }

        foreach (var list in places.Values)
        {
            for (int low = 0, high = list.Count - 1; low < high; low++, high--)
            {
                (ordered[list[low]], ordered[list[high]]) = (ordered[list[high]], ordered[list[low]]);
            }
        }

        return ordered;
    }

    // The workers of the background lane of linked records (VulkanVideoPresenter.StartLinkedPrewarm), each keeping one driver
    // compile running: two on a machine with sixteen or more hardware threads, else one, beside the session and the other
    // prewarm lanes.
    internal static int LinkedLaneWorkers(int processorCount) => processorCount >= 16 ? 2 : 1;

    // The order of the background lane of linked records (VulkanVideoPresenter.StartLinkedPrewarm), which compiles one at a
    // time. Every caller's first record goes before any caller's second, since one ready permutation already runs the
    // caller's dispatches while a newer one compiles. A caller's first record is the one its next session asks for first
    // (firstAsked: the library of the callees a session kept for it, which the session links first); without one, the
    // oldest, since a session then links what the caller's tables name at its first dispatch, which sessions record before
    // the later permutations. The caller's other records follow newest first. Within each round, callers a session saw
    // compile for minutes go first, since a session cannot compile those in time itself; otherwise callers keep the order
    // in which they were first recorded.
    internal static T[] LinkedLaneOrder(IReadOnlyList<T> records, Func<T, ulong> caller, Func<ulong, bool> slow,
        Func<T, bool> firstAsked)
    {
        var callers = new Dictionary<ulong, (int Place, List<T> Records)>();
        for (var index = 0; index < records.Count; index++)
        {
            var hash = caller(records[index]);
            if (!callers.TryGetValue(hash, out var entry))
            {
                callers.Add(hash, entry = (index, []));
            }

            entry.Records.Add(records[index]);
        }

        var keyed = new List<(T Record, int Round, bool Slow, int Place)>(records.Count);
        foreach (var (hash, (place, list)) in callers)
        {
            var first = Math.Max(0, list.FindIndex(record => firstAsked(record)));
            var lane = new List<T>(list.Count) { list[first] };
            for (var index = list.Count - 1; index >= 0; index--)
            {
                if (index != first)
                {
                    lane.Add(list[index]);
                }
            }

            var isSlow = slow(hash);
            for (var round = 0; round < lane.Count; round++)
            {
                keyed.Add((lane[round], round, isSlow, place));
            }
        }

        return [.. keyed.OrderBy(static entry => entry.Round).ThenByDescending(static entry => entry.Slow)
            .ThenBy(static entry => entry.Place).Select(static entry => entry.Record)];
    }
}

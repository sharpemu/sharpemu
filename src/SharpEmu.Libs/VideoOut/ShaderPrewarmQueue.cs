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

    // caller: the linked caller's hash of a record, or null for a record without linked callees.
    public ShaderPrewarmQueue(IReadOnlyList<T> records, Func<T, ulong?> caller)
    {
        _caller = caller;
        _work = new ConcurrentQueue<T>(NewestLinkedFirst(records, caller));
    }

    public int Count => _work.Count;

    // Records put back because their caller was busy (tests).
    internal long Deferrals => Interlocked.Read(ref _deferrals);

    // Waits for a release at most this long before it looks again, so stopping is seen.
    internal const int ReleaseWaitMilliseconds = 250;

    public bool TryTake(Func<bool> stopping, out T record, out SemaphoreSlim? gate)
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
}

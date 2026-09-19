// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Holds DCBs whose parsing was suspended on an unsatisfied WAIT_REG_MEM
/// condition. AgcExports re-checks every waiter against guest memory on each
/// submit and resumes the ones whose condition became true (labels are advanced
/// by ReleaseMem / WriteData / DmaData packets, or by direct CPU writes).
///
/// This preserves cross-submit ordering: the work that follows a wait inside a
/// DCB is only queued once the awaited completion label is genuinely written,
/// instead of being force-satisfied at parse time and running ahead of the
/// compute/graphics work it depends on (which produced a black composite).
/// </summary>
internal static class GpuWaitRegistry
{
    public struct WaitingDcb
    {
        public ulong CommandBufferAddress;
        public ulong ResumeAddress;
        public uint TotalDwords;
        public uint ResumeOffset;
        public ulong WaitAddress;
        public ulong ReferenceValue;
        public ulong Mask;
        public uint CompareFunction;
        public uint ControlValue;
        public bool Is64Bit;
        public bool IsStandard;
        public object? Memory;
        public string? QueueName;
        public ulong SubmissionId;
        // Stopwatch timestamp captured at registration. Stale waiters remain
        // registered; this only controls one-shot diagnostics.
        public long RegisteredTicks;
        public bool StaleReported;
        public object? State;
        // Latched by LatchSatisfiedByValue when a producer wrote a value that
        // satisfies this waiter. The label is frequently reused (reset to 0 for
        // the next frame) immediately after the producing write, so re-reading
        // guest memory at wake time can miss the transient satisfied window.
        // Latching records satisfaction at the moment of the write instead.
        public bool Latched;
        // Non-zero for indirect-dispatch dimension retries: a bounded deadline
        // (Stopwatch ticks) after which the waiter is resumed even if unsatisfied,
        // so a legitimately empty indirect dispatch can never stall forever.
        public long RetryDeadlineTicks;
        // Frame counter at registration. The deadlock breaker may only replay a
        // produced label value written in this frame or later — never an earlier
        // frame's signal that the guest has since recycled.
        public long WaitFrameId;
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, List<WaitingDcb>> _waiters = new();
    // The last value each label producer wrote. Used only by the deadlock
    // breaker: our serial submission parser cannot model two GPU queues running
    // concurrently, so a label written -> reset -> re-waited across queues can
    // cycle forever even though a real producer did signal it. Keyed by (memory,
    // address) so distinct guest processes never alias.
    private static readonly Dictionary<(object, ulong), ulong> _lastProduced = new();
    // Frame-staleness guard: tracks the frame ID of each label write so that
    // WAIT_REG_MEM in frame N+1 is not satisfied by a stale write from frame N.
    private static readonly Dictionary<(object, ulong), long> _labelFrameIds = new();
    private static long _currentFrameId;

  
    private static object? Canonicalize(object? memory)
    {
        while (memory is SharpEmu.HLE.ICpuMemoryWrapper wrapper)
        {
            memory = wrapper.Inner;
        }

        return memory;
    }

    /// <summary>
    /// Advances the frame counter. Called at each frame boundary (flip) so that
    /// stale label writes from previous frames cannot satisfy WAIT_REG_MEM.
    /// </summary>
    public static void AdvanceFrame()
    {
        System.Threading.Interlocked.Increment(ref _currentFrameId);
    }

    /// <summary>
    /// Returns true if the label at (memory, address) was written in the
    /// current frame, or has never been written (uninitialized).
    /// Only labels written in a PREVIOUS frame are considered stale.
    /// </summary>
    public static bool IsLabelFresh(object memory, ulong address)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            if (!_labelFrameIds.TryGetValue((memory, address), out var frameId))
            {
                return true; // never written — treat as fresh (not stale)
            }

            return frameId >= System.Threading.Volatile.Read(ref _currentFrameId);
        }
    }

    public static int Count
    {
        get
        {
            lock (_gate)
            {
                var total = 0;
                foreach (var (_, list) in _waiters)
                {
                    total += list.Count;
                }

                return total;
            }
        }
    }

    public static int CountForMemory(object memory)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            var total = 0;
            foreach (var (_, list) in _waiters)
            {
                foreach (var waiter in list)
                {
                    total += ReferenceEquals(waiter.Memory, memory) ? 1 : 0;
                }
            }

            return total;
        }
    }

    public readonly record struct OutstandingSnapshot(
        int Outstanding,
        int Latched,
        long OldestAgeMs,
        ulong SampleWaitAddress,
        string? SampleQueueName);

    /// <summary>
    /// Diagnostics snapshot of suspended WAIT_REG_MEM / dims waiters.
    /// </summary>
    public static OutstandingSnapshot SnapshotOutstanding(object? memory = null)
    {
        memory = Canonicalize(memory);
        lock (_gate)
        {
            var outstanding = 0;
            var latched = 0;
            var oldestTicks = long.MaxValue;
            ulong sampleAddress = 0;
            string? sampleQueue = null;
            var now = Stopwatch.GetTimestamp();
            foreach (var (_, list) in _waiters)
            {
                foreach (var waiter in list)
                {
                    if (memory is not null &&
                        !ReferenceEquals(waiter.Memory, memory))
                    {
                        continue;
                    }

                    outstanding++;
                    if (waiter.Latched)
                    {
                        latched++;
                    }

                    if (waiter.RegisteredTicks != 0 &&
                        waiter.RegisteredTicks < oldestTicks)
                    {
                        oldestTicks = waiter.RegisteredTicks;
                        sampleAddress = waiter.WaitAddress;
                        sampleQueue = waiter.QueueName;
                    }
                }
            }

            var oldestAgeMs = oldestTicks == long.MaxValue || oldestTicks == 0
                ? 0L
                : (now - oldestTicks) * 1000L / Stopwatch.Frequency;
            return new OutstandingSnapshot(
                outstanding,
                latched,
                oldestAgeMs,
                sampleAddress,
                sampleQueue);
        }
    }

    public static void Register(ulong address, WaitingDcb waiter)
    {
        waiter.WaitAddress = address;
        waiter.Memory = Canonicalize(waiter.Memory);
        waiter.WaitFrameId = System.Threading.Volatile.Read(ref _currentFrameId);
        lock (_gate)
        {
            if (!_waiters.TryGetValue(address, out var list))
            {
                list = new List<WaitingDcb>();
                _waiters.Add(address, list);
            }

            list.Add(waiter);
        }
    }

    /// <summary>
    /// Re-evaluates every registered waiter. <paramref name="readValue"/>
    /// receives (address, is64Bit) and returns null when the memory is
    /// unreadable; such waiters are kept registered. Returns the waiters whose
    /// condition is now satisfied (removed from the registry), or null.
    /// </summary>
    public static List<WaitingDcb>? CollectSatisfied(
        object memory,
        Func<ulong, bool, ulong?> readValue)
    {
        memory = Canonicalize(memory)!;
        List<WaitingDcb>? woken = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(list[i].Memory, memory))
                    {
                        continue;
                    }

                    var satisfied = list[i].Latched;
                    if (!satisfied)
                    {
                        var value = readValue(address, list[i].Is64Bit);
                        satisfied = value is not null && Compare(list[i], value.Value);
                    }

                    if (!satisfied)
                    {
                        continue;
                    }

                    woken ??= new List<WaitingDcb>();
                    woken.Add(list[i]);
                    list.RemoveAt(i);
                }

                if (list.Count == 0)
                {
                    emptied ??= new List<ulong>();
                    emptied.Add(address);
                }
            }

            if (emptied is not null)
            {
                foreach (var address in emptied)
                {
                    _waiters.Remove(address);
                }
            }
        }

        return woken;
    }

    /// <summary>
    /// Returns waiters that have remained unsatisfied longer than
    /// <paramref name="maxAgeTicks"/> exactly once, without removing them or
    /// changing their labels. Missing GPU work must fail closed: advancing a
    /// command buffer without its real producer corrupts cross-queue ordering.
    /// </summary>
    public static List<WaitingDcb>? CollectUnreportedStale(
        object memory,
        long nowTicks,
        long maxAgeTicks)
    {
        memory = Canonicalize(memory)!;
        List<WaitingDcb>? stale = null;
        lock (_gate)
        {
            foreach (var (_, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var waiter = list[i];
                    if (!ReferenceEquals(waiter.Memory, memory) ||
                        waiter.StaleReported ||
                        nowTicks - waiter.RegisteredTicks < maxAgeTicks)
                    {
                        continue;
                    }

                    stale ??= new List<WaitingDcb>();
                    waiter.StaleReported = true;
                    list[i] = waiter;
                    stale.Add(waiter);
                }
            }
        }

        return stale;
    }

    /// <summary>
    /// Returns watched labels overlapped by a newly discovered producer. Used
    /// only for diagnostics; producer completion still wakes through the
    /// normal CollectSatisfied path after the ordered memory write executes.
    /// </summary>
    public static List<(ulong Address, int Count)> SnapshotInRange(
        object memory,
        ulong start,
        ulong length)
    {
        memory = Canonicalize(memory)!;
        var matches = new List<(ulong Address, int Count)>();
        if (length == 0)
        {
            return matches;
        }

        var end = start > ulong.MaxValue - length ? ulong.MaxValue : start + length;
        lock (_gate)
        {
            foreach (var (address, list) in _waiters)
            {
                var matchingCount = 0;
                var any64Bit = false;
                foreach (var waiter in list)
                {
                    if (!ReferenceEquals(waiter.Memory, memory))
                    {
                        continue;
                    }

                    matchingCount++;
                    any64Bit |= waiter.Is64Bit;
                }

                if (matchingCount == 0)
                {
                    continue;
                }

                var width = any64Bit
                    ? sizeof(ulong)
                    : sizeof(uint);
                var waitEnd = address > ulong.MaxValue - (ulong)width
                    ? ulong.MaxValue
                    : address + (ulong)width;
                if (start < waitEnd && address < end)
                {
                    matches.Add((address, matchingCount));
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Records satisfaction for every waiter at <paramref name="address"/> whose
    /// condition is met by <paramref name="value"/> — the value a producer just
    /// wrote to that label. Called from the ordered producer side effect so a
    /// same-frame label reset cannot lose the wakeup. The waiters stay registered
    /// (latched) and are drained by the next CollectSatisfied. Returns true when
    /// at least one waiter latched, so the caller can trigger a wake pass.
    /// </summary>
    public static bool LatchSatisfiedByValue(object memory, ulong address, ulong value)
    {
        memory = Canonicalize(memory)!;
        var latchedAny = false;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(address, out var list))
            {
                return false;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var waiter = list[i];
                if (waiter.Latched ||
                    !ReferenceEquals(waiter.Memory, memory) ||
                    !Compare(waiter, value))
                {
                    continue;
                }

                waiter.Latched = true;
                list[i] = waiter;
                latchedAny = true;
            }
        }

        return latchedAny;
    }

    /// <summary>
    /// Every registered waiter, for the flip-stall watchdog. Not filtered by
    /// memory identity — the watchdog wants a whole-process view.
    /// </summary>
    public static List<WaitingDcb> SnapshotAll()
    {
        var snapshot = new List<WaitingDcb>();
        lock (_gate)
        {
            foreach (var (_, list) in _waiters)
            {
                snapshot.AddRange(list);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Removes the waiter at <paramref name="address"/> whose State is
    /// <paramref name="state"/> — used when a new submission supersedes a
    /// ring-tail park that would otherwise pin the queue forever.
    /// </summary>
    public static bool TryRemoveByState(object state, ulong address)
    {
        lock (_gate)
        {
            if (!_waiters.TryGetValue(address, out var list))
            {
                return false;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(list[i].State, state))
                {
                    list.RemoveAt(i);
                    if (list.Count == 0)
                    {
                        _waiters.Remove(address);
                    }

                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Removes and returns waiters carrying a <see cref="WaitingDcb.RetryDeadlineTicks"/>
    /// that has elapsed. Used for indirect-dispatch dimension retries: the caller
    /// resumes them so a genuinely empty dispatch (dims that never become non-zero)
    /// is dropped after a bounded wait instead of stalling the queue forever.
    /// </summary>
    public static List<WaitingDcb>? CollectExpiredRetries(object memory, long nowTicks)
    {
        memory = Canonicalize(memory)!;
        List<WaitingDcb>? expired = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var waiter = list[i];
                    if (waiter.RetryDeadlineTicks == 0 ||
                        !ReferenceEquals(waiter.Memory, memory) ||
                        nowTicks < waiter.RetryDeadlineTicks)
                    {
                        continue;
                    }

                    expired ??= new List<WaitingDcb>();
                    expired.Add(waiter);
                    list.RemoveAt(i);
                }

                if (list.Count == 0)
                {
                    emptied ??= new List<ulong>();
                    emptied.Add(address);
                }
            }

            if (emptied is not null)
            {
                foreach (var address in emptied)
                {
                    _waiters.Remove(address);
                }
            }
        }

        return expired;
    }

    public static List<WaitingDcb>? CollectAllForMemory(object memory)
    {
        memory = Canonicalize(memory)!;
        List<WaitingDcb>? collected = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var index = list.Count - 1; index >= 0; index--)
                {
                    if (!ReferenceEquals(list[index].Memory, memory))
                    {
                        continue;
                    }

                    collected ??= new List<WaitingDcb>();
                    collected.Add(list[index]);
                    list.RemoveAt(index);
                }

                if (list.Count == 0)
                {
                    emptied ??= new List<ulong>();
                    emptied.Add(address);
                }
            }

            if (emptied is not null)
            {
                foreach (var address in emptied)
                {
                    _waiters.Remove(address);
                }
            }
        }

        return collected;
    }

    /// <summary>
    /// Drops produced-label values that no registered waiter is watching. Called
    /// under <see cref="_gate"/> when the table reaches its soft bound. A value
    /// still watched by a waiter is the only thing that can release that waiter
    /// once the guest recycles its label, so those are always retained even if
    /// the table has to grow past the bound.
    /// </summary>
    private static void PruneUnwatchedProducedLocked()
    {
        List<(object Memory, ulong Address)>? unwatched = null;
        foreach (var (key, _) in _lastProduced)
        {
            if (_waiters.TryGetValue(key.Item2, out var list))
            {
                var watched = false;
                foreach (var waiter in list)
                {
                    if (ReferenceEquals(waiter.Memory, key.Item1))
                    {
                        watched = true;
                        break;
                    }
                }

                if (watched)
                {
                    continue;
                }
            }

            (unwatched ??= []).Add(key);
        }

        if (unwatched is null)
        {
            return;
        }

        foreach (var key in unwatched)
        {
            _lastProduced.Remove(key);
        }
    }

    /// <summary>Records the value a label producer wrote, for the deadlock
    /// breaker. Also latches any already-waiting waiter it satisfies.</summary>
    public static bool RecordProduced(object memory, ulong address, ulong value)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            if (_lastProduced.Count >= 8192)
            {
                // These entries are release state, not a cache. CollectDeadlockBroken
                // can only free a waiter whose label the guest has since recycled by
                // replaying the value a real producer wrote to it, so clearing the
                // table wholesale strands every such waiter forever — the suspended
                // queue then never resumes and the title wedges with its render
                // thread parked. Drop only values no live waiter is watching, and
                // let the table exceed the bound when they all are.
                PruneUnwatchedProducedLocked();
            }

            _lastProduced[(memory, address)] = value;
            _labelFrameIds[(memory, address)] = System.Threading.Volatile.Read(ref _currentFrameId);
        }

        return LatchSatisfiedByValue(memory, address, value);
    }

    /// <summary>
    /// Breaks cross-queue GPU deadlocks the serial parser cannot avoid: returns
    /// (and removes) waiters that have been stuck longer than
    /// <paramref name="minAgeTicks"/> and whose condition is satisfied by the
    /// last value a real producer wrote to their label — even though guest
    /// memory has since been reset. Never fabricates a value: a waiter is only
    /// released when an actual producer signalled it at least once.
    /// </summary>
    public static List<WaitingDcb>? CollectDeadlockBroken(
        object memory,
        long nowTicks,
        long minAgeTicks)
    {
        memory = Canonicalize(memory)!;
        List<WaitingDcb>? broken = null;
        lock (_gate)
        {
            List<ulong>? emptied = null;
            foreach (var (address, list) in _waiters)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var waiter = list[i];
                    if (!ReferenceEquals(waiter.Memory, memory) ||
                        nowTicks - waiter.RegisteredTicks < minAgeTicks ||
                        !_lastProduced.TryGetValue((memory, address), out var produced) ||
                        !_labelFrameIds.TryGetValue((memory, address), out var produceFrame) ||
                        produceFrame < waiter.WaitFrameId ||
                        !Compare(waiter, produced))
                    {
                        continue;
                    }

                    broken ??= new List<WaitingDcb>();
                    broken.Add(waiter);
                    list.RemoveAt(i);
                }

                if (list.Count == 0)
                {
                    emptied ??= new List<ulong>();
                    emptied.Add(address);
                }
            }

            if (emptied is not null)
            {
                foreach (var address in emptied)
                {
                    _waiters.Remove(address);
                }
            }
        }

        return broken;
    }

    private static bool IsComputeQueue(string? queueName) =>
        queueName is not null &&
        queueName.StartsWith("acb.compute", StringComparison.Ordinal);

    private static bool IsGraphicsQueue(string? queueName) =>
        queueName is not null &&
        queueName.StartsWith("dcb.graphics", StringComparison.Ordinal);

    private static long _lastCircularBreakTicks;
    private static int _circularBreaksThisWindow;

    /// <summary>
    /// Astro-style A↔B hang: graphics waits for a compute label while compute
    /// waits for a graphics label further down the same CB. Replaying a stale
    /// produced value onto graphics races the title draw ahead of meshlet work.
    /// Prefer force-resuming aged compute waiters so they can signal graphics.
    /// Throttled: at most two breaks per second, otherwise we keep skipping
    /// waits that still need a real producer (asset/meshlet fill never lands).
    /// </summary>
    public static List<WaitingDcb>? CollectCircularComputeBreaks(
        object memory,
        long nowTicks,
        long minAgeTicks)
    {
        memory = Canonicalize(memory)!;
        var windowTicks = System.Diagnostics.Stopwatch.Frequency; // ~1s
        if (nowTicks - System.Threading.Volatile.Read(ref _lastCircularBreakTicks) > windowTicks)
        {
            System.Threading.Volatile.Write(ref _lastCircularBreakTicks, nowTicks);
            System.Threading.Volatile.Write(ref _circularBreaksThisWindow, 0);
        }

        if (System.Threading.Volatile.Read(ref _circularBreaksThisWindow) >= 2)
        {
            return null;
        }

        List<WaitingDcb>? broken = null;
        lock (_gate)
        {
            WaitingDcb? oldestGraphics = null;
            foreach (var (_, list) in _waiters)
            {
                foreach (var waiter in list)
                {
                    if (!ReferenceEquals(waiter.Memory, memory) ||
                        !IsGraphicsQueue(waiter.QueueName) ||
                        nowTicks - waiter.RegisteredTicks < minAgeTicks)
                    {
                        continue;
                    }

                    if (oldestGraphics is null ||
                        waiter.RegisteredTicks < oldestGraphics.Value.RegisteredTicks)
                    {
                        oldestGraphics = waiter;
                    }
                }
            }

            if (oldestGraphics is null)
            {
                return null;
            }

            // Only break compute waiters that look like the mid-CB handshake
            // graphics will release later (0x40020xxx labels), not arbitrary
            // compute waits that still need a real producer.
            WaitingDcb? oldestCompute = null;
            ulong oldestAddress = 0;
            foreach (var (address, list) in _waiters)
            {
                if (address < 0x0000000400200000UL || address >= 0x0000000400210000UL)
                {
                    continue;
                }

                foreach (var waiter in list)
                {
                    if (!ReferenceEquals(waiter.Memory, memory) ||
                        !IsComputeQueue(waiter.QueueName) ||
                        nowTicks - waiter.RegisteredTicks < minAgeTicks)
                    {
                        continue;
                    }

                    if (oldestCompute is null ||
                        waiter.RegisteredTicks < oldestCompute.Value.RegisteredTicks)
                    {
                        oldestCompute = waiter;
                        oldestAddress = address;
                    }
                }
            }

            if (oldestCompute is null)
            {
                return null;
            }

            if (!_waiters.TryGetValue(oldestAddress, out var computeList))
            {
                return null;
            }

            for (var i = computeList.Count - 1; i >= 0; i--)
            {
                var waiter = computeList[i];
                if (!ReferenceEquals(waiter.Memory, memory) ||
                    waiter.RegisteredTicks != oldestCompute.Value.RegisteredTicks ||
                    waiter.WaitAddress != oldestCompute.Value.WaitAddress)
                {
                    continue;
                }

                broken = [waiter];
                computeList.RemoveAt(i);
                break;
            }

            if (computeList.Count == 0)
            {
                _waiters.Remove(oldestAddress);
            }
        }

        if (broken is not null)
        {
            System.Threading.Interlocked.Increment(ref _circularBreaksThisWindow);
        }

        return broken;
    }

    // Under orphan force-submit, producers can run ahead of waiter
    // registration and pass an equal-compare value before it's ever seen.
    // Treat == as "reached or passed" only in that mode, so other titles
    // keep exact hardware semantics. SHARPEMU_GPU_WAIT_EQ_EXACT=1 restores
    // strict equality for A/B.
    private static readonly bool _equalCompareExact =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_EQ_EXACT"),
            "1",
            StringComparison.Ordinal) ||
        !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES"),
            "1",
            StringComparison.Ordinal);

    public static bool Compare(in WaitingDcb waiter, ulong value)
    {
        var masked = value & waiter.Mask;
        var reference = waiter.ReferenceValue & waiter.Mask;
        return waiter.CompareFunction switch
        {
            0 => true,
            1 => masked < reference,
            2 => masked <= reference,
            3 => _equalCompareExact ? masked == reference : masked >= reference,
            4 => masked != reference,
            5 => masked >= reference,
            6 => masked > reference,
            // 7 is reserved; treating it as satisfied keeps a malformed packet
            // from suspending forever.
            _ => true,
        };
    }

    public static void Clear()
    {
        lock (_gate)
        {
            _waiters.Clear();
            _lastProduced.Clear();
            _labelFrameIds.Clear();
        }

        System.Threading.Volatile.Write(ref _currentFrameId, 0);
        System.Threading.Volatile.Write(ref _lastCircularBreakTicks, 0);
        System.Threading.Volatile.Write(ref _circularBreaksThisWindow, 0);
    }
}

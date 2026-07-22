// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, List<WaitingDcb>> _waiters = new();
    // The last value each label producer wrote. Used only by the deadlock
    // breaker: our serial submission parser cannot model two GPU queues running
    // concurrently, so a label written -> reset -> re-waited across queues can
    // cycle forever even though a real producer did signal it. Keyed by (memory,
    // address) so distinct guest processes never alias.
    private static readonly Dictionary<(object, ulong), ulong> _lastProduced = new();

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

    public static void Register(ulong address, WaitingDcb waiter)
    {
        waiter.WaitAddress = address;
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
    /// Removes and returns waiters carrying a <see cref="WaitingDcb.RetryDeadlineTicks"/>
    /// that has elapsed. Used for indirect-dispatch dimension retries: the caller
    /// resumes them so a genuinely empty dispatch (dims that never become non-zero)
    /// is dropped after a bounded wait instead of stalling the queue forever.
    /// </summary>
    public static List<WaitingDcb>? CollectExpiredRetries(object memory, long nowTicks)
    {
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

    /// <summary>Records the value a label producer wrote, for the deadlock
    /// breaker. Also latches any already-waiting waiter it satisfies.</summary>
    public static bool RecordProduced(object memory, ulong address, ulong value)
    {
        lock (_gate)
        {
            if (_lastProduced.Count >= 8192)
            {
                _lastProduced.Clear();
            }

            _lastProduced[(memory, address)] = value;
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

    public static bool Compare(in WaitingDcb waiter, ulong value)
    {
        var masked = value & waiter.Mask;
        var reference = waiter.ReferenceValue & waiter.Mask;
        return waiter.CompareFunction switch
        {
            0 => true,
            1 => masked < reference,
            2 => masked <= reference,
            3 => masked == reference,
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
        }
    }
}

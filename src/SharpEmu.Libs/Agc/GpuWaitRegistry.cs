// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu;

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
    private static readonly bool _highDwordWaitWakeEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_LABEL_HIGH_DWORD_WAKE"),
        "0",
        StringComparison.Ordinal);

    internal enum WaitRegistrationResult : byte
    {
        Unreadable,
        Satisfied,
        SatisfiedByHistory,
        Registered,
    }

    internal enum VirtualLabelEngine : byte
    {
        Pfp,
        Me,
        Mec,
    }

    internal enum VirtualLabelVisibility : byte
    {
        Gpu,
        Cpu,
    }

    public readonly record struct VirtualLabelPublication(
        ulong Address,
        uint DwordCount,
        ulong Generation);

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
        // ATOMIC_MEM loops require an exact comparison. An ordinary label wait
        // can use a compatibility mode that treats equality as reached.
        public bool RequiresExactEquality;
        // MEM_SEMAPHORE is a counting wait. Only one signal can release one
        // waiter, so generic label comparisons must not latch this entry.
        public bool IsMemSemaphore;
        public object? Memory;
        public string? QueueName;
        public ulong SubmissionId;
        // A delayed parser can reach this wait after the producer publishes
        // and resets the label. Only newer publications can satisfy it.
        public ulong SubmissionPublicationGeneration;
        // Stopwatch timestamp captured at registration. Stale waiters remain
        // registered; this only controls one-shot diagnostics.
        public long RegisteredTicks;
        public bool StaleReported;
        public object? State;
        // Latched when a producer wrote a value that
        // satisfies this waiter. The label is frequently reused (reset to 0 for
        // the next frame) immediately after the producing write, so re-reading
        // guest memory at wake time can miss the transient satisfied window.
        // Latching records satisfaction at the moment of the write instead.
        public bool Latched;
        // Vulkan timeline values that must precede the resumed queue's next
        // GPU submission. This is empty for CPU-visible labels.
        public GuestGpuLabelDependency Dependency;
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
    // Frame-staleness guard: tracks the frame ID of each label write so that
    // WAIT_REG_MEM in frame N+1 is not satisfied by a stale write from frame N.
    private static readonly Dictionary<(object, ulong), long> _labelFrameIds = new();
    private static long _currentFrameId;
    private readonly record struct VirtualLabelValue(
        uint Value,
        GuestGpuLabelDependency Dependency,
        ulong Generation,
        ulong PublicationAddress,
        uint DwordCount,
        uint CachePolicy,
        VirtualLabelEngine Engine,
        VirtualLabelVisibility Visibility);
    private static readonly Dictionary<(object, ulong), VirtualLabelValue>
        _virtualLabels = new();
    // An active submission retains all newer publications until its parser
    // completes. No history is needed when no submission can observe it.
    private static readonly Dictionary<(object, ulong), List<VirtualLabelValue>>
        _virtualLabelHistory = new();
    private static readonly Dictionary<object, SortedDictionary<ulong, int>>
        _activeSubmissionGenerations = new();
    private static ulong _nextLabelPublicationGeneration;

  
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
            return IsLabelFreshLocked(memory, address);
        }
    }

    private static bool IsLabelFreshLocked(object memory, ulong address) =>
        !_labelFrameIds.TryGetValue((memory, address), out var frameId) ||
        frameId >= System.Threading.Volatile.Read(ref _currentFrameId);

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
        lock (_gate)
        {
            RegisterLocked(address, waiter);
        }
    }

    internal static int GetPublicationHistoryCountForTests(
        object memory,
        ulong address)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            return _virtualLabelHistory.TryGetValue(
                (memory, address),
                out var history)
                ? history.Count
                : 0;
        }
    }

    private static void RegisterLocked(ulong address, WaitingDcb waiter)
    {
        if (!_waiters.TryGetValue(address, out var list))
        {
            list = new List<WaitingDcb>();
            _waiters.Add(address, list);
        }

        list.Add(waiter);
    }

    /// <summary>
    /// Reads and registers one wait while holding the publication lock. A
    /// producer cannot publish and reset the label between these operations.
    /// </summary>
    public static WaitRegistrationResult RegisterIfUnsatisfied(
        WaitingDcb waiter,
        Func<ulong, bool, ulong?> readValue,
        out ulong currentValue,
        out GuestGpuLabelDependency dependency)
    {
        currentValue = 0;
        dependency = default;
        waiter.Memory = Canonicalize(waiter.Memory);
        if (waiter.Memory is null)
        {
            return WaitRegistrationResult.Unreadable;
        }

        lock (_gate)
        {
            ulong? value;
            var waitCachePolicy = (waiter.ControlValue >> 25) & 0x3u;
            if (waitCachePolicy <= 2 &&
                TryReadVirtualLocked(
                    waiter.Memory,
                    waiter.WaitAddress,
                    waiter.Is64Bit,
                    out var virtualValue,
                    out dependency,
                    waitCachePolicy,
                    waiter.Mask))
            {
                value = virtualValue;
            }
            else
            {
                value = readValue(waiter.WaitAddress, waiter.Is64Bit);
            }

            if (value is null)
            {
                return WaitRegistrationResult.Unreadable;
            }

            currentValue = value.Value;
            if (Compare(waiter, currentValue) &&
                IsLabelFreshLocked(waiter.Memory, waiter.WaitAddress))
            {
                return WaitRegistrationResult.Satisfied;
            }

            if (TryFindSubmittedPublicationLocked(
                    waiter,
                    out var submittedValue,
                    out var submittedDependency))
            {
                currentValue = submittedValue;
                dependency = submittedDependency;
                return WaitRegistrationResult.SatisfiedByHistory;
            }

            RegisterLocked(waiter.WaitAddress, waiter);
            return WaitRegistrationResult.Registered;
        }
    }

    public static ulong BeginSubmission(object memory)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            var generation = _nextLabelPublicationGeneration;
            if (!_activeSubmissionGenerations.TryGetValue(memory, out var active))
            {
                active = new SortedDictionary<ulong, int>();
                _activeSubmissionGenerations.Add(memory, active);
            }

            active[generation] = active.TryGetValue(generation, out var count)
                ? count + 1
                : 1;
            return generation;
        }
    }

    public static void EndSubmission(object memory, ulong generation)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            if (!_activeSubmissionGenerations.TryGetValue(memory, out var active) ||
                !active.TryGetValue(generation, out var count))
            {
                return;
            }

            if (count == 1)
            {
                active.Remove(generation);
            }
            else
            {
                active[generation] = count - 1;
            }

            if (active.Count == 0)
            {
                _activeSubmissionGenerations.Remove(memory);
            }

            PrunePublicationHistoryLocked(memory);
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
                        var waiter = list[i];
                        if (waiter.IsMemSemaphore)
                        {
                            continue;
                        }

                        ulong? value;
                        GuestGpuLabelDependency dependency = default;
                        var waitCachePolicy = (waiter.ControlValue >> 25) & 0x3u;
                        if (waitCachePolicy <= 2 &&
                            TryReadVirtualLocked(
                                memory,
                                address,
                                waiter.Is64Bit,
                                out var virtualValue,
                                out dependency,
                                waitCachePolicy,
                                waiter.Mask))
                        {
                            value = virtualValue;
                        }
                        else
                        {
                            value = readValue(address, waiter.Is64Bit);
                        }
                        satisfied = value is not null && Compare(list[i], value.Value);
                        if (satisfied && !dependency.IsEmpty)
                        {
                            waiter.Dependency = waiter.Dependency.Merge(dependency);
                            list[i] = waiter;
                        }
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
    /// Returns the waiters for one label. This method is for diagnostics only.
    /// </summary>
    public static List<WaitingDcb>? SnapshotAt(object memory, ulong address)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(address, out var list))
            {
                return null;
            }

            List<WaitingDcb>? matches = null;
            foreach (var waiter in list)
            {
                if (!ReferenceEquals(waiter.Memory, memory))
                {
                    continue;
                }

                matches ??= new List<WaitingDcb>();
                matches.Add(waiter);
            }

            return matches;
        }
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
        lock (_gate)
        {
            return LatchSatisfiedByValueLocked(memory, address, value);
        }
    }

    private static bool LatchSatisfiedByValueLocked(
        object memory,
        ulong address,
        ulong value,
        bool include64BitWaiters = true)
    {
        if (!_waiters.TryGetValue(address, out var list))
        {
            return false;
        }

        var latchedAny = false;
        for (var index = 0; index < list.Count; index++)
        {
            var waiter = list[index];
            if (waiter.Latched ||
                waiter.IsMemSemaphore ||
                (!include64BitWaiters && waiter.Is64Bit) ||
                !ReferenceEquals(waiter.Memory, memory) ||
                !Compare(waiter, value))
            {
                continue;
            }

            waiter.Latched = true;
            list[index] = waiter;
            latchedAny = true;
        }

        return latchedAny;
    }

    /// <summary>
    /// Commits one semaphore signal and assigns its token to the oldest waiter.
    /// The caller holds the semaphore counter lock while this method holds the
    /// waiter lock, so registration cannot race the counter update.
    /// </summary>
    internal static bool CommitMemSemaphoreSignal(
        object memory,
        ulong address,
        Func<bool, bool> commitCounter,
        out bool waiterAssigned)
    {
        memory = Canonicalize(memory)!;
        waiterAssigned = false;
        lock (_gate)
        {
            var waiterIndex = -1;
            if (_waiters.TryGetValue(address, out var list))
            {
                for (var index = 0; index < list.Count; index++)
                {
                    var waiter = list[index];
                    if (!waiter.Latched &&
                        waiter.IsMemSemaphore &&
                        ReferenceEquals(waiter.Memory, memory))
                    {
                        waiterIndex = index;
                        break;
                    }
                }
            }

            if (!commitCounter(waiterIndex >= 0))
            {
                return false;
            }

            if (waiterIndex >= 0)
            {
                var waiter = list![waiterIndex];
                waiter.Latched = true;
                list[waiterIndex] = waiter;
                waiterAssigned = true;
            }

            return true;
        }
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
    /// Removes all waiters owned by one queue state in one guest memory.
    /// A stopped queue must not resume when a later label changes.
    /// </summary>
    public static int RemoveAllByState(object memory, object state)
    {
        memory = Canonicalize(memory)!;
        var removed = 0;
        lock (_gate)
        {
            foreach (var address in _waiters.Keys.ToArray())
            {
                var list = _waiters[address];
                for (var index = list.Count - 1; index >= 0; index--)
                {
                    var waiter = list[index];
                    if (ReferenceEquals(waiter.Memory, memory) &&
                        ReferenceEquals(waiter.State, state))
                    {
                        list.RemoveAt(index);
                        removed++;
                    }
                }

                if (list.Count == 0)
                {
                    _waiters.Remove(address);
                }
            }
        }

        return removed;
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
    public static bool RecordProduced(
        object memory,
        ulong address,
        ulong value,
        bool hasHighDword = false)
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
            _virtualLabels.Remove((memory, address));
            if (hasHighDword)
            {
                var highAddress = address + sizeof(uint);
                _lastProduced[(memory, highAddress)] = unchecked((uint)(value >> 32));
                _virtualLabels.Remove((memory, highAddress));
            }

            var latched = LatchSatisfiedByValueLocked(memory, address, value);
            if (hasHighDword && _highDwordWaitWakeEnabled)
            {
                // The high half is complete only for a 32-bit wait at address
                // plus four. A 64-bit wait there also needs the next dword.
                latched |= LatchSatisfiedByValueLocked(
                    memory,
                    address + sizeof(uint),
                    unchecked((uint)(value >> 32)),
                    include64BitWaiters: false);
            }

            var generation = ++_nextLabelPublicationGeneration;
            var dwordCount = hasHighDword ? 2u : 1u;
            AppendPublicationHistoryLocked(
                memory,
                address,
                new VirtualLabelValue(
                    unchecked((uint)value),
                    default,
                    generation,
                    address,
                    dwordCount,
                    CachePolicy: 0,
                    VirtualLabelEngine.Pfp,
                    VirtualLabelVisibility.Cpu));
            if (hasHighDword)
            {
                AppendPublicationHistoryLocked(
                    memory,
                    address + sizeof(uint),
                    new VirtualLabelValue(
                        unchecked((uint)(value >> 32)),
                        default,
                        generation,
                        address,
                        dwordCount,
                        CachePolicy: 0,
                        VirtualLabelEngine.Pfp,
                        VirtualLabelVisibility.Cpu));
            }

            return latched;
        }
    }

    private static void AppendPublicationHistoryLocked(
        object memory,
        ulong dwordAddress,
        VirtualLabelValue published)
    {
        if (!_activeSubmissionGenerations.TryGetValue(memory, out var active) ||
            active.Count == 0)
        {
            return;
        }

        if (!_virtualLabelHistory.TryGetValue(
                (memory, dwordAddress),
                out var history))
        {
            history = new List<VirtualLabelValue>(4);
            _virtualLabelHistory.Add((memory, dwordAddress), history);
        }

        history.Add(published);
        var oldestGeneration = active.First().Key;
        history.RemoveAll(value => value.Generation <= oldestGeneration);
    }

    private static void PrunePublicationHistoryLocked(object memory)
    {
        var hasActive = _activeSubmissionGenerations.TryGetValue(memory, out var active) &&
                        active.Count != 0;
        var oldestGeneration = hasActive ? active!.First().Key : 0UL;
        List<(object, ulong)>? empty = null;
        foreach (var (key, history) in _virtualLabelHistory)
        {
            if (!ReferenceEquals(key.Item1, memory))
            {
                continue;
            }

            if (hasActive)
            {
                history.RemoveAll(value => value.Generation <= oldestGeneration);
            }
            else
            {
                history.Clear();
            }

            if (history.Count == 0)
            {
                (empty ??= []).Add(key);
            }
        }

        if (empty is null)
        {
            return;
        }

        foreach (var key in empty)
        {
            _virtualLabelHistory.Remove(key);
        }
    }

    /// <summary>
    /// Publishes one GPU-visible label packet as a single state transition.
    /// Waiters cannot observe a mixture of dwords from two publications.
    /// </summary>
    public static VirtualLabelPublication RecordVirtualProducedRange(
        object memory,
        ulong address,
        ReadOnlySpan<uint> values,
        GuestGpuLabelDependency dependency,
        uint cachePolicy,
        VirtualLabelEngine engine,
        VirtualLabelVisibility visibility = VirtualLabelVisibility.Gpu)
    {
        if (values.IsEmpty)
        {
            return default;
        }

        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            if (_virtualLabels.Count + values.Length >= 8192)
            {
                PruneUnwatchedVirtualLocked();
            }

            var generation = ++_nextLabelPublicationGeneration;
            var dwordCount = checked((uint)values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                var dwordAddress = address + checked((ulong)index * sizeof(uint));
                var published = new VirtualLabelValue(
                    values[index],
                    dependency,
                    generation,
                    address,
                    dwordCount,
                    cachePolicy,
                    engine,
                    visibility);
                _virtualLabels[(memory, dwordAddress)] = published;
                AppendPublicationHistoryLocked(memory, dwordAddress, published);
                _lastProduced[(memory, dwordAddress)] = values[index];
                _labelFrameIds[(memory, dwordAddress)] =
                    System.Threading.Volatile.Read(ref _currentFrameId);
            }

            if (values.Length >= 2)
            {
                _lastProduced[(memory, address)] =
                    values[0] | ((ulong)values[1] << 32);
            }

            // All dwords are visible before any waiter is evaluated.
            for (var index = 0; index < values.Length; index++)
            {
                LatchVirtualWaitersLocked(
                    memory,
                    address + checked((ulong)index * sizeof(uint)));
            }

            return new VirtualLabelPublication(address, dwordCount, generation);
        }
    }

    /// <summary>
    /// Returns true when all dwords still belong to one virtual publication.
    /// A delayed host mirror must not replace a newer label value.
    /// </summary>
    public static bool IsCurrentVirtualPublication(
        object memory,
        VirtualLabelPublication publication)
    {
        if (publication.Generation == 0 || publication.DwordCount == 0)
        {
            return false;
        }

        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            for (uint index = 0; index < publication.DwordCount; index++)
            {
                var address = publication.Address + ((ulong)index * sizeof(uint));
                if (!_virtualLabels.TryGetValue((memory, address), out var value) ||
                    value.Generation != publication.Generation ||
                    value.PublicationAddress != publication.Address ||
                    value.DwordCount != publication.DwordCount)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Removes a virtual publication after its host mirror becomes visible.
    /// A newer publication at the same address stays current.
    /// </summary>
    public static bool RetireVirtualPublication(
        object memory,
        VirtualLabelPublication publication)
    {
        if (publication.Generation == 0 || publication.DwordCount == 0)
        {
            return false;
        }

        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            for (uint index = 0; index < publication.DwordCount; index++)
            {
                var address = publication.Address + ((ulong)index * sizeof(uint));
                if (!_virtualLabels.TryGetValue((memory, address), out var value) ||
                    value.Generation != publication.Generation ||
                    value.PublicationAddress != publication.Address ||
                    value.DwordCount != publication.DwordCount)
                {
                    return false;
                }
            }

            for (uint index = 0; index < publication.DwordCount; index++)
            {
                var address = publication.Address + ((ulong)index * sizeof(uint));
                _virtualLabels.Remove((memory, address));
            }

            return true;
        }
    }

    /// <summary>
    /// Publishes a single GPU-visible dword. This wrapper keeps call sites that
    /// naturally produce one value on the atomic range path.
    /// </summary>
    public static void RecordVirtualProduced(
        object memory,
        ulong address,
        uint value,
        GuestGpuLabelDependency dependency) =>
        _ = RecordVirtualProducedRange(
            memory,
            address,
            new[] { value },
            dependency,
            cachePolicy: 0,
            engine: VirtualLabelEngine.Pfp);

    private static void LatchVirtualWaitersLocked(object memory, ulong producedAddress)
    {
        LatchAt(producedAddress);
        if (producedAddress >= sizeof(uint))
        {
            // A 64-bit waiter is keyed by its low dword. Publishing its high
            // dword completes the value, so recheck the preceding address too.
            LatchAt(producedAddress - sizeof(uint));
        }

        void LatchAt(ulong waitAddress)
        {
            if (!_waiters.TryGetValue(waitAddress, out var list))
            {
                return;
            }

            for (var index = 0; index < list.Count; index++)
            {
                var waiter = list[index];
                if (waiter.Latched ||
                    waiter.IsMemSemaphore ||
                    !ReferenceEquals(waiter.Memory, memory) ||
                    !TryReadVirtualLocked(
                        memory,
                        waitAddress,
                        waiter.Is64Bit,
                        out var virtualValue,
                        out var virtualDependency,
                        (waiter.ControlValue >> 25) & 0x3u,
                        waiter.Mask) ||
                    !Compare(waiter, virtualValue))
                {
                    continue;
                }

                waiter.Latched = true;
                waiter.Dependency = waiter.Dependency.Merge(virtualDependency);
                list[index] = waiter;
            }
        }
    }

    /// <summary>
    /// Reads a GPU-only label and its producer timeline token.
    /// </summary>
    public static bool TryReadVirtual(
        object memory,
        ulong address,
        bool is64Bit,
        out ulong value,
        out GuestGpuLabelDependency dependency,
        uint? requiredCachePolicy = null)
    {
        memory = Canonicalize(memory)!;
        lock (_gate)
        {
            return TryReadVirtualLocked(
                memory,
                address,
                is64Bit,
                out value,
                out dependency,
                requiredCachePolicy);
        }
    }

    private static bool TryReadVirtualLocked(
        object memory,
        ulong address,
        bool is64Bit,
        out ulong value,
        out GuestGpuLabelDependency dependency,
        uint? requiredCachePolicy = null,
        ulong? requiredMask = null)
    {
        value = 0;
        dependency = default;
        if (!_virtualLabels.TryGetValue((memory, address), out var low))
        {
            return false;
        }

        // LRU, Stream, and Noalloc select how the GPU accesses GL2. They do
        // not create separate visibility domains. Bypass is not valid for a
        // GPU label read.
        if (low.Visibility != VirtualLabelVisibility.Gpu ||
            requiredCachePolicy > 2)
        {
            return false;
        }

        value = low.Value;
        dependency = low.Dependency;
        if (!is64Bit)
        {
            return true;
        }

        if ((requiredMask.GetValueOrDefault(ulong.MaxValue) &
             0xFFFF_FFFF_0000_0000UL) == 0)
        {
            return true;
        }

        if (!_virtualLabels.TryGetValue(
                (memory, address + sizeof(uint)),
                out var high))
        {
            value = 0;
            dependency = default;
            return false;
        }

        if (address < low.PublicationAddress)
        {
            value = 0;
            dependency = default;
            return false;
        }

        var byteOffset = address - low.PublicationAddress;
        if (byteOffset % sizeof(uint) != 0 ||
            byteOffset / sizeof(uint) + 1 >= low.DwordCount ||
            low.Generation != high.Generation ||
            high.PublicationAddress != low.PublicationAddress ||
            high.DwordCount != low.DwordCount ||
            low.DwordCount < 2 ||
            high.DwordCount < 2 ||
            high.CachePolicy != low.CachePolicy ||
            high.Engine != low.Engine ||
            high.Visibility != low.Visibility)
        {
            value = 0;
            dependency = default;
            return false;
        }

        value |= (ulong)high.Value << 32;
        dependency = dependency.Merge(high.Dependency);
        return true;
    }

    private static bool TryFindSubmittedPublicationLocked(
        in WaitingDcb waiter,
        out ulong value,
        out GuestGpuLabelDependency dependency)
    {
        value = 0;
        dependency = default;
        if (waiter.Memory is null ||
            !_virtualLabelHistory.TryGetValue(
                (waiter.Memory, waiter.WaitAddress),
                out var lowHistory))
        {
            return false;
        }

        var waitCachePolicy = (waiter.ControlValue >> 25) & 0x3u;
        if (waitCachePolicy > 2)
        {
            return false;
        }

        for (var index = lowHistory.Count - 1; index >= 0; index--)
        {
            var low = lowHistory[index];
            if (low.Generation <= waiter.SubmissionPublicationGeneration)
            {
                continue;
            }

            var candidate = (ulong)low.Value;
            var candidateDependency = low.Dependency;
            if (waiter.Is64Bit &&
                (waiter.Mask & 0xFFFF_FFFF_0000_0000UL) != 0)
            {
                if (!_virtualLabelHistory.TryGetValue(
                        (waiter.Memory, waiter.WaitAddress + sizeof(uint)),
                        out var highHistory))
                {
                    continue;
                }

                var foundHigh = false;
                foreach (var high in highHistory)
                {
                    if (high.Generation != low.Generation ||
                        high.PublicationAddress != low.PublicationAddress ||
                        high.DwordCount != low.DwordCount ||
                        high.CachePolicy != low.CachePolicy ||
                        high.Engine != low.Engine ||
                        high.Visibility != low.Visibility)
                    {
                        continue;
                    }

                    candidate |= (ulong)high.Value << 32;
                    candidateDependency = candidateDependency.Merge(high.Dependency);
                    foundHigh = true;
                    break;
                }

                if (!foundHigh)
                {
                    continue;
                }
            }

            if (!Compare(waiter, candidate))
            {
                continue;
            }

            value = candidate;
            dependency = candidateDependency;
            return true;
        }

        return false;
    }

    private static void PruneUnwatchedVirtualLocked()
    {
        List<(object, ulong)>? removable = null;
        foreach (var key in _virtualLabels.Keys)
        {
            var watched = false;
            if (_waiters.TryGetValue(key.Item2, out var list))
            {
                foreach (var waiter in list)
                {
                    if (ReferenceEquals(waiter.Memory, key.Item1))
                    {
                        watched = true;
                        break;
                    }
                }
            }

            if (!watched)
            {
                (removable ??= []).Add(key);
            }
        }

        if (removable is null)
        {
            return;
        }

        foreach (var key in removable)
        {
            _virtualLabels.Remove(key);
        }
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
                        waiter.IsMemSemaphore ||
                        nowTicks - waiter.RegisteredTicks < minAgeTicks ||
                        !_lastProduced.TryGetValue((memory, address), out var produced) ||
                        !Compare(waiter, produced))
                    {
                        continue;
                    }

                    var waitCachePolicy = (waiter.ControlValue >> 25) & 0x3u;
                    if (waitCachePolicy <= 2 &&
                        TryReadVirtualLocked(
                            memory,
                            address,
                            waiter.Is64Bit,
                            out var virtualValue,
                            out var dependency,
                            waitCachePolicy,
                            waiter.Mask) &&
                        Compare(waiter, virtualValue))
                    {
                        waiter.Dependency = waiter.Dependency.Merge(dependency);
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
        if (waiter.RequiresExactEquality)
        {
            return masked == reference;
        }

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
            _currentFrameId = 0;
            _virtualLabels.Clear();
            _virtualLabelHistory.Clear();
            _activeSubmissionGenerations.Clear();
        }
    }
}

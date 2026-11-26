// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.GpuMemory;

// Premark may relay to the GPU. Only the subsequent copy and landed notification reserve pages.
// Admission and validation share this gate; no store, copy or relay executes while it is held.
//
// A managed write inside one 2 MiB region takes no lock while no GPU claim covers the region, as almost every guest
// store does. Its reservation is an entry in the region's slot of a small table, published before it reads the slot's
// claim count; a GPU claim counts itself into the slots of its regions before it reads their entries, so either the
// write sees the claim or the claim sees the write. A write that meets a claim or a locked reservation in its region,
// spans regions or finds its slot full takes the locked path, under the same rules. A claim that began in the region
// while the write premarked is looked up in the history of recent claims, so only a claim over the write's own pages
// invalidates it, as on the locked path.
//
// Most guest stores go to memory that no GPU claim has ever covered, such as command buffers. A claim marks the 64 KiB
// chunks of its range for good. A write inside one region whose chunks are unmarked reserves its pages in its thread's
// own record instead of the shared slot table and then reads the marks again; a mark that appeared while the write
// premarked sends it on to the slot table, where the claim history decides whether it premarks again. The claim that
// marks a chunk first makes every thread either see the mark or show its record (Interlocked.MemoryBarrierProcessWide)
// and waits out every recorded write in the chunk. Writes on unmarked chunks do not exclude each other; each landed
// notification marks its pages again.
internal sealed class GuestWriteReservations
{
    private readonly object _gate = new();
    private readonly List<CpuReservation> _cpu = [];
    private readonly List<Claim> _gpu = [];
    // The gate publishes direct claims. Their owner can retire one without competing for the gate.
    private Claim _directClaim;
    private long _directId;
    private int _sleepers;
    private long _nextClaim;
    private long _nextCpuAttempt;

    private const int RegionShift = 21;
    private const int SlotBits = 10;
    private const int SlotCount = 1 << SlotBits;
    private const int Ways = 4;
    private const int HistorySize = 64;
    // An entry being filled in: its range is not readable yet, so it counts as overlapping everything.
    private const long Filling = -1;
    private readonly Slot[] _slots = new Slot[SlotCount];
    private readonly long[] _entryTokens = new long[SlotCount * Ways];
    private readonly ulong[] _entryStarts = new ulong[SlotCount * Ways];
    private readonly ulong[] _entryEnds = new ulong[SlotCount * Ways];
    private readonly ClaimRecord[] _history = new ClaimRecord[HistorySize];
    private long _claimSequence;

    // One bit per 64 KiB chunk of the guest GPU address space that a GPU claim has covered.
    private const int ChunkShift = 16;
    private readonly long[] _claimedChunks = new long[(int)(TrackerLayout.SpaceBytes >> ChunkShift >> 6)];

    // Each thread's reservation on unmarked chunks, shared by every owner: a thread reserves one write at a time.
    private const int RecordCount = 1024;
    private static readonly ThreadRecord[] s_records = new ThreadRecord[RecordCount];
    private static int s_recordsTaken;

    [ThreadStatic] private static FastAttempt? t_fast;
    [ThreadStatic] private static uint t_tokens;
    [ThreadStatic] private static List<GuestWriteReservations>? t_lockedReserved;

    // Tests may only signal asynchronous continuations from these hooks, which run under the gate.
    internal Action? GpuClaimWaitingForTest;
    internal Action? CpuAttemptWaitingForTest;
    private readonly record struct Claim(long Id, ulong Start, ulong End, int Thread);
    private readonly record struct ClaimRecord(long Sequence, ulong Start, ulong End);

    private record struct CpuReservation(long Id, ulong Start, ulong End, int Thread)
    {
        internal bool Invalidated;
        internal bool Reserved;
    }

    // A region's GPU claims (begun and not released), its locked reservations and the count of claims begun in it.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct Slot
    {
        [FieldOffset(0)] public int Claims;
        [FieldOffset(4)] public int LockedReserved;
        [FieldOffset(8)] public long Epoch;
    }

    // Padded so that two threads' records never share a cache line.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct ThreadRecord
    {
        [FieldOffset(64)] public long Token;
        [FieldOffset(72)] public ulong Start;
        [FieldOffset(80)] public ulong End;
        [FieldOffset(88)] public GuestWriteReservations? Owner;
    }

    // The thread's lock-free attempt. A thread holds at most one; a nested attempt takes the locked path.
    private sealed class FastAttempt
    {
        public GuestWriteReservations? Owner;
        public GuestWriteReservations? LockedNext;
        public long Token;
        public ulong Start;
        public ulong End;
        public int Slot;
        public int Entry = -1;
        // The thread's record, or -1 when every record is taken.
        public readonly int Record = TakeRecord();
        public long Epoch;
        public long Sequence;
        public bool Active;
        public bool Reserved;
        public bool Invalidated;
        public bool InRecord;
    }

    private static int TakeRecord()
    {
        var index = Interlocked.Increment(ref s_recordsTaken) - 1;
        return index < RecordCount ? index : -1;
    }

    // A copied or stale token names only its original admission; a later write never reuses that id. Locked attempts
    // have positive ids, lock-free ones the negated token of their thread.
    internal readonly struct CpuAttempt(GuestWriteReservations owner, long id) : IDisposable
    {
        internal bool Released => owner is null || owner.IsReleased(id);
        internal bool TryReserve() => owner is not null ? owner.TryReserve(id)
            : throw new InvalidOperationException("The CPU write attempt is not available on this thread.");
        public void Dispose() => owner?.Release(id);
    }

    internal readonly struct GpuClaim(GuestWriteReservations owner, long id, bool direct) : IDisposable
    {
        public void Dispose()
        {
            if (direct) owner.ReleaseDirectClaim(id);
            else owner?.ReleaseGpu(id);
        }
    }

    internal CpuAttempt BeginCpuAttempt(ulong address, ulong size)
    {
        var (start, end) = Pages(address, size);
        RejectFastReservedReentry();
        var fast = t_fast;
        if (fast?.LockedNext == this)
        {
            fast.LockedNext = null;
        }
        else if (start >> RegionShift == (end - 1) >> RegionShift && fast is not { Active: true } &&
            t_lockedReserved is not { Count: > 0 })
        {
            fast ??= t_fast = new FastAttempt();
            var slot = SlotOf(start >> RegionShift);
            // Sequence, then the marks, then epoch, then the counts: a claim marks its chunks and counts itself into the
            // slot before it advances the epoch and takes its sequence number.
            var sequence = Volatile.Read(ref _claimSequence);
            var inRecord = fast.Record >= 0 && ChunksUnmarked(start, end);
            // An epoch no slot reaches: a write that leaves its record looks up every claim since the sequence.
            var epoch = inRecord ? -1 : Volatile.Read(ref _slots[slot].Epoch);
            if (inRecord ||
                (Volatile.Read(ref _slots[slot].Claims) == 0 && Volatile.Read(ref _slots[slot].LockedReserved) == 0))
            {
                fast.Owner = this;
                fast.Token = ((long)Environment.CurrentManagedThreadId << 32) | ++t_tokens;
                fast.Start = start;
                fast.End = end;
                fast.Slot = slot;
                fast.Entry = -1;
                fast.Epoch = epoch;
                fast.Sequence = sequence;
                fast.Active = true;
                fast.Reserved = false;
                fast.Invalidated = false;
                fast.InRecord = inRecord;
                return new CpuAttempt(this, -fast.Token);
            }
        }

        return BeginLockedCpuAttempt(start, end);
    }

    private CpuAttempt BeginLockedCpuAttempt(ulong start, ulong end)
    {
        var thread = Environment.CurrentManagedThreadId;
        lock (_gate)
        {
            RejectReservedReentry(thread);
            while (ReservedOverlap(start, end) || GpuOverlap(start, end, thread) || FastEntryOverlaps(start, end))
            {
                CpuAttemptWaitingForTest?.Invoke();
                WaitForRelease(start, end, thread, cpu: true);
            }
            var id = checked(++_nextCpuAttempt);
            _cpu.Add(new CpuReservation(id, start, end, thread));
            return new CpuAttempt(this, id);
        }
    }

    private bool TryReserve(long id)
    {
        if (id < 0)
        {
            return TryReserveFast(-id);
        }

        lock (_gate)
        {
            for (var index = _cpu.Count - 1; index >= 0; index--)
            {
                var attempt = _cpu[index];
                if (attempt.Id != id) continue;
                if (attempt.Reserved || attempt.Thread != Environment.CurrentManagedThreadId)
                    break;
                if (attempt.Invalidated || ReservedOverlap(attempt.Start, attempt.End) ||
                    GpuOverlap(attempt.Start, attempt.End, attempt.Thread))
                    return false;
                // Counted into the slots before the entries are read: a lock-free write that publishes later sees the count.
                AddLockedReserved(attempt.Start, attempt.End, 1);
                if (FastEntryOverlaps(attempt.Start, attempt.End))
                {
                    AddLockedReserved(attempt.Start, attempt.End, -1);
                    return false;
                }
                attempt.Reserved = true;
                _cpu[index] = attempt;
                (t_lockedReserved ??= []).Add(this);
                return true;
            }
            throw new InvalidOperationException("The CPU write attempt is not available on this thread.");
        }
    }

    private bool TryReserveFast(long token)
    {
        var fast = t_fast;
        if (fast is null || !fast.Active || fast.Owner != this || fast.Token != token || fast.Reserved)
            throw new InvalidOperationException("The CPU write attempt is not available on this thread.");
        if (fast.Invalidated)
            return false;

        if (fast.InRecord)
        {
            ref var record = ref s_records[fast.Record];
            record.Owner = this;
            record.Start = fast.Start;
            record.End = fast.End;
            Volatile.Write(ref record.Token, token);
            // No fence: the claim that marks a chunk first makes this store visible before it reads the records.
            if (ChunksUnmarked(fast.Start, fast.End))
            {
                fast.Reserved = true;
                return true;
            }

            // A claim marked a chunk while the write premarked.
            Volatile.Write(ref record.Token, 0);
            fast.InRecord = false;
        }

        var slot = fast.Slot;
        var entry = -1;
        for (var way = 0; way < Ways; way++)
        {
            var index = slot * Ways + way;
            if (Volatile.Read(ref _entryTokens[index]) != 0 ||
                Interlocked.CompareExchange(ref _entryTokens[index], Filling, 0) != 0)
                continue;
            _entryStarts[index] = fast.Start;
            _entryEnds[index] = fast.End;
            // Published with a full fence, so the counts read below are not read before the entry is visible.
            Interlocked.Exchange(ref _entryTokens[index], token);
            entry = index;
            break;
        }

        if (entry < 0)
        {
            fast.LockedNext = this;
            return false;
        }

        if (Volatile.Read(ref _slots[slot].Claims) != 0 || Volatile.Read(ref _slots[slot].LockedReserved) != 0 ||
            OtherEntryOverlaps(slot, entry, fast.Start, fast.End))
        {
            ClearEntry(entry);
            fast.LockedNext = this;
            return false;
        }

        if (Volatile.Read(ref _slots[slot].Epoch) != fast.Epoch && ClaimBeganSince(fast.Sequence, fast.Start, fast.End))
        {
            ClearEntry(entry);
            fast.Invalidated = true;
            return false;
        }

        fast.Entry = entry;
        fast.Reserved = true;
        return true;
    }

    // Enter before tracker/image/backing locks; retain the claim through its publication.
    internal GpuClaim BeginGpuClaim(ulong address, ulong size)
    {
        if (size == 0) return default;
        var (start, end) = Pages(address, size);
        var thread = Environment.CurrentManagedThreadId;
        RejectFastReservedReentry();
        lock (_gate)
        {
            RejectReservedReentry(thread);
            // Marked before the sequence: a write that read the sequence before this claim took its number and then found
            // the chunks unmarked looks the claim up in the history.
            MarkChunks(start, end);
            // Counted into the slots before the entries are read: a lock-free write that publishes later sees the count.
            var walk = new SlotWalk(start, end);
            while (walk.Next(out var slot))
            {
                Interlocked.Increment(ref _slots[slot].Claims);
                Interlocked.Increment(ref _slots[slot].Epoch);
            }
            var sequence = Interlocked.Increment(ref _claimSequence);
            _history[sequence % HistorySize] = new ClaimRecord(sequence, start, end);
            try
            {
                while (ReservedOverlap(start, end) || FastEntryOverlaps(start, end))
                {
                    GpuClaimWaitingForTest?.Invoke();
                    WaitForRelease(start, end, thread, cpu: false);
                }
            }
            catch
            {
                walk = new SlotWalk(start, end);
                while (walk.Next(out var slot))
                {
                    Interlocked.Decrement(ref _slots[slot].Claims);
                }
                throw;
            }
            var id = unchecked(++_nextClaim);
            if (id == 0) id = unchecked(++_nextClaim);
            var claim = new Claim(id, start, end, thread);
            var direct = id > 0 && Volatile.Read(ref _directId) == 0;
            if (direct)
            {
                _directClaim = claim;
                Volatile.Write(ref _directId, id);
            }
            else _gpu.Add(claim);
            for (var index = 0; index < _cpu.Count; index++)
            {
                var attempt = _cpu[index];
                if (!Overlaps(start, end, attempt.Start, attempt.End)) continue;
                attempt.Invalidated = true;
                _cpu[index] = attempt;
            }
            return new GpuClaim(this, id, direct);
        }
    }

    // Waits under the gate for a release. Locked reservations and claims pulse the gate when they are released, and the
    // timeout bounds the wait should a pulse still be missed. A lock-free write holds its entry for one copy and releases it
    // without the gate, so a wait on lock-free entries alone spins outside the gate until they clear.
    private void WaitForRelease(ulong start, ulong end, int thread, bool cpu)
    {
        if (ReservedOverlap(start, end) || (cpu && GpuOverlap(start, end, thread)))
        {
            Interlocked.Increment(ref _sleepers);
            try
            {
                // Pair the waiter publication with direct retirement. Either it will pulse us
                // under the gate or we see that its claim has already gone before waiting.
                if (ReservedOverlap(start, end) || (cpu && GpuOverlap(start, end, thread)))
                    Monitor.Wait(_gate, 10);
            }
            finally { Interlocked.Decrement(ref _sleepers); }
            return;
        }

        Monitor.Exit(_gate);
        try
        {
            var spin = new SpinWait();
            while (FastEntryOverlaps(start, end))
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
        finally
        {
            Monitor.Enter(_gate);
        }
    }

    private void RejectFastReservedReentry()
    {
        if (t_fast is { Active: true, Reserved: true } fast && fast.Owner == this)
            throw new InvalidOperationException("A reserved CPU writer cannot enter another premark or GPU claim.");
    }

    private void RejectReservedReentry(int thread)
    {
        foreach (var attempt in _cpu)
            if (attempt.Reserved && attempt.Thread == thread)
                throw new InvalidOperationException("A reserved CPU writer cannot enter another premark or GPU claim.");
    }

    private bool ReservedOverlap(ulong start, ulong end)
    {
        foreach (var attempt in _cpu)
            if (attempt.Reserved && Overlaps(start, end, attempt.Start, attempt.End)) return true;
        return false;
    }

    // A GPU owner may perform a managed store inside its own claim, but not another thread's claim.
    private bool GpuOverlap(ulong start, ulong end, int exceptThread)
    {
        if (Volatile.Read(ref _directId) > 0 && _directClaim.Thread != exceptThread &&
            Overlaps(start, end, _directClaim.Start, _directClaim.End)) return true;
        foreach (var claim in _gpu)
            if (claim.Thread != exceptThread && Overlaps(start, end, claim.Start, claim.End)) return true;
        return false;
    }

    // A lock-free reservation over the range. An entry replaced while it is read may give a mixed range; its new writer
    // publishes after the caller's count and so finds that count itself.
    private bool FastEntryOverlaps(ulong start, ulong end)
    {
        var walk = new SlotWalk(start, end);
        while (walk.Next(out var slot))
        {
            if (OtherEntryOverlaps(slot, -1, start, end)) return true;
        }
        return false;
    }

    private bool OtherEntryOverlaps(int slot, int exceptEntry, ulong start, ulong end)
    {
        for (var way = 0; way < Ways; way++)
        {
            var index = slot * Ways + way;
            if (index == exceptEntry) continue;
            var token = Volatile.Read(ref _entryTokens[index]);
            if (token == 0) continue;
            if (token == Filling) return true;
            if (Overlaps(start, end, Volatile.Read(ref _entryStarts[index]), Volatile.Read(ref _entryEnds[index]))) return true;
        }
        return false;
    }

    // Marks the chunks of a claim's range. The claim that marks a chunk first waits until every thread either sees the
    // mark or shows its record, and then until every recorded write in its chunks has released, over its own pages or
    // not: a later claim over another page of a marked chunk reads no records. No write that begins afterwards reserves a
    // marked chunk in its record.
    private void MarkChunks(ulong start, ulong end)
    {
        var first = start >> ChunkShift;
        var last = (end - 1) >> ChunkShift;
        var marked = false;
        for (var word = first >> 6; word <= last >> 6; word++)
        {
            var mask = ChunkMask(word, first, last);
            if ((Volatile.Read(ref _claimedChunks[word]) & mask) == mask) continue;
            Interlocked.Or(ref _claimedChunks[word], mask);
            marked = true;
        }

        if (!marked) return;
        Interlocked.MemoryBarrierProcessWide();
        var spin = new SpinWait();
        while (RecordOverlaps(first << ChunkShift, (last + 1) << ChunkShift))
        {
            GpuClaimWaitingForTest?.Invoke();
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    private bool ChunksUnmarked(ulong start, ulong end)
    {
        var first = start >> ChunkShift;
        var last = (end - 1) >> ChunkShift;
        for (var word = first >> 6; word <= last >> 6; word++)
        {
            if ((Volatile.Read(ref _claimedChunks[word]) & ChunkMask(word, first, last)) != 0) return false;
        }
        return true;
    }

    private static long ChunkMask(ulong word, ulong first, ulong last)
    {
        var low = word == first >> 6 ? (int)(first & 63) : 0;
        var high = word == last >> 6 ? (int)(last & 63) : 63;
        var bits = high - low + 1;
        return bits == 64 ? -1L : (long)(((1UL << bits) - 1) << low);
    }

    // A recorded write of this owner over the range. A record whose token changes while it is read is skipped: the write
    // it named has released, and a write that shows its record only now reads the marks afterwards.
    private bool RecordOverlaps(ulong start, ulong end)
    {
        var count = Math.Min(Volatile.Read(ref s_recordsTaken), RecordCount);
        for (var index = 0; index < count; index++)
        {
            ref var record = ref s_records[index];
            var token = Volatile.Read(ref record.Token);
            if (token == 0 || record.Owner != this ||
                !Overlaps(start, end, Volatile.Read(ref record.Start), Volatile.Read(ref record.End))) continue;
            if (Volatile.Read(ref record.Token) == token) return true;
        }
        return false;
    }

    // Whether a claim over the range began after the attempt read the sequence. When the history does not reach back to
    // the attempt, the answer is yes.
    private bool ClaimBeganSince(long sequence, ulong start, ulong end)
    {
        lock (_gate)
        {
            var current = _claimSequence;
            if (current - sequence > HistorySize) return true;
            for (var next = sequence + 1; next <= current; next++)
            {
                var record = _history[next % HistorySize];
                if (record.Sequence != next || Overlaps(start, end, record.Start, record.End)) return true;
            }
            return false;
        }
    }

    private void AddLockedReserved(ulong start, ulong end, int delta)
    {
        var walk = new SlotWalk(start, end);
        while (walk.Next(out var slot))
        {
            Interlocked.Add(ref _slots[slot].LockedReserved, delta);
        }
    }

    // Cleared after the copy's stores; a waiter on lock-free entries spins until it reads the entry free.
    private void ClearEntry(int entry) => Volatile.Write(ref _entryTokens[entry], 0);

    private bool IsReleased(long id)
    {
        if (id < 0)
        {
            var fast = t_fast;
            return fast is null || !fast.Active || fast.Owner != this || fast.Token != -id;
        }

        lock (_gate)
        {
            foreach (var attempt in _cpu)
                if (attempt.Id == id) return false;
            return true;
        }
    }

    private void Release(long id)
    {
        if (id < 0)
        {
            var fast = t_fast;
            if (fast is not { Active: true } || fast.Owner != this || fast.Token != -id) return;
            fast.Active = false;
            if (fast.Reserved)
            {
                fast.Reserved = false;
                if (fast.InRecord) Volatile.Write(ref s_records[fast.Record].Token, 0);
                else ClearEntry(fast.Entry);
            }
            fast.Entry = -1;
            fast.InRecord = false;
            return;
        }

        lock (_gate)
        {
            for (var index = _cpu.Count - 1; index >= 0; index--)
            {
                var attempt = _cpu[index];
                if (attempt.Id != id) continue;
                _cpu.RemoveAt(index);
                if (attempt.Reserved)
                {
                    AddLockedReserved(attempt.Start, attempt.End, -1);
                    t_lockedReserved?.Remove(this);
                }
                Monitor.PulseAll(_gate);
                return;
            }
        }
    }

    private void ReleaseDirectClaim(long id)
    {
        // The negative id holds the range stable until its slot counts are released.
        if (Interlocked.CompareExchange(ref _directId, -id, id) != id) return;
        var walk = new SlotWalk(_directClaim.Start, _directClaim.End);
        while (walk.Next(out var slot)) Interlocked.Decrement(ref _slots[slot].Claims);
        Interlocked.Exchange(ref _directId, 0);
        if (Volatile.Read(ref _sleepers) != 0)
            lock (_gate) Monitor.PulseAll(_gate);
    }

    private void ReleaseGpu(long id)
    {
        lock (_gate)
        {
            for (var index = _gpu.Count - 1; index >= 0; index--)
            {
                var claim = _gpu[index];
                if (claim.Id != id) continue;
                _gpu.RemoveAt(index);
                var walk = new SlotWalk(claim.Start, claim.End);
                while (walk.Next(out var slot))
                {
                    Interlocked.Decrement(ref _slots[slot].Claims);
                }
                Monitor.PulseAll(_gate);
                return;
            }
        }
    }

    private static int SlotOf(ulong region) => (int)((region * 0x9E3779B97F4A7C15UL) >> (64 - SlotBits));

    // The slots of a page range's regions, one per region (two regions may share a slot); a range over more regions than
    // there are slots visits every slot once.
    private struct SlotWalk
    {
        private ulong _region;
        private readonly ulong _last;
        private readonly bool _every;
        private int _index;

        public SlotWalk(ulong start, ulong end)
        {
            _region = start >> RegionShift;
            _last = (end - 1) >> RegionShift;
            _every = _last - _region >= SlotCount;
            _index = 0;
        }

        public bool Next(out int slot)
        {
            if (_every)
            {
                slot = _index;
                return _index++ < SlotCount;
            }

            if (_region > _last)
            {
                slot = 0;
                return false;
            }

            slot = SlotOf(_region++);
            return true;
        }
    }

    private static bool Overlaps(ulong aStart, ulong aEnd, ulong bStart, ulong bEnd) =>
        aStart < bEnd && bStart < aEnd;

    private static (ulong Start, ulong End) Pages(ulong address, ulong size)
    {
        if (address == 0 || size == 0 || address >= TrackerLayout.SpaceBytes ||
            size > TrackerLayout.SpaceBytes - address)
            throw new ArgumentOutOfRangeException(nameof(address), "The reservation must fit the guest GPU address space.");
        return (address & ~(TrackerLayout.PageBytes - 1),
            (address + size + TrackerLayout.PageBytes - 1) & ~(TrackerLayout.PageBytes - 1));
    }
}

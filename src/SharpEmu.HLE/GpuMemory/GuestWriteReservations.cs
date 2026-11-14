// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// Premark may relay to the GPU. Only the subsequent copy and landed notification reserve pages.
// Admission and validation share this gate; no store, copy or relay executes while it is held.
internal sealed class GuestWriteReservations
{
    private readonly object _gate = new();
    private readonly List<CpuReservation> _cpu = [];
    private readonly List<Claim> _gpu = [];
    private long _nextClaim;
    private long _nextCpuAttempt;

    // Tests may only signal asynchronous continuations from these hooks, which run under the gate.
    internal Action? GpuClaimWaitingForTest;
    internal Action? CpuAttemptWaitingForTest;
    private readonly record struct Claim(long Id, ulong Start, ulong End, int Thread);

    private record struct CpuReservation(long Id, ulong Start, ulong End, int Thread)
    {
        internal bool Invalidated;
        internal bool Reserved;
    }

    // A copied or stale token names only its original admission; a later write never reuses that id.
    internal readonly struct CpuAttempt(GuestWriteReservations owner, long id) : IDisposable
    {
        internal bool Released => owner is null || owner.IsReleased(id);
        internal bool TryReserve() => owner is not null ? owner.TryReserve(id)
            : throw new InvalidOperationException("The CPU write attempt is not available on this thread.");
        public void Dispose() => owner?.Release(id);
    }

    internal readonly struct GpuClaim(GuestWriteReservations owner, long id) : IDisposable
    {
        public void Dispose() => owner?.ReleaseGpu(id);
    }

    internal CpuAttempt BeginCpuAttempt(ulong address, ulong size)
    {
        var (start, end) = Pages(address, size);
        var thread = Environment.CurrentManagedThreadId;
        lock (_gate)
        {
            RejectReservedReentry(thread);
            while (ReservedOverlap(start, end) || GpuOverlap(start, end, thread))
            {
                CpuAttemptWaitingForTest?.Invoke();
                Monitor.Wait(_gate);
            }
            var id = checked(++_nextCpuAttempt);
            _cpu.Add(new CpuReservation(id, start, end, thread));
            return new CpuAttempt(this, id);
        }
    }

    private bool TryReserve(long id)
    {
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
                attempt.Reserved = true;
                _cpu[index] = attempt;
                return true;
            }
            throw new InvalidOperationException("The CPU write attempt is not available on this thread.");
        }
    }

    // Enter before tracker/image/backing locks; retain the claim through its publication.
    internal GpuClaim BeginGpuClaim(ulong address, ulong size)
    {
        if (size == 0) return default;
        var (start, end) = Pages(address, size);
        var thread = Environment.CurrentManagedThreadId;
        lock (_gate)
        {
            RejectReservedReentry(thread);
            while (ReservedOverlap(start, end))
            {
                GpuClaimWaitingForTest?.Invoke();
                Monitor.Wait(_gate);
            }
            var id = unchecked(++_nextClaim);
            if (id == 0) id = unchecked(++_nextClaim);
            _gpu.Add(new Claim(id, start, end, thread));
            for (var index = 0; index < _cpu.Count; index++)
            {
                var attempt = _cpu[index];
                if (!Overlaps(start, end, attempt.Start, attempt.End)) continue;
                attempt.Invalidated = true;
                _cpu[index] = attempt;
            }
            return new GpuClaim(this, id);
        }
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
        foreach (var claim in _gpu)
            if (claim.Thread != exceptThread && Overlaps(start, end, claim.Start, claim.End)) return true;
        return false;
    }

    private bool IsReleased(long id)
    {
        lock (_gate)
        {
            foreach (var attempt in _cpu)
                if (attempt.Id == id) return false;
            return true;
        }
    }

    private void Release(long id)
    {
        lock (_gate)
        {
            for (var index = _cpu.Count - 1; index >= 0; index--)
            {
                if (_cpu[index].Id != id) continue;
                _cpu.RemoveAt(index);
                Monitor.PulseAll(_gate);
                return;
            }
        }
    }

    private void ReleaseGpu(long id)
    {
        lock (_gate)
        {
            for (var index = _gpu.Count - 1; index >= 0; index--)
            {
                if (_gpu[index].Id != id) continue;
                _gpu.RemoveAt(index);
                Monitor.PulseAll(_gate);
                return;
            }
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

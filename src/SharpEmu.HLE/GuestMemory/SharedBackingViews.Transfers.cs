// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GuestMemory;

public sealed unsafe partial class SharedBackingViews
{
    // Reserve physical ranges so aliases cannot bypass overlap checks.
    private readonly record struct CopyReservation(ulong Source, ulong Destination, ulong Size)
    {
        public bool Overlaps(ulong offset, ulong size) => Size != 0 && size != 0 &&
            ((offset < Source + Size && Source < offset + size) ||
             (offset < Destination + Size && Destination < offset + size));
    }

    internal bool TryReserveCopy(ulong destination, ulong source, ulong size, out CopyLease lease)
    {
        lease = default;
        if (size == 0 || size > int.MaxValue) return false;
        lock (_lock)
            while (true)
            {
                if (_mappingWaiters != 0)
                {
                    WaitForCopyCompletion();
                    continue;
                }

                if (!IsAvailable || !TryFindRecord(source, size, out var sourceRecord) ||
                    !TryFindRecord(destination, size, out var destinationRecord)) return false;
                var sourceOffset = sourceRecord.Offset + source - sourceRecord.Address;
                var destinationOffset = destinationRecord.Offset + destination - destinationRecord.Address;
                if (!IsWithinBacking(sourceOffset, size) || !IsWithinBacking(destinationOffset, size)) return false;
                if (WaitForCopyConflict(sourceOffset, size, destinationOffset, size)) continue;

                _copyReservations ??= new CopyReservation[4];
                var index = 0;
                while (index < _copyReservations.Length && _copyReservations[index].Size != 0) index++;
                if (index == _copyReservations.Length) Array.Resize(ref _copyReservations, checked(index * 2));
                _copyReservations[index] = new CopyReservation(sourceOffset, destinationOffset, size);
                _activeCopies++;
                lease = new CopyLease(this, index, AliasBase + sourceOffset, AliasBase + destinationOffset);
                return true;
            }
    }

    private bool WaitForBufferCopyConflict(ulong backingOffset, ReadOnlySpan<byte> buffer)
    {
        fixed (byte* bytes = buffer)
        {
            var address = (ulong)bytes;
            var size = (ulong)buffer.Length;
            if (address >= AliasBase && IsWithinBacking(address - AliasBase, size))
            {
                return WaitForCopyConflict(backingOffset, size, address - AliasBase, size);
            }

            if (WaitForCopyConflict(backingOffset, size)) return true;

            // A caller buffer can span views with different backing offsets.
            var end = address + size;
            var index = Math.Max(0, RangeSearch.FindLastIndexAtOrBelow(_views, address));
            for (; index < _views.Count; index++)
            {
                var record = _views.Values[index];
                if (record.Address >= end) break;
                var start = Math.Max(address, record.Address);
                var partEnd = Math.Min(end, record.Address + record.Size);
                if (start >= partEnd) continue;
                if (WaitForCopyConflict(record.Offset + start - record.Address, partEnd - start)) return true;
            }

            return false;
        }
    }

    // A wait releases the metadata lock. Callers must resolve mappings again afterward.
    private bool WaitForCopyConflict(ulong firstOffset, ulong firstSize, ulong secondOffset = 0, ulong secondSize = 0)
    {
        if (_activeCopies == 0) return false;
        foreach (var reservation in _copyReservations!)
        {
            if (reservation.Overlaps(firstOffset, firstSize) || reservation.Overlaps(secondOffset, secondSize))
            {
                WaitForCopyCompletion();
                return true;
            }
        }
        return false;
    }

    private bool WaitForAnyCopy()
    {
        if (_activeCopies == 0) return false;
        WaitForCopyCompletion();
        return true;
    }

    private void WaitForCopies()
    {
        if (_activeCopies == 0) return;
        _mappingWaiters++;
        try
        {
            while (_activeCopies != 0) WaitForCopyCompletion();
        }
        finally
        {
            _mappingWaiters--;
            if (_copyWaiters != 0) Monitor.PulseAll(_lock);
        }
    }

    private void WaitForCopyCompletion()
    {
        _copyWaiters++;
        try { Monitor.Wait(_lock); }
        finally { _copyWaiters--; }
    }

    private void ReleaseCopy(int index)
    {
        lock (_lock)
        {
            _copyReservations![index] = default;
            _activeCopies--;
            if (_copyWaiters != 0) Monitor.PulseAll(_lock);
        }
    }

    internal readonly ref struct CopyLease(SharedBackingViews owner, int index, ulong source, ulong destination)
    {
        public ulong Source { get; } = source;
        public ulong Destination { get; } = destination;

        public void Dispose() => owner.ReleaseCopy(index);
    }
}

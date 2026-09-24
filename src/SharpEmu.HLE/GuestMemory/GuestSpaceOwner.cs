// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;

namespace SharpEmu.HLE.GuestMemory;

public enum RangeKind
{
    Backed,
    Private,
}

public readonly record struct OwnedRange(ulong Address, ulong Size, RangeKind Kind);

public sealed class GuestSpaceOwner : IDisposable
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    public const ulong GuestPage = 0x4000;
    private const ulong UserAddressStart = 0x10_0000_0000;
    private const ulong UserAddressEnd = 0xFC_0000_0000;
    private const ulong MinimumPreReservedRange = 0x0100_0000;

    private readonly IHostViewMemory _host;
    private readonly SharedBackingViews _views;
    private readonly object _mappingLock = new();
    private readonly object[] _protectionLocks = CreateProtectionLocks();
    private readonly SortedList<ulong, ulong> _free = new();
    private readonly SortedList<ulong, OwnedRange> _mapped = new();
    private readonly List<(ulong Address, ulong Size)> _owned = new();
    private readonly bool _preReserveGuestAddressSpace;
    private bool _disposed;

    public GuestSpaceOwner(IHostViewMemory host, ulong backingSize, bool preReserveGuestAddressSpace = false)
    {
        _host = host;
        Granularity = host.Granularity;
        // Create lookup views before concurrent fault handlers can read the range table.
        _ = _mapped.Keys;
        _ = _mapped.Values;
        _preReserveGuestAddressSpace = preReserveGuestAddressSpace;
        PreReserveGuestAddressSpace();
        _views = new SharedBackingViews(host, backingSize);
        if (!_views.IsAvailable)
        {
            var megabytes = backingSize / (1024 * 1024);
            OnFatal(OperatingSystem.IsWindows()
                ? $"Could not allocate {megabytes} MB for guest direct memory. Windows requires this amount of available system commit. Close other applications or increase the paging file size."
                : $"Could not allocate {megabytes} MB for guest direct memory.");
        }
    }

    public ulong Granularity { get; }

    public ulong AliasBase => _views.AliasBase;

    public ulong BackingSize => _views.Size;

    public bool TryClearBacking(ulong offset, ulong size) => _views.Clear(offset, size);

    public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => _views.TryWriteBacking(address, data);

    public bool TryReadBacking(ulong address, Span<byte> data) => _views.TryReadBacking(address, data);

    public bool TryCopyBacking(ulong destination, ulong source, ulong size) => _views.TryCopyBacking(destination, source, size);

    public bool IsBacked(ulong address, ulong size) => _views.Contains(address, size);

    public bool IsRestoredView(ulong address) => _views.IsRestoredView(address);

    public bool TryReserveAddressRange(ulong address, ulong size)
    {
        if (!IsValidRange(address, size) || address % Granularity != 0 || size % Granularity != 0)
        {
            return false;
        }

        using (EnterMappingLock())
        {
            if (_disposed || OverlapsOwnedLocked(address, size) || _host.ReserveHole(address, size) != address)
            {
                return false;
            }

            AddFreeRange(address, size);
            _owned.Add((address, size));
            return true;
        }
    }

    public bool TryReserveFreeRange(ulong address, ulong size)
    {
        using var reservationScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.Reservation);
        if (!IsValidAlignedRange(address, size))
            return false;
        var end = address + size;
        var reservationEnd = AlignUp(end, Granularity);
        if (reservationEnd == 0)
            return false;

        using (EnterMappingLock())
        {
            List<(ulong Address, ulong Size)> additions;
            using (GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.ReservationSearch))
            {
                if (_disposed)
                    return false;
                if (FindFreeRangeIndex(address, size) >= 0)
                    return true;
                if (_mapped.Values.Any(range => range.Address < end && address < range.Address + range.Size))
                    return false;

                // Keep owned placeholders. Reserve only the gaps between them.
                additions = new List<(ulong Address, ulong Size)>();
                var current = address - address % Granularity;
                foreach (var range in _owned.OrderBy(range => range.Address))
                {
                    if (range.Address + range.Size <= current)
                        continue;
                    if (range.Address >= reservationEnd)
                        break;
                    if (current < range.Address)
                        additions.Add((current, range.Address - current));
                    current = Math.Max(current, Math.Min(reservationEnd, range.Address + range.Size));
                }
                if (current < reservationEnd)
                    additions.Add((current, reservationEnd - current));
            }

            using (GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.ReservationHost))
            {
                for (var index = 0; index < additions.Count; index++)
                {
                    var range = additions[index];
                    if (_host.ReserveHole(range.Address, range.Size) == range.Address)
                        continue;
                    // No new range has joined an existing placeholder yet.
                    for (var previous = index - 1; previous >= 0; previous--)
                    {
                        var rollback = additions[previous];
                        if (!_host.FreeHole(rollback.Address, rollback.Size))
                            OnFatal($"Cannot release the new reservation at 0x{rollback.Address:X16}.");
                    }
                    return false;
                }
            }

            using (GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.ReservationPublish))
            {
                foreach (var range in additions)
                {
                    _owned.Add(range);
                    AddFreeRange(range.Address, range.Size);
                }
                return FindFreeRangeIndex(address, size) >= 0;
            }
        }
    }

    public bool OwnsReservedRange(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            return false;
        }

        lock (_mappingLock)
        {
            return _owned.Any(range => address >= range.Address && address + size <= range.Address + range.Size);
        }
    }

    public bool ContainsFreeRange(ulong address, ulong size)
    {
        if (!IsValidAlignedRange(address, size))
        {
            return false;
        }

        lock (_mappingLock)
        {
            return FindFreeRangeIndex(address, size) >= 0;
        }
    }

    public ulong FindFreeAddress(ulong searchStart, ulong searchEnd, ulong size, ulong alignment)
    {
        if (size == 0 || alignment == 0)
        {
            return 0;
        }

        alignment = Math.Max(alignment, GuestPage);
        lock (_mappingLock)
        {
            return FindAlignedFreeAddress(searchStart, searchEnd, size, alignment);
        }
    }

    public bool SetAccess(ulong address, ulong size, HostPageProtection protection)
    {
        if (!IsValidAlignedRange(address, size))
        {
            return false;
        }

        using (EnterMappingLock())
        {
            return SetAccessLocked(address, size, protection);
        }
    }

    // Page watchers protect host pages, which are smaller than the guest page.
    public bool SetTransientAccess(ulong address, ulong size, HostPageProtection protection)
    {
        if (!IsValidRange(address, size) || address % _host.PageSize != 0 || size % _host.PageSize != 0)
        {
            return false;
        }

        using var protectionScope = new ProtectionRangeScope(_protectionLocks, address, size);
        return SetAccessLocked(address, size, protection);
    }

    private MappingLockScope EnterMappingLock() => new(_mappingLock, _protectionLocks);

    // Mapping changes own every address lock before they inspect or change the range tables.
    // Transient protection needs only its address locks and does not allocate reader state.
    private ref struct MappingLockScope
    {
        private readonly object _mappingLock;
        private ProtectionRangeScope _protectionScope;

        public MappingLockScope(object mappingLock, object[] protectionLocks)
        {
            _mappingLock = mappingLock;
            var taken = false;
            try
            {
                Monitor.Enter(mappingLock, ref taken);
                _protectionScope = new ProtectionRangeScope(protectionLocks, 0,
                    (ulong)protectionLocks.Length << ProtectionBlockShift, profileWait: false);
            }
            catch
            {
                if (taken) Monitor.Exit(mappingLock);
                throw;
            }
        }

        public void Dispose()
        {
            _protectionScope.Dispose();
            Monitor.Exit(_mappingLock);
        }
    }

    private const int ProtectionLockCount = 256;
    private const int ProtectionBlockShift = 22;

    private static object[] CreateProtectionLocks()
    {
        var protectionLocks = new object[ProtectionLockCount];
        for (var index = 0; index < protectionLocks.Length; index++) protectionLocks[index] = new object();
        return protectionLocks;
    }

    // Address locks keep views alive and order overlapping protection changes.
    // Lock collisions only serialize extra ranges; every caller acquires locks in index order.
    private ref struct ProtectionRangeScope
    {
        private readonly object[] _protectionLocks;
        private readonly int _firstLockIndex;
        private readonly int _lockCount;
        private int _acquiredLockCount;

        public ProtectionRangeScope(object[] protectionLocks, ulong address, ulong size, bool profileWait = true)
        {
            _protectionLocks = protectionLocks;
            var firstBlock = address >> ProtectionBlockShift;
            var lastBlock = (address + size - 1) >> ProtectionBlockShift;
            _lockCount = (int)Math.Min((ulong)protectionLocks.Length, lastBlock - firstBlock + 1);
            _firstLockIndex = _lockCount == protectionLocks.Length ? 0 : (int)(firstBlock % (ulong)protectionLocks.Length);
            _acquiredLockCount = 0;
            using var profile = profileWait ? GpuMemoryAccessProfile.MeasureBackingProtectionWait() : default;
            try
            {
                while (_acquiredLockCount < _lockCount)
                {
                    var taken = false;
                    try { Monitor.Enter(protectionLocks[GetLockIndex(_acquiredLockCount)], ref taken); }
                    finally { if (taken) _acquiredLockCount++; }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private readonly int GetLockIndex(int acquisitionIndex)
        {
            var wrappedCount = Math.Max(0, _firstLockIndex + _lockCount - _protectionLocks.Length);
            return acquisitionIndex < wrappedCount ? acquisitionIndex : _firstLockIndex + acquisitionIndex - wrappedCount;
        }

        public void Dispose()
        {
            while (_acquiredLockCount > 0) Monitor.Exit(_protectionLocks[GetLockIndex(--_acquiredLockCount)]);
        }
    }

    public bool MapShared(ulong address, ulong size, ulong offset, HostPageProtection protection, out HostViewFailure failure)
    {
        using var mappingScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BackingMap);
        failure = HostViewFailure.AddressUnavailable;
        if (!IsValidAlignedRange(address, size))
        {
            return false;
        }

        if (offset % GuestPage != 0 || offset >= BackingSize || size > BackingSize - offset)
        {
            failure = HostViewFailure.OffsetOutOfBounds;
            return false;
        }

        using (EnterMappingLock())
        {
            if (!TryTakeFreeRange(address, size))
            {
                failure = HostViewFailure.AddressUnavailable;
                return false;
            }

            if (_views.TryMapReservedRange(address, size, offset, GetBackingProtection(protection), out failure))
            {
                if (!TryAddMappedRange(address, size, RangeKind.Backed))
                {
                    OnFatal($"Cannot map shared backing. A mapped range overlaps 0x{address:X16}.");
                }

                return true;
            }

            AddFreeRange(address, size);
            return false;
        }
    }

    public bool UnmapShared(ulong address, ulong size)
    {
        using var mappingScope = GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.BackingUnmap);
        if (!IsValidAlignedRange(address, size))
        {
            return false;
        }

        using (EnterMappingLock())
        {
            if (!HasOnlyRangeKind(address, size, RangeKind.Backed))
            {
                return false;
            }

            if (!_views.Unmap(address, size, out var holePreserved) || !holePreserved)
            {
                return false;
            }

            if (!TryRemoveMappedRanges(address, size, RangeKind.Backed))
            {
                OnFatal($"No shared backing record exists at 0x{address:X16}.");
            }

            AddFreeRange(address, size);
            return true;
        }
    }

    public bool AllocatePrivate(ulong address, ulong size, HostPageProtection protection)
    {
        if (!IsValidAlignedRange(address, size))
        {
            return false;
        }

        using (EnterMappingLock())
        {
            if (!TryTakeFreeRange(address, size))
            {
                return false;
            }

            if (_host.CommitPrivate(address, size, GetBackingProtection(protection)))
            {
                if (!TryAddMappedRange(address, size, RangeKind.Private))
                {
                    OnFatal($"Cannot map private memory. A mapped range overlaps 0x{address:X16}.");
                }

                return true;
            }

            AddFreeRange(address, size);
            return false;
        }
    }

    public bool FreePrivate(ulong address, ulong size)
    {
        if (!IsValidAlignedRange(address, size))
        {
            return false;
        }

        using (EnterMappingLock())
        {
            if (!HasOnlyRangeKind(address, size, RangeKind.Private))
            {
                return false;
            }

            var end = address + size;
            var current = address;
            var pieces = new List<(ulong Address, ulong Size)>();
            while (current < end)
            {
                var range = _mapped.Values[FindMappedRangeIndex(current)];
                var pieceEnd = Math.Min(end, range.Address + range.Size);
                pieces.Add((current, pieceEnd - current));
                current = pieceEnd;
            }

            foreach (var piece in pieces)
            {
                if (!_host.ReleasePrivate(piece.Address, piece.Size))
                {
                    OnFatal($"Could not release private memory at 0x{piece.Address:X16}.");
                }
            }

            if (pieces.Count > 1 && !_host.JoinHoles(address, size))
            {
                OnFatal($"Could not join the free ranges after releasing private memory at 0x{address:X16}.");
            }

            if (!TryRemoveMappedRanges(address, size, RangeKind.Private))
            {
                OnFatal($"No private memory record exists at 0x{address:X16}.");
            }

            AddFreeRange(address, size);
            return true;
        }
    }

    // Release all mappings and reserved ranges, but keep the backing object for reuse.
    public void ReleaseAddressRanges()
    {
        using (EnterMappingLock())
        {
            if (_disposed)
            {
                return;
            }

            foreach (var range in _mapped.Values.ToArray())
            {
                var released = range.Kind == RangeKind.Backed
                    ? _views.Unmap(range.Address, range.Size, out _)
                    : _host.ReleasePrivate(range.Address, range.Size);
                if (!released)
                {
                    OnFatal($"Could not release the reserved range at 0x{range.Address:X16}.");
                }
            }

            foreach (var (address, size) in _owned)
            {
                if (!_host.FreeOwnedRange(address, size))
                {
                    OnFatal($"Could not release the owned range at 0x{address:X16}.");
                }
            }

            _owned.Clear();
            _free.Clear();
            _mapped.Clear();
            // A new image load needs the guest address space reserved again.
            PreReserveGuestAddressSpace();
        }
    }

    private void PreReserveGuestAddressSpace()
    {
        if (!_preReserveGuestAddressSpace)
        {
            return;
        }

        foreach (var range in _host.ReserveFreeAddressRanges(UserAddressStart, UserAddressEnd, MinimumPreReservedRange))
        {
            _owned.Add((range.Address, range.Size));
            AddFreeRange(range.Address, range.Size);
        }
    }

    public void Dispose()
    {
        using (EnterMappingLock())
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _views.Dispose();
            foreach (var (address, size) in _owned)
            {
                if (!_host.FreeOwnedRange(address, size))
                {
                    OnFatal($"Could not release the owned range at 0x{address:X16}.");
                }
            }

            _owned.Clear();
            _free.Clear();
            _mapped.Clear();
        }
    }

    private static bool IsValidRange(ulong address, ulong size) =>
        address != 0 && size != 0 && size <= ulong.MaxValue - address;

    private static bool IsValidAlignedRange(ulong address, ulong size) =>
        IsValidRange(address, size) && address % GuestPage == 0 && size % GuestPage == 0;

    private static HostPageProtection GetBackingProtection(HostPageProtection protection) =>
        protection is HostPageProtection.Execute or HostPageProtection.ReadExecute
            or HostPageProtection.ReadWriteExecute or HostPageProtection.ExecuteWriteCopy
            ? HostPageProtection.ReadWriteExecute
            : HostPageProtection.ReadWrite;

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        var remainder = value % alignment;
        var increment = remainder != 0 ? alignment - remainder : 0;
        return increment <= ulong.MaxValue - value ? value + increment : 0;
    }

    private bool OverlapsOwnedLocked(ulong address, ulong size) =>
        _owned.Any(range => address < range.Address + range.Size && range.Address < address + size);

    private bool SetAccessLocked(ulong address, ulong size, HostPageProtection protection)
    {
        if (_disposed) return false;
        var end = address + size;
        var index = Math.Max(0, RangeSearch.FindLastIndexAtOrBelow(_mapped, address));
        for (; index < _mapped.Count && _mapped.Values[index].Address < end; index++)
        {
            var range = _mapped.Values[index];
            var start = Math.Max(address, range.Address);
            var stop = Math.Min(end, range.Address + range.Size);
            if (start < stop)
            {
                using var profile = GpuMemoryAccessProfile.MeasureHostProtectionCall(stop - start);
                if (!_host.ChangeAccess(start, stop - start, protection)) return false;
            }
        }

        return true;
    }

    private bool TryTakeFreeRange(ulong address, ulong size)
    {
        var index = FindFreeRangeIndex(address, size);
        if (index < 0 || !TrySplitFreeRange(index, address, size))
        {
            return false;
        }

        if (!_free.TryGetValue(address, out var found) || found != size)
        {
            return false;
        }

        _free.Remove(address);
        return true;
    }

    private bool TrySplitFreeRange(int index, ulong address, ulong size)
    {
        var start = _free.Keys[index];
        var end = start + _free.Values[index];
        if (start == address && end == address + size)
        {
            return true;
        }

        if (start != address)
        {
            var leftSize = address - start;
            if (!_host.SplitHole(start, leftSize))
            {
                return false;
            }

            _free[start] = leftSize;
            _free[address] = end - address;
        }

        if (_free[address] != size)
        {
            if (!_host.SplitHole(address, size))
            {
                return false;
            }

            var rightStart = address + size;
            var rightSize = _free[address] - size;
            _free[address] = size;
            _free[rightStart] = rightSize;
        }

        return true;
    }

    private void AddFreeRange(ulong address, ulong size)
    {
        if (address == 0 || size == 0)
        {
            return;
        }

        _free[address] = size;
        var index = _free.IndexOfKey(address);
        var start = address;
        var end = address + size;
        var first = index;
        while (first > 0 && _free.Keys[first - 1] + _free.Values[first - 1] == start)
        {
            first--;
            start = _free.Keys[first];
        }

        var last = index;
        while (last + 1 < _free.Count && _free.Keys[last + 1] == end)
        {
            last++;
            end = _free.Keys[last] + _free.Values[last];
        }

        // Keep adjacent entries separate when the host cannot join their free ranges.
        if ((first != index || last != index) && _host.JoinHoles(start, end - start))
        {
            for (var remove = last; remove >= first; remove--)
            {
                _free.RemoveAt(remove);
            }

            _free[start] = end - start;
        }
    }

    private int FindFreeRangeIndex(ulong address, ulong size)
    {
        var index = RangeSearch.FindLastIndexAtOrBelow(_free, address);
        if (index < 0)
        {
            return -1;
        }

        var start = _free.Keys[index];
        var end = start + _free.Values[index];
        return address >= start && address + size <= end ? index : -1;
    }

    private ulong FindAlignedFreeAddress(ulong searchStart, ulong searchEnd, ulong size, ulong alignment)
    {
        if (searchStart >= searchEnd || size > searchEnd - searchStart)
        {
            return 0;
        }

        foreach (var (start, length) in _free)
        {
            var candidate = AlignUp(Math.Max(start, searchStart), alignment);
            if (candidate != 0 && candidate < searchEnd && size <= searchEnd - candidate &&
                candidate >= start && candidate <= start + length && size <= start + length - candidate)
            {
                return candidate;
            }
        }

        return 0;
    }

    private int FindMappedRangeIndex(ulong address)
    {
        var index = RangeSearch.FindLastIndexAtOrBelow(_mapped, address);
        if (index < 0)
        {
            return -1;
        }

        var range = _mapped.Values[index];
        return address < range.Address + range.Size ? index : -1;
    }

    private bool HasOnlyRangeKind(ulong address, ulong size, RangeKind kind)
    {
        var end = address + size;
        var current = address;
        while (current < end)
        {
            var index = FindMappedRangeIndex(current);
            if (index < 0 || _mapped.Values[index].Kind != kind)
            {
                return false;
            }

            current = Math.Min(end, _mapped.Values[index].Address + _mapped.Values[index].Size);
        }

        return true;
    }

    private bool TryAddMappedRange(ulong address, ulong size, RangeKind kind)
    {
        var end = address + size;
        var index = RangeSearch.FindLastIndexAtOrBelow(_mapped, address);
        if (index >= 0 && _mapped.Values[index].Address + _mapped.Values[index].Size > address)
        {
            return false;
        }

        if (index + 1 < _mapped.Count && _mapped.Keys[index + 1] < end)
        {
            return false;
        }

        _mapped[address] = new OwnedRange(address, size, kind);
        return true;
    }

    private bool TryRemoveMappedRanges(ulong address, ulong size, RangeKind kind)
    {
        if (!HasOnlyRangeKind(address, size, kind))
        {
            return false;
        }

        var end = address + size;
        var current = address;
        while (current < end)
        {
            var index = FindMappedRangeIndex(current);
            var original = _mapped.Values[index];
            var partEnd = Math.Min(end, original.Address + original.Size);
            _mapped.RemoveAt(index);
            if (original.Address < current)
            {
                _mapped[original.Address] = original with { Size = current - original.Address };
            }

            if (partEnd < original.Address + original.Size)
            {
                _mapped[partEnd] = original with { Address = partEnd, Size = original.Address + original.Size - partEnd };
            }

            current = partEnd;
        }

        return true;
    }
}

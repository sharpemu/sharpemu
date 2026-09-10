// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private const int MemoryNoSpace = unchecked((int)0x8002000C);
    private const int MemoryAccessDenied = unchecked((int)0x8002000D);
    private const int MemoryFault = unchecked((int)0x8002000E);
    private const int MemoryInvalidArgument = unchecked((int)0x80020016);
    private static FlexibleBackingPool _flexibleBacking =
        new(GuestMemoryLayout.FlexibleOffset, GuestMemoryLayout.FlexibleBytes);
    private static IGuestBackedSpace? _backingOwner;
    private static int _prtMapWarning;

    internal static bool TryReadPrtBacking(IGuestBackedSpace backing, ulong address, Span<byte> destination)
    {
        var size = (ulong)destination.Length;
        lock (_memoryGate)
        {
            if (!ReferenceEquals(backing, _backingOwner) ||
                !KernelRuntimeCompatExports.ContainsPrtRange(address, size))
                return false;

            var regions = GetMappingSlices(address, size);
            if (!MappingsCoverRange(regions, address, size) ||
                regions.Any(region => !region.IsReserved && !backing.IsBackedRange(region.Address, region.Length)))
                return false;

            // Keep mappings stable while copying resident bytes and clearing reserved gaps.
            foreach (var region in regions)
            {
                var target = destination.Slice((int)(region.Address - address), (int)region.Length);
                if (region.IsReserved)
                    target.Clear();
                else if (!backing.TryReadBacking(region.Address, target))
                    return false;
            }
            return true;
        }
    }

    internal static FlexibleBackingPool SetFlexibleBackingForTests(FlexibleBackingPool pool)
    {
        lock (_memoryGate)
        {
            if (_flexibleBacking.Used != 0)
                throw new InvalidOperationException("Flexible mappings are still active.");
            var previous = _flexibleBacking;
            _flexibleBacking = pool;
            return previous;
        }
    }

    public static void ResetBackingMappings(IGuestBackedSpace? owner)
    {
        RunMappingTransaction(() =>
        {
            ResetBackingMappingsCore(owner);
            return 0;
        });
    }

    private static void ResetBackingMappingsCore(IGuestBackedSpace? owner)
    {
        lock (_memoryGate)
        {
            if (owner is null || !ReferenceEquals(owner, _backingOwner))
                return;
            foreach (var region in _mappedRegions.Values)
                GuestGpuMemoryHook.NoteUnmapped(region.Address, region.Length);
            _mappedRegions.Clear();
            _mappedRegionNames.Clear();
            _directAllocations.Clear();
            _flexibleBacking.Reset();
            _nextPhysicalAddress = 0;
            _nextVirtualAddress = 0;
            _backingOwner = null;
        }
    }

    private static IGuestBackedSpace? ResolveBackingSpace(CpuContext ctx)
    {
        if (!KernelVirtualRangeAllocator.TryResolveAddressSpace(ctx.Memory, out var addressSpace) ||
            addressSpace is not IGuestBackedSpace space)
            return null;
        if (_backingOwner is not null && !ReferenceEquals(_backingOwner, space))
            throw new InvalidOperationException("The previous guest address space is still active.");
        _backingOwner = space;
        return space;
    }

    private static bool TryDecodeMappedProtection(int value, out GuestPageProtection protection)
    {
        protection = GuestPageProtection.None;
        if (value != 0 && (value & 0x37) == 0)
            return false;
        if ((value & (OrbisProtCpuRead | OrbisProtGpuRead)) != 0)
            protection |= GuestPageProtection.Read;
        if ((value & (OrbisProtCpuWrite | OrbisProtGpuWrite)) != 0)
            protection |= GuestPageProtection.Read | GuestPageProtection.Write;
        if ((value & OrbisProtCpuExec) != 0)
            protection |= GuestPageProtection.Execute;
        return true;
    }

    private static bool IsValidMapRange(ulong length, ulong alignment) =>
        length != 0 && IsAligned(length, OrbisPageSize) &&
        (alignment == 0 || (alignment & (alignment - 1)) == 0 || IsAligned(alignment, OrbisPageSize));

    private static MappedRegion SliceMapping(MappedRegion region, ulong start, ulong end) => region with
    {
        Address = start,
        Length = end - start,
        DirectStart = region.IsDirect ? region.DirectStart + start - region.Address : 0,
        BackingOffset = region.IsDirect || region.IsFlexible ? region.BackingOffset + start - region.Address : 0,
    };

    private static MappedRegion[] GetMappingSlices(ulong address, ulong size, bool clip = true) => _mappedRegions.Values
        .Where(region => region.Address < address + size && address < region.Address + region.Length)
        .Select(region => clip ? SliceMapping(region, Math.Max(address, region.Address),
            Math.Min(address + size, region.Address + region.Length)) : region).ToArray();

    private static bool MappingsCoverRange(MappedRegion[] regions, ulong address, ulong size)
    {
        var current = address;
        foreach (var region in regions)
        {
            if (region.Address != current)
                return false;
            current += region.Length;
        }
        return current == address + size;
    }

    private static void RemoveMappingLocked(ulong address, ulong size)
    {
        ReplaceMappedRegionRangeLocked(new MappedRegion(address, size, 0, false, false, 0, IsReserved: true));
        _mappedRegions.Remove(address);
    }

    private static void RestoreViews(IGuestBackedSpace space, IReadOnlyList<MappedRegion> regions)
    {
        for (var index = regions.Count - 1; index >= 0; index--)
        {
            var region = regions[index];
            if (!TryDecodeMappedProtection(region.Protection, out var protection) ||
                !space.TryMapBacked(region.Address, region.Length, region.BackingOffset, protection, out _))
                Environment.FailFast("Cannot restore a guest backing view.");
        }
    }

    private static void RestoreGpuMappings(IEnumerable<MappedRegion> regions)
    {
        foreach (var region in regions)
            if (!region.IsReserved && TryDecodeMappedProtection(region.Protection, out var mode))
                GuestGpuMemoryHook.NoteMapped(region.Address, region.Length, mode);
    }

    private static bool TryUnmapViews(IGuestBackedSpace space, MappedRegion[] regions)
    {
        var removed = new List<MappedRegion>();
        foreach (var region in regions)
        {
            if (region.IsReserved)
                continue;
            if ((!region.IsDirect && !region.IsFlexible) || !space.IsBackedView(region.Address))
                return false;
        }
        foreach (var region in regions)
            GuestGpuMemoryHook.NoteUnmapped(region.Address, region.Length);
        foreach (var region in regions)
        {
            if (region.IsReserved)
                continue;
            if (!space.TryUnmapBacked(region.Address, region.Length))
            {
                RestoreViews(space, removed);
                RestoreGpuMappings(regions);
                return false;
            }
            removed.Add(region);
        }
        return true;
    }

    private static bool TryReplaceWithHole(IGuestBackedSpace space, ulong address, ulong size, bool noOverwrite)
    {
        var regions = GetMappingSlices(address, size);
        if (noOverwrite && regions.Length != 0)
            return false;
        if (MappingsCoverRange(regions, address, size) && regions.All(region => region.IsReserved))
        {
            if (!space.TryHoldRange(address, size))
                return false;
            GuestGpuMemoryHook.NoteUnmapped(address, size);
            return true;
        }
        if (!TryUnmapViews(space, regions))
            return false;
        if (!space.TryHoldRange(address, size))
        {
            RestoreViews(space, regions.Where(region => !region.IsReserved).ToArray());
            RestoreGpuMappings(regions);
            return false;
        }
        foreach (var region in regions)
            if (region.IsFlexible)
                _flexibleBacking.Release(region.Address, region.Length);
        ReplaceMappedRegionRangeLocked(new MappedRegion(address, size, 0, false, false, 0, IsReserved: true));
        return true;
    }

    private static bool TrySelectBackingAddress(IGuestBackedSpace space, ulong requested, ulong length,
        ulong alignment, ulong flags, out ulong address, bool reuseReservation = true)
    {
        address = 0;
        if ((flags & OrbisKernelMapFixed) != 0)
        {
            if (!TryReplaceWithHole(space, requested, length, (flags & 0x80) != 0))
            {
                if (requested >= 0x10_0000_0000UL && requested < 0xFC_0000_0000UL &&
                    Interlocked.Exchange(ref _prtMapWarning, 1) == 0)
                    Console.Error.WriteLine("[LOADER][WARN] Shared mapping cannot replace the reserved aperture.");
                return false;
            }
            address = requested;
            return true;
        }
        var desired = requested != 0 ? requested : DefaultMapSearchBase;
        while (space.TryHoldRangeAtOrAbove(desired, length, alignment, out address))
        {
            var overlap = GetMappingSlices(address, length, clip: false);
            if (overlap.Length == 0 || (reuseReservation && address == requested &&
                MappingsCoverRange(GetMappingSlices(address, length), address, length) && overlap.All(region => region.IsReserved)))
                break;
            // Skip the complete reservation, not only the requested slice.
            desired = overlap[^1].Address + overlap[^1].Length;
        }
        if (address == 0)
            return false;
        GuestGpuMemoryHook.NoteUnmapped(address, length);
        return true;
    }

    internal static int ReserveBackingRange(CpuContext ctx, ulong pointer, ulong length, ulong flags, ulong alignment)
        => RunMappingTransaction(() => ReserveBackingRangeCore(ctx, pointer, length, flags, alignment));

    private static int RunMappingTransaction(Func<int> transaction)
    {
        // GPU handoff must precede locks needed by image and buffer reads.
        if (Monitor.IsEntered(_memoryGate))
            throw new InvalidOperationException("Cannot start a mapping transaction while holding the mapping lock.");
        if (GuestGpuMemoryHook.Current is not { } memory)
            return transaction();

        var result = MemoryFault;
        memory.RunMappingChange(() => result = transaction());
        return result;
    }

    private static int ReserveBackingRangeCore(CpuContext ctx, ulong pointer, ulong length, ulong flags, ulong alignment)
    {
        if (pointer == 0 || !IsValidMapRange(length, alignment))
            return MemoryInvalidArgument;
        if (!ctx.TryReadUInt64(pointer, out var requested))
            return MemoryFault;
        alignment = alignment == 0 ? OrbisPageSize : alignment;
        if ((flags & OrbisKernelMapFixed) != 0 &&
            (requested == 0 || !IsAligned(requested, OrbisPageSize) || requested > ulong.MaxValue - length))
            return MemoryInvalidArgument;
        lock (_memoryGate)
        {
            var space = ResolveBackingSpace(ctx);
            if (space is null || !TrySelectBackingAddress(space, requested, length, alignment, flags, out var address, reuseReservation: false))
                return MemoryNoSpace;
            ReplaceMappedRegionRangeLocked(new MappedRegion(address, length, 0, false, false, 0, IsReserved: true));
            if (ShouldTraceDirectMemory())
                Console.Error.WriteLine($"[LOADER][TRACE] reserve_virtual address=0x{address:X} requested=0x{requested:X} size=0x{length:X} flags=0x{flags:X}");
            return ctx.TryWriteUInt64(pointer, address) ? 0 : MemoryFault;
        }
    }

    private static bool HasPhysicalSpan(ulong start, ulong length)
    {
        if (length == 0 || start > ulong.MaxValue - length)
            return false;
        var current = start;
        foreach (var allocation in _directAllocations.Values.OrderBy(allocation => allocation.Start))
        {
            if (allocation.Start + allocation.Length <= current)
                continue;
            if (allocation.Start > current)
                return false;
            current = Math.Min(start + length, allocation.Start + allocation.Length);
            if (current == start + length)
                return true;
        }
        return false;
    }

    private static bool TryReleaseDirectMemoryRangeLocked(CpuContext ctx, ulong start, ulong length)
    {
        if (!HasPhysicalSpan(start, length))
            return false;
        var end = start + length;
        var aliases = _mappedRegions.Values.Where(region => region.IsDirect &&
            region.DirectStart < end && start < region.DirectStart + region.Length)
            .Select(region => SliceMapping(region,
                region.Address + Math.Max(start, region.DirectStart) - region.DirectStart,
                region.Address + Math.Min(end, region.DirectStart + region.Length) - region.DirectStart)).ToArray();
        if (aliases.Length != 0)
        {
            var space = ResolveBackingSpace(ctx);
            if (space is null || !TryUnmapViews(space, aliases))
                return false;
        }
        foreach (var alias in aliases)
        {
            RemoveMappingLocked(alias.Address, alias.Length);
            AgcExports.UnregisterHtileMetadataRange(ctx.Memory, alias.Address, alias.Length);
        }
        foreach (var allocation in _directAllocations.Values.Where(allocation =>
                     allocation.Start < end && start < allocation.Start + allocation.Length).ToArray())
        {
            var allocationEnd = allocation.Start + allocation.Length;
            _directAllocations.Remove(allocation.Start);
            if (allocation.Start < start)
                _directAllocations.Add(allocation.Start, allocation with { Length = start - allocation.Start });
            if (end < allocationEnd)
                _directAllocations.Add(end, allocation with { Start = end, Length = allocationEnd - end });
        }
        _nextPhysicalAddress = GetDirectMemoryHighWaterMarkLocked();
        return true;
    }

    private static int ProtectMappedRange(CpuContext ctx, ulong address, ulong length, int protection, int? memoryType = null)
    {
        if (GuestGpuMemoryHook.Traces(address, length))
            GuestGpuMemoryHook.Trace(address, length, $"kernel-protect raw=0x{protection:X}");
        if (address == 0 || length == 0 || !TryDecodeMappedProtection(protection, out var mode) ||
            !TryNormalizeProtectRange(address, length, out var start, out var size))
            return MemoryInvalidArgument;
        lock (_memoryGate)
        {
            var regions = GetMappingSlices(start, size);
            var backed = regions.Any(region => region.IsDirect || region.IsFlexible);
            if (backed)
            {
                start = address & ~(OrbisPageSize - 1);
                if (length > ulong.MaxValue - (address - start) - (OrbisPageSize - 1))
                    return MemoryInvalidArgument;
                size = AlignUp(length + address - start, OrbisPageSize);
                regions = GetMappingSlices(start, size);
                if (!MappingsCoverRange(regions, start, size) || regions.Any(region => region.IsReserved) ||
                    !KernelVirtualRangeAllocator.TryResolveAddressSpace(ctx.Memory, out var space) ||
                    (!GuestGpuMemoryHook.NoteProtected(start, size, mode) && !space.TryProtect(start, size, mode)))
                    return MemoryAccessDenied;
            }
            else if (!GuestGpuMemoryHook.NoteProtected(start, size, mode) && !TryProtectHostRange(start, size, protection))
                return unchecked((int)0x80020002);
            _ = TryApplyMappedRegionProtectionLocked(start, size, protection, memoryType);
            return 0;
        }
    }
}

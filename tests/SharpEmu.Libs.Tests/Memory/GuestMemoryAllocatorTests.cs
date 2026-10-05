// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.Core.Loader;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

public sealed class GuestMemoryAllocatorTests
{
    [Fact]
    public void FreedRangesAreReusedAndCoalesced()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());
        const ulong usableArenaSize = 0x2000_0000 - 0x1000;

        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x8000, 0x1000, out var second));
        Assert.True(memory.TryAllocateGuestMemory(usableArenaSize - 0xC000, 0x1000, out var third));
        Assert.False(memory.TryAllocateGuestMemory(1, 1, out _));

        Assert.True(memory.TryFreeGuestMemory(second));
        Assert.True(memory.TryAllocateGuestMemory(0x8000, 0x1000, out var reused));
        Assert.Equal(second, reused);

        Assert.True(memory.TryFreeGuestMemory(first));
        Assert.True(memory.TryFreeGuestMemory(reused));
        Assert.True(memory.TryFreeGuestMemory(third));
        Assert.False(memory.TryFreeGuestMemory(third));

        Assert.True(memory.TryAllocateGuestMemory(usableArenaSize, 0x1000, out var coalesced));
        Assert.Equal(first, coalesced);
    }

    [Fact]
    public void ArenaSupportsAllocationsBeyondLegacySixteenMiBLimit()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        Assert.True(memory.TryAllocateGuestMemory(0x0100_0000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x0020_0000, 0x1000, out var beyondLegacyLimit));
        Assert.Equal(first + 0x0100_0000, beyondLegacyLimit);
    }

    [Fact]
    public void SegmentProtectionIsAppliedInContiguousRuns()
    {
        const ulong pageSize = 0x1000;
        using var host = new RecordingHostMemory(3 * pageSize);
        using var memory = new PhysicalVirtualMemory(host);

        memory.Map(host.Address, 3 * pageSize, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);

        Assert.Equal(
            [
                (host.Address, 3 * pageSize, HostPageProtection.ReadWrite),
                (host.Address, 3 * pageSize, HostPageProtection.ReadOnly),
            ],
            host.ProtectionCalls);

        host.ProtectionCalls.Clear();
        memory.Map(host.Address + pageSize, pageSize, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Write);
        host.ProtectionCalls.Clear();

        memory.Map(host.Address, 3 * pageSize, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Execute);

        Assert.Equal(
            [
                (host.Address, 3 * pageSize, HostPageProtection.ReadWriteExecute),
                (host.Address, pageSize, HostPageProtection.ReadExecute),
                (host.Address + pageSize, pageSize, HostPageProtection.ReadWriteExecute),
                (host.Address + (2 * pageSize), pageSize, HostPageProtection.ReadExecute),
            ],
            host.ProtectionCalls);
    }

    [Fact]
    public unsafe void GetPointerCommitsLazyPageBeforeReturningIt()
    {
        const ulong address = 0x00005000_0000_0000;
        const ulong pageSize = 0x1000;
        using var host = new LazyHostMemory(address);
        using var memory = new PhysicalVirtualMemory(host);
        memory.AllocateAt(address, (4UL << 30) + pageSize, executable: false, allowAlternative: false);
        host.CommitCalls.Clear();

        var pointer = memory.GetPointer(address + 0x123);

        Assert.Equal(address + 0x123, (ulong)pointer);
        // GetPointer commits a 32 MiB working-set chunk as 4 KiB pages against
        // this fake host (Query reports 4 KiB reserved regions). Non-page-
        // aligned start makes AlignUp(addr + chunk) cover one extra page.
        const ulong lazyPrimeChunkBytes = 0x0200_0000UL;
        var endPage = (address + 0x123 + lazyPrimeChunkBytes + pageSize - 1) & ~(pageSize - 1);
        Assert.Equal((int)((endPage - address) / pageSize), host.CommitCalls.Count);
        Assert.Equal((address, pageSize, HostPageProtection.ReadWrite), host.CommitCalls[0]);
        Assert.All(
            host.CommitCalls,
            call => Assert.Equal(pageSize, call.Size));
    }

    [Fact]
    public unsafe void GetPointerReturnsNullWhenLazyCommitFails()
    {
        const ulong address = 0x00005000_0000_0000;
        using var host = new LazyHostMemory(address);
        using var memory = new PhysicalVirtualMemory(host);
        memory.AllocateAt(address, (4UL << 30) + 0x1000, executable: false, allowAlternative: false);
        host.CommitCalls.Clear();
        host.CommitSucceeds = false;

        Assert.Equal(0UL, (ulong)memory.GetPointer(address));
    }

    [Fact]
    public void AdjacentFixedGuestPageMappingsShareAHostGranule()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var memory = new PhysicalVirtualMemory(new GranularityAwareHostMemory());
        const ulong baseAddress = 0x0000008001600000;

        Assert.Equal(baseAddress, memory.AllocateAt(baseAddress, 0x4000, executable: false, allowAlternative: false));
        Assert.Equal(
            baseAddress + 0x4000,
            memory.AllocateAt(baseAddress + 0x4000, 0x4000, executable: false, allowAlternative: false));
        Assert.Equal(
            baseAddress + 0x8000,
            memory.AllocateAt(baseAddress + 0x8000, 0x8000, executable: false, allowAlternative: false));

        Assert.True(memory.IsAccessible(baseAddress, 0x10000));
    }

    [Theory]
    [InlineData(0x0000UL, 0x6000UL)]
    [InlineData(0x1000UL, 0x2000UL)]
    [InlineData(0x5000UL, 0x2000UL)]
    public void FixedAllocationRejectsCommittedOverlap(ulong offset, ulong size)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var memory = new PhysicalVirtualMemory(new GranularityAwareHostMemory());
        const ulong baseAddress = 0x0000008001600000;
        memory.AllocateAt(baseAddress, 0x6000, executable: true, allowAlternative: false);

        Assert.Throws<InvalidOperationException>(() =>
            memory.AllocateAt(baseAddress + offset, size, executable: true, allowAlternative: false));
        Assert.False(memory.TryAllocateAtExact(baseAddress + offset, size, true, out var actualAddress));
        Assert.Equal(0UL, actualAddress);
        Assert.Single(memory.SnapshotRegions());
        Assert.True(memory.IsAccessible(baseAddress + 0x5000, 8));
        Assert.False(memory.IsAccessible(baseAddress + 0x6000, 1));
    }

    [Fact]
    public void TryBackFixedRangeSharesAHostGranuleAcrossCallsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var memory = new PhysicalVirtualMemory(new GranularityAwareHostMemory());
        const ulong baseAddress = 0x0000008001600000;

        Assert.True(memory.TryBackFixedRange(baseAddress, 0x4000, executable: false));
        Assert.True(memory.TryBackFixedRange(baseAddress + 0x4000, 0x4000, executable: false));

        Assert.True(memory.IsAccessible(baseAddress, 0x8000));
    }

    [Fact]
    public void AlignedAllocationDoesNotRetainOverallocatedMappingsOutsideMacOS()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        const ulong desiredAddress = 0x00005000_0000_0123;
        const ulong alignment = 0x10000;
        const ulong alignedAddress = 0x00005000_0001_0000;
        const ulong allocationSize = 0x2000;
        using var host = new RelocatingHostMemory(alignedAddress);
        using var memory = new PhysicalVirtualMemory(host);

        Assert.True(memory.TryAllocateAtOrAbove(desiredAddress, 0x1234, false, alignment, out var actualAddress));
        Assert.Equal(alignedAddress + alignment, actualAddress);
        Assert.Equal(
            [
                (alignedAddress, allocationSize),
                (alignedAddress + alignment, allocationSize),
            ],
            host.AllocationCalls);
        Assert.Equal([alignedAddress + 0x1000], host.FreedAddresses);
    }

    [Fact]
    public void TryBackFixedRangeRollsBackEarlierGapsWhenLaterGapCannotBeBacked()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Layout: committed | free | committed | free
        // First free gap allocates successfully, second fails.
        // The first allocation must be freed — nothing should leak.
        const ulong rangeBase = 0x0000_0020_2F00_0000;
        const ulong rangeSize = 0x4000;
        using var host = new GappedHostMemory(rangeBase);
        using var memory = new PhysicalVirtualMemory(host);

        Assert.False(memory.TryBackFixedRange(rangeBase, rangeSize, executable: false));

        // First gap allocates successfully; second gap is attempted and fails.
        Assert.Equal(
            [(rangeBase + 0x1000, 0x1000), (rangeBase + 0x3000, 0x1000)],
            host.AllocationCalls);
        // First gap must have been freed during rollback — nothing leaks.
        Assert.Equal([rangeBase + 0x1000], host.FreedAddresses);
    }

    [Fact]
    public void TryBackFixedRangeFillsOnlyTheFreePagesOfAPartiallyOccupiedRange()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const ulong rangeBase = 0x0000_0020_2F00_0000;
        const ulong rangeSize = 0x40_0000;
        const ulong occupiedSize = 0x4_0000;
        using var host = new PartialOverlapHostMemory(rangeBase, occupiedSize, rangeSize);
        using var memory = new PhysicalVirtualMemory(host);

        Assert.True(memory.TryBackFixedRange(rangeBase, rangeSize, executable: false));

        // Only the free tail is allocated; the already-occupied head is untouched.
        Assert.Equal(
            [(rangeBase + occupiedSize, rangeSize - occupiedSize)],
            host.AllocationCalls);
    }

    [Fact]
    public void TryBackFixedRangeReturnsFalseWhenRangeIsFullyOccupied()
    {
        const ulong rangeBase = 0x0000_0020_2F00_0000;
        const ulong rangeSize = 0x40_0000;
        using var host = new PartialOverlapHostMemory(rangeBase, rangeSize, rangeSize);
        using var memory = new PhysicalVirtualMemory(host);

        Assert.False(memory.TryBackFixedRange(rangeBase, rangeSize, executable: false));
        Assert.Empty(host.AllocationCalls);
    }

    private sealed class PartialOverlapHostMemory : IHostMemory, IDisposable
    {
        private readonly ulong _rangeBase;
        private readonly ulong _occupiedEnd;
        private readonly ulong _rangeEnd;

        public PartialOverlapHostMemory(ulong rangeBase, ulong occupiedSize, ulong rangeSize)
        {
            _rangeBase = rangeBase;
            _occupiedEnd = rangeBase + occupiedSize;
            _rangeEnd = rangeBase + rangeSize;
        }

        public List<(ulong Address, ulong Size)> AllocationCalls { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            AllocationCalls.Add((desiredAddress, size));
            return desiredAddress;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => true;

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            // Report contiguous runs of same-state pages, exactly as VirtualQuery
            // does: the occupied head first, then the free tail.
            if (address < _occupiedEnd)
            {
                info = new HostRegionInfo(
                    address,
                    _rangeBase,
                    _occupiedEnd - address,
                    HostRegionState.Committed,
                    RawState: 0x1000,
                    HostPageProtection.ReadWrite,
                    RawProtection: 0x04,
                    RawAllocationProtection: 0x04);
                return true;
            }

            info = new HostRegionInfo(
                address,
                AllocationBase: 0,
                _rangeEnd - address,
                HostRegionState.Free,
                RawState: 0x10000,
                HostPageProtection.NoAccess,
                RawProtection: 0x01,
                RawAllocationProtection: 0);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class GappedHostMemory : IHostMemory, IDisposable
    {
        // Layout (4 × 0x1000 pages):
        //   [committed] [free] [committed] [free]
        // Allocate succeeds for the first free gap, fails for the second.
        private readonly ulong _base;
        private readonly ulong _firstGapStart;
        private readonly ulong _firstGapEnd;
        private readonly ulong _secondGapStart;
        private readonly ulong _secondGapEnd;
        private readonly ulong _end;

        public GappedHostMemory(ulong @base)
        {
            _base = @base;
            _firstGapStart = @base + 0x1000;
            _firstGapEnd = @base + 0x2000;
            _secondGapStart = @base + 0x3000;
            _secondGapEnd = @base + 0x4000;
            _end = @base + 0x4000;
        }

        public List<(ulong Address, ulong Size)> AllocationCalls { get; } = [];
        public List<ulong> FreedAddresses { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            AllocationCalls.Add((desiredAddress, size));

            // First free gap — succeed.
            if (desiredAddress == _firstGapStart && size == 0x1000)
            {
                return desiredAddress;
            }

            // Second free gap — fail to trigger rollback.
            return 0;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address)
        {
            FreedAddresses.Add(address);
            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            if (address < _firstGapStart)
            {
                // First block: committed.
                info = new HostRegionInfo(address, _base, _firstGapStart - address,
                    HostRegionState.Committed, RawState: 0x1000,
                    HostPageProtection.ReadWrite, RawProtection: 0x04, RawAllocationProtection: 0x04);
                return true;
            }

            if (address < _firstGapEnd)
            {
                // Second block: free (first gap).
                info = new HostRegionInfo(address, AllocationBase: 0, _firstGapEnd - address,
                    HostRegionState.Free, RawState: 0x10000,
                    HostPageProtection.NoAccess, RawProtection: 0x01, RawAllocationProtection: 0);
                return true;
            }

            if (address < _secondGapStart)
            {
                // Third block: committed.
                info = new HostRegionInfo(address, _base + 0x2000, _secondGapStart - address,
                    HostRegionState.Committed, RawState: 0x1000,
                    HostPageProtection.ReadWrite, RawProtection: 0x04, RawAllocationProtection: 0x04);
                return true;
            }

            // Fourth block: free (second gap — this one will fail to allocate).
            info = new HostRegionInfo(address, AllocationBase: 0, _end - address,
                HostRegionState.Free, RawState: 0x10000,
                HostPageProtection.NoAccess, RawProtection: 0x01, RawAllocationProtection: 0);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size) { }

        public void Dispose() { }
    }

    private sealed class FakeHostMemory : IHostMemory
    {
        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            desiredAddress != 0 ? desiredAddress : 0x00007000_0000_0000;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => true;

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }
    }

    private sealed class RecordingHostMemory : IHostMemory, IDisposable
    {
        private readonly nint _allocation;
        private bool _freed;

        public RecordingHostMemory(ulong size)
        {
            _allocation = System.Runtime.InteropServices.Marshal.AllocHGlobal(checked((nint)(size + 0xFFF)));
            Address = (unchecked((ulong)_allocation) + 0xFFF) & ~0xFFFUL;
        }

        public ulong Address { get; }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> ProtectionCalls { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            desiredAddress == Address ? Address : 0;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address)
        {
            if (address != Address || _freed)
            {
                return false;
            }

            System.Runtime.InteropServices.Marshal.FreeHGlobal(_allocation);
            _freed = true;
            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            ProtectionCalls.Add((address, size, protection));
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            if (!_freed)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(_allocation);
                _freed = true;
            }
        }
    }

    private sealed class RelocatingHostMemory(ulong firstAddress) : IHostMemory, IDisposable
    {
        private bool _relocatedFirstAllocation;

        public List<(ulong Address, ulong Size)> AllocationCalls { get; } = [];

        public List<ulong> FreedAddresses { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            AllocationCalls.Add((desiredAddress, size));
            if (!_relocatedFirstAllocation)
            {
                _relocatedFirstAllocation = true;
                return firstAddress + 0x1000;
            }

            return desiredAddress;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address)
        {
            FreedAddresses.Add(address);
            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class GranularityAwareHostMemory : IHostMemory
    {
        private const ulong Granularity = 0x10000;
        private const ulong Page = 0x1000;

        private readonly SortedDictionary<ulong, (ulong Size, SortedSet<ulong> CommittedPages)> _allocations = new();

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            var reservedBase = Reserve(desiredAddress, size, protection);
            if (reservedBase != 0)
            {
                var start = desiredAddress == 0 ? reservedBase : AlignDown(desiredAddress, Page);
                Commit(start, size, protection);
            }

            return reservedBase;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            if (desiredAddress == 0)
            {
                return 0;
            }

            var allocationBase = AlignDown(desiredAddress, Granularity);
            var end = AlignUp(desiredAddress + size, Page);
            foreach (var (existingBase, existing) in _allocations)
            {
                if (allocationBase < existingBase + existing.Size && existingBase < end)
                {
                    return 0;
                }
            }

            _allocations[allocationBase] = (end - allocationBase, new SortedSet<ulong>());
            return allocationBase;
        }

        public bool Commit(ulong address, ulong size, HostPageProtection protection)
        {
            var start = AlignDown(address, Page);
            var end = AlignUp(address + size, Page);
            if (!TryFindAllocation(start, out var allocationBase, out var allocation) ||
                end > allocationBase + allocation.Size)
            {
                return false;
            }

            for (var page = start; page < end; page += Page)
            {
                allocation.CommittedPages.Add(page);
            }

            return true;
        }

        public bool Free(ulong address) => _allocations.Remove(address);

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            var page = AlignDown(address, Page);
            if (TryFindAllocation(page, out var allocationBase, out var allocation))
            {
                var committed = allocation.CommittedPages.Contains(page);
                var runEnd = page + Page;
                while (runEnd < allocationBase + allocation.Size &&
                       allocation.CommittedPages.Contains(runEnd) == committed)
                {
                    runEnd += Page;
                }

                info = new HostRegionInfo(
                    page,
                    allocationBase,
                    runEnd - page,
                    committed ? HostRegionState.Committed : HostRegionState.Reserved,
                    0,
                    committed ? HostPageProtection.ReadWrite : HostPageProtection.NoAccess,
                    0,
                    0);
                return true;
            }

            var freeEnd = ulong.MaxValue;
            foreach (var existingBase in _allocations.Keys)
            {
                if (existingBase > page)
                {
                    freeEnd = existingBase;
                    break;
                }
            }

            info = new HostRegionInfo(
                page,
                0,
                freeEnd - page,
                HostRegionState.Free,
                0,
                HostPageProtection.NoAccess,
                0,
                0);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        private bool TryFindAllocation(
            ulong address,
            out ulong allocationBase,
            out (ulong Size, SortedSet<ulong> CommittedPages) allocation)
        {
            foreach (var (existingBase, existing) in _allocations)
            {
                if (address >= existingBase && address < existingBase + existing.Size)
                {
                    allocationBase = existingBase;
                    allocation = existing;
                    return true;
                }
            }

            allocationBase = 0;
            allocation = default;
            return false;
        }

        private static ulong AlignDown(ulong value, ulong alignment) => value & ~(alignment - 1);

        private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
    }

    private sealed class LazyHostMemory(ulong address) : IHostMemory, IDisposable
    {
        public bool CommitSucceeds { get; set; } = true;

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> CommitCalls { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            desiredAddress == address ? address : 0;

        public bool Commit(ulong commitAddress, ulong size, HostPageProtection protection)
        {
            CommitCalls.Add((commitAddress, size, protection));
            return CommitSucceeds;
        }

        public bool Free(ulong freeAddress) => freeAddress == address;

        public bool Protect(ulong protectAddress, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong protectAddress, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong queryAddress, out HostRegionInfo info)
        {
            var pageAddress = queryAddress & ~0xFFFUL;
            info = new HostRegionInfo(
                pageAddress,
                address,
                0x1000,
                HostRegionState.Reserved,
                0,
                HostPageProtection.NoAccess,
                0,
                0);
            return true;
        }

        public void FlushInstructionCache(ulong flushAddress, ulong size)
        {
        }

        public void Dispose()
        {
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.Core.Loader;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// PhysicalVirtualMemory is the host-backed (identity-mapped) implementation.
// Reserve-only regions (> 4 GiB, non-executable) defer commit until first
// access; TryAllocateGuestMemory serves a first-fit free-list with coalescing.
// These tests pin that behaviour through fake IHostMemory implementations.
public sealed class PhysicalVirtualMemoryTests
{
    // 1. Lazy commit: a reserve-only region has its pages committed on demand
    //    when read; freshly committed pages read as zero.
    [Fact]
    public void LazyReadCommitsPageOnDemandAndReadsZero()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        // > 4 GiB, non-executable -> reserve-only with lazy commit.
        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        Assert.NotEqual(0UL, address);

        // Discard the priming commits AllocateAt issues up front; we want to
        // observe the on-demand commit triggered by the read itself.
        host.CommitCalls.Clear();

        var buffer = new byte[1];
        Assert.True(memory.TryRead(address, buffer));
        Assert.Equal(0, buffer[0]);

        // The touched page (page-aligned to `address`) was committed on demand.
        var page = address & ~0xFFFUL;
        Assert.Equal([(page, 0x1000UL, HostPageProtection.ReadWrite)], host.CommitCalls);
    }

    // 2. Reserve-only region: GetPointer commits the page before returning it,
    //    so callers receive a valid (non-null) pointer. An unmapped address yields null.
    [Fact]
    public unsafe void GetPointerOnReserveOnlyRegionCommitsAndReturnsValidPointer()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        host.CommitCalls.Clear();

        var pointer = memory.GetPointer(address + 0x123);
        Assert.NotEqual(0UL, (ulong)pointer);
        Assert.Equal(address + 0x123, (ulong)pointer);

        var page = (address + 0x123) & ~0xFFFUL;
        Assert.Equal([(page, 0x1000UL, HostPageProtection.ReadWrite)], host.CommitCalls);
    }

    [Fact]
    public unsafe void GetPointerOnUnmappedAddressReturnsNull()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        Assert.Equal(0UL, (ulong)memory.GetPointer(0x0001_0000));
    }

    // 3. Free-list reuse: a freed range is served back by first-fit allocation,
    //    preferring the lowest fitting free range over the larger trailing span.
    [Fact]
    public void FreedRangeIsReusedByFirstFitAllocation()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var second));
        Assert.NotEqual(first, second);
        Assert.True(memory.TryFreeGuestMemory(first));

        // A smaller allocation must reuse first's freed slot (lowest fitting range),
        // not the larger trailing free range.
        Assert.True(memory.TryAllocateGuestMemory(0x2000, 0x1000, out var reused));
        Assert.Equal(first, reused);
    }

    // 4. Coalescing: freeing the middle of three adjacent ranges merges both the
    //    left and right free neighbours in a single TryFreeGuestMemory call,
    //    restoring the full span for subsequent first-fit reuse.
    [Fact]
    public void FreeingMiddleRangeCoalescesBothNeighbours()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        // Three adjacent 0x1000 allocations: offsets 0x1000, 0x2000, 0x3000.
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var second));
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var third));

        // Free the outer ranges first, leaving two separate free ranges.
        Assert.True(memory.TryFreeGuestMemory(first));
        Assert.True(memory.TryFreeGuestMemory(third));

        // Freeing the middle range must coalesce both neighbours at once.
        Assert.True(memory.TryFreeGuestMemory(second));

        // The whole arena is now one coalesced free range; a full-arena allocation
        // reuses first's base address.
        Assert.True(memory.TryAllocateGuestMemory(0x000F_F000, 0x1000, out var coalesced));
        Assert.Equal(first, coalesced);
    }

    [Fact]
    public void ExactAllocationCommitsAdjacentRangeInOwnedHostReservation()
    {
        const ulong allocationBase = 0x0000_0080_0160_0000;
        var host = new GranularityHostMemory(allocationBase, allowInitialAllocation: true);
        using (var memory = new PhysicalVirtualMemory(host))
        {
            Assert.True(memory.TryAllocateAtExact(allocationBase, 0x4000, executable: false, out var first));
            Assert.Equal(allocationBase, first);

            var second = memory.AllocateAt(
                allocationBase + 0x4000,
                0x4000,
                executable: false,
                allowAlternative: false);
            Assert.Equal(allocationBase + 0x4000, second);
            var expectedCommits = OperatingSystem.IsWindows()
                ? new[]
                {
                    (allocationBase, 0x4000UL, HostPageProtection.ReadWrite),
                    (allocationBase + 0x4000, 0x4000UL, HostPageProtection.ReadWrite),
                }
                : new[]
                {
                    (allocationBase + 0x4000, 0x4000UL, HostPageProtection.ReadWrite),
                };
            Assert.Equal(expectedCommits, host.CommitCalls);
        }

        Assert.Equal([allocationBase], host.FreeCalls);
    }

    [Fact]
    public void ExactAllocationDoesNotCommitUnownedHostReservation()
    {
        const ulong allocationBase = 0x0000_0080_0160_0000;
        var host = new GranularityHostMemory(allocationBase, allowInitialAllocation: false);
        using var memory = new PhysicalVirtualMemory(host);

        Assert.False(memory.TryAllocateAtExact(
            allocationBase + 0x4000,
            0x4000,
            executable: false,
            out var actualAddress));
        Assert.Equal(0UL, actualAddress);
        Assert.Empty(host.CommitCalls);
    }

    [Fact]
    public void TryWriteSpansAdjacentMappedRegions()
    {
        using var host = new ContiguousHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var firstAddress = host.Address;
        var secondAddress = firstAddress + 0x1000;

        Assert.True(memory.TryAllocateAtExact(firstAddress, 0x1000, executable: false, out _));
        Assert.True(memory.TryAllocateAtExact(secondAddress, 0x1000, executable: false, out _));

        var source = Enumerable.Range(0, 0x1800).Select(index => unchecked((byte)index)).ToArray();
        Assert.True(memory.TryWrite(firstAddress + 0x800, source));

        var firstPage = new byte[0x800];
        var secondPage = new byte[0x1000];
        Assert.True(memory.TryRead(firstAddress + 0x800, firstPage));
        Assert.True(memory.TryRead(secondAddress, secondPage));
        Assert.True(source.AsSpan(0, 0x800).SequenceEqual(firstPage));
        Assert.True(source.AsSpan(0x800).SequenceEqual(secondPage));
    }

    /// <summary>
    /// Host memory backed by a single real, zero-initialised page. Reserve/Allocate
    /// report the page-aligned buffer address so lazy-commit read paths can actually
    /// dereference the returned pointer. Query always reports Reserved, so
    /// EnsureRangeCommitted issues a Commit on first access.
    /// </summary>
    private sealed unsafe class LazyZeroedHostMemory : IHostMemory, IDisposable
    {
        private readonly void* _allocation;
        private readonly ulong _address;
        private bool _freed;

        public LazyZeroedHostMemory()
        {
            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x2000);
            _address = ((ulong)_allocation + 0xFFF) & ~0xFFFUL;
        }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> CommitCalls { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => _address;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => _address;

        public bool Commit(ulong address, ulong size, HostPageProtection protection)
        {
            CommitCalls.Add((address, size, protection));
            return true;
        }

        public bool Free(ulong address)
        {
            // The real buffer is released in Dispose; keep Free a no-op so
            // PhysicalVirtualMemory.Clear does not double-free it.
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
            var pageAddress = address & ~0xFFFUL;
            info = new HostRegionInfo(
                pageAddress,
                pageAddress,
                0x1000,
                HostRegionState.Reserved,
                0,
                HostPageProtection.NoAccess,
                0,
                0);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            if (!_freed)
            {
                System.Runtime.InteropServices.NativeMemory.Free(_allocation);
                _freed = true;
            }
        }
    }

    // Minimal host memory for free-list tests: Allocate honours the desired
    // address (or a fallback), everything else succeeds as a no-op. The guest
    // allocation arena never dereferences, so no real backing is required.
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

    private sealed class GranularityHostMemory(ulong allocationBase, bool allowInitialAllocation) : IHostMemory
    {
        private bool _initialAllocationAvailable = allowInitialAllocation;

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> CommitCalls { get; } = [];

        public List<ulong> FreeCalls { get; } = [];

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            if (!_initialAllocationAvailable || desiredAddress != allocationBase)
            {
                return 0;
            }

            _initialAllocationAvailable = false;
            return allocationBase;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection)
        {
            CommitCalls.Add((address, size, protection));
            return true;
        }

        public bool Free(ulong address)
        {
            FreeCalls.Add(address);
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
            var reservedStart = allocationBase + 0x4000;
            var reservedEnd = allocationBase + 0x10000;
            if (address < reservedStart || address >= reservedEnd)
            {
                info = default;
                return false;
            }

            info = new HostRegionInfo(
                reservedStart,
                allocationBase,
                reservedEnd - reservedStart,
                HostRegionState.Reserved,
                0x2000,
                HostPageProtection.NoAccess,
                0x01,
                0x04);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }
    }

    private sealed unsafe class ContiguousHostMemory : IHostMemory, IDisposable
    {
        private readonly void* _allocation;

        public ContiguousHostMemory()
        {
            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x4000);
            Address = ((ulong)_allocation + 0xFFF) & ~0xFFFUL;
        }

        public ulong Address { get; }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => desiredAddress;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => desiredAddress;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => true;

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0x04;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = rawProtection;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = new HostRegionInfo(
                address & ~0xFFFUL,
                Address,
                0x1000,
                HostRegionState.Committed,
                0x1000,
                HostPageProtection.ReadWrite,
                0x04,
                0x04);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            System.Runtime.InteropServices.NativeMemory.Free(_allocation);
        }
    }
}

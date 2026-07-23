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

    [Fact]
    public void RepeatedLazyReadUsesCommittedRangeCache()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        host.CommitCalls.Clear();

        Span<byte> buffer = stackalloc byte[16];
        Assert.True(memory.TryRead(address + 0x100, buffer));
        var queryCallsAfterFirstRead = host.QueryCalls;
        Assert.True(memory.TryRead(address + 0x108, buffer[..8]));

        Assert.Equal(queryCallsAfterFirstRead, host.QueryCalls);
        Assert.Single(host.CommitCalls);
    }

    [Fact]
    public void TryCopyHandlesOverlappingIdentityMappedRanges()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        Assert.True(memory.TryWrite(address, new byte[] { 1, 2, 3, 4, 5, 6 }));

        Assert.True(memory.TryCopy(address + 2, address, 4));

        Span<byte> result = stackalloc byte[6];
        Assert.True(memory.TryRead(address, result));
        Assert.Equal(new byte[] { 1, 2, 1, 2, 3, 4 }, result.ToArray());
    }

    [Fact]
    public void RepeatedTryCopyKeepsSourceAndDestinationCommitRangesCached()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        var source = address + 0x100;
        var destination = address + 0x1100;
        Assert.True(memory.TryWrite(source, new byte[] { 1, 2, 3, 4 }));
        Assert.True(memory.TryWrite(destination, new byte[4]));

        host.CommitCalls.Clear();
        Assert.True(memory.TryCopy(destination, source, 4));
        var queryCallsAfterFirstCopy = host.QueryCalls;
        Assert.True(memory.TryCopy(destination, source, 4));

        Assert.Equal(queryCallsAfterFirstCopy, host.QueryCalls);
    }

    [Fact]
    public void AdjacentRegionsSupportCrossRegionRangeOperations()
    {
        using var host = new AdjacentRegionHostMemory(regionCount: 2, stridePages: 1);
        using var memory = new PhysicalVirtualMemory(host);

        var first = memory.AllocateAt(0, 0x1000, executable: false);
        var second = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(first + 0x1000, second);

        var address = first + 0x1000 - 4;
        var source = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert.True(memory.TryWrite(address, source));

        Span<byte> readback = stackalloc byte[8];
        Assert.True(memory.TryRead(address, readback));
        Assert.Equal(source, readback.ToArray());
        Assert.True(memory.TryCompare(address, source));
        Assert.True(memory.IsAccessible(address, (ulong)source.Length));

        // The copy crosses the same region boundary and overlaps, so it must
        // retain memmove semantics rather than overwrite the source prefix.
        Assert.True(memory.TryCopy(address + 2, address, 6));
        Assert.True(memory.TryRead(address, readback));
        Assert.Equal(new byte[] { 1, 2, 1, 2, 3, 4, 5, 6 }, readback.ToArray());
    }

    [Fact]
    public void CrossRegionWriteWithGapFailsBeforeMutatingEarlierRegion()
    {
        using var host = new AdjacentRegionHostMemory(regionCount: 2, stridePages: 2);
        using var memory = new PhysicalVirtualMemory(host);

        var first = memory.AllocateAt(0, 0x1000, executable: false);
        _ = memory.AllocateAt(0, 0x1000, executable: false);

        var boundary = first + 0x1000 - 4;
        var sentinel = new byte[] { 0xA1, 0xA2, 0xA3, 0xA4 };
        Assert.True(memory.TryWrite(boundary, sentinel));

        // Four bytes are mapped, followed by an unmapped page. A failed
        // cross-region write must not partially update the mapped prefix.
        Assert.False(memory.TryWrite(boundary, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }));

        Span<byte> prefix = stackalloc byte[4];
        Assert.True(memory.TryRead(boundary, prefix));
        Assert.Equal(sentinel, prefix.ToArray());
        Assert.False(memory.IsAccessible(boundary, 8));
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
            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x3000);
            _address = ((ulong)_allocation + 0xFFF) & ~0xFFFUL;
        }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> CommitCalls { get; } = [];

        public int QueryCalls { get; private set; }

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
            QueryCalls++;
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

    /// <summary>
    /// Deterministic host backing for range tests. Each allocation is a page in
    /// one zeroed native block, with a configurable page stride so tests can
    /// create either adjacent regions or an explicit unmapped gap.
    /// </summary>
    private sealed unsafe class AdjacentRegionHostMemory : IHostMemory, IDisposable
    {
        private const ulong PageSize = 0x1000;
        private readonly void* _allocation;
        private readonly ulong _baseAddress;
        private readonly ulong _strideBytes;
        private int _allocationCount;
        private bool _freed;

        public AdjacentRegionHostMemory(int regionCount, int stridePages)
        {
            if (regionCount <= 0 || stridePages <= 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed(
                (nuint)((regionCount * stridePages + 1) * (int)PageSize));
            _baseAddress = ((ulong)_allocation + PageSize - 1) & ~(PageSize - 1);
            _strideBytes = (ulong)stridePages * PageSize;
        }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            var address = _baseAddress + ((ulong)_allocationCount++ * _strideBytes);
            return address;
        }

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
            var pageAddress = address & ~(PageSize - 1);
            info = new HostRegionInfo(
                pageAddress,
                pageAddress,
                PageSize,
                HostRegionState.Committed,
                0,
                HostPageProtection.ReadWrite,
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
}

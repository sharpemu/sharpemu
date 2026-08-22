// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.Core.Loader;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// PhysicalVirtualMemory is the host-backed (identity-mapped) implementation.
// Huge non-executable maps (> 4 GiB) prefer commit-first, then fall back to
// reserve-only + lazy commit when Allocate fails. TryAllocateGuestMemory serves
// a first-fit free-list with coalescing. These tests pin that behaviour through
// fake IHostMemory implementations that refuse full Allocate for huge sizes.
public sealed class PhysicalVirtualMemoryTests
{
    // 1. Lazy commit: a reserve-only region has its pages committed on demand
    //    when read; freshly committed pages read as zero.
    [Fact]
    public void LazyReadCommitsPageOnDemandAndReadsZero()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        // > 4 GiB, non-executable; fake host rejects Allocate so reserve-only
        // + lazy commit is used.
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
    public void EnsureRangeCommittedBacksOnlyTheMappedWindow()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        host.CommitCalls.Clear();

        var mappedAddress = address + 0x0800_0000UL;
        Assert.True(memory.TryEnsureRangeCommitted(mappedAddress, 0x3000));

        Assert.Equal(
            [
                (mappedAddress, 0x1000UL, HostPageProtection.ReadWrite),
                (mappedAddress + 0x1000, 0x1000UL, HostPageProtection.ReadWrite),
                (mappedAddress + 0x2000, 0x1000UL, HostPageProtection.ReadWrite),
            ],
            host.CommitCalls);
    }

    [Fact]
    public void EnsureRangeCommittedRejectsAnUnallocatedWindow()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        Assert.False(memory.TryEnsureRangeCommitted(0x0001_0000, 0x1000));
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
    public void TryCompareDistinguishesMismatchFromAccessFailure()
    {
        using var host = new ReadableHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var address = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.NotEqual(0UL, address);
        Assert.True(memory.TryWrite(address, new byte[] { 1, 2, 3, 4 }));

        Assert.True(memory.TryCompare(address, new byte[] { 1, 2, 3, 4 }, out var equal));
        Assert.True(equal);

        Assert.True(memory.TryCompare(address, new byte[] { 1, 2, 3, 5 }, out equal));
        Assert.False(equal);

        Assert.False(memory.TryCompare(ulong.MaxValue - 0x1000, new byte[4], out equal));
        Assert.False(equal);
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

        // GetPointer primes a 32 MiB working-set chunk (page-sized commits
        // against this fake host's 4 KiB Query regions). Non-page-aligned
        // start makes AlignUp(addr + chunk) cover one extra page.
        const ulong lazyPrimeChunkBytes = 0x0200_0000UL;
        var page = (address + 0x123) & ~0xFFFUL;
        var endPage = (address + 0x123 + lazyPrimeChunkBytes + 0xFFFUL) & ~0xFFFUL;
        Assert.Equal((int)((endPage - page) / 0x1000UL), host.CommitCalls.Count);
        Assert.Equal((page, 0x1000UL, HostPageProtection.ReadWrite), host.CommitCalls[0]);
        Assert.All(
            host.CommitCalls,
            call => Assert.Equal(0x1000UL, call.Size));
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

        // Force the commit-first → reserve-only fallback for huge maps.
        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => 0;

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

    private sealed unsafe class ReadableHostMemory : IHostMemory, IDisposable
    {
        private readonly void* _allocation;
        private readonly ulong _address;

        public ReadableHostMemory()
        {
            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x2000);
            _address = ((ulong)_allocation + 0xFFF) & ~0xFFFUL;
        }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            _address;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            _address;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => true;

        public bool Protect(
            ulong address,
            ulong size,
            HostPageProtection protection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0x04;
            return true;
        }

        public bool ProtectRaw(
            ulong address,
            ulong size,
            uint rawProtection,
            out uint rawOldProtection)
        {
            rawOldProtection = 0x04;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            if (address >= _address && address < _address + 0x1000)
            {
                info = new HostRegionInfo(
                    _address,
                    _address,
                    0x1000,
                    HostRegionState.Committed,
                    RawState: 0x1000,
                    HostPageProtection.ReadWrite,
                    RawProtection: 0x04,
                    RawAllocationProtection: 0x04);
                return true;
            }

            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose() =>
            System.Runtime.InteropServices.NativeMemory.Free(_allocation);
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

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
        const ulong usableArenaSize = 0x0100_0000 - 0x1000;

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
        Assert.Equal([(address, pageSize, HostPageProtection.ReadWrite)], host.CommitCalls);
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

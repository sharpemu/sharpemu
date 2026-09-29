// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

public sealed class ExecutableRangeAllocationTests
{
    [Fact]
    public void SearchFindsAnAlignedGapBetweenOccupiedProbeAddresses()
    {
        var host = new RangeMemory(0x18000, 0x30000);
        using var memory = new PhysicalVirtualMemory(host);
        Assert.True(memory.TryAllocateWithinRange(0x10000, 0x4000000, 0x2000, out var address));
        Assert.Equal(0x20000UL, address);
    }

    [Fact]
    public void SearchContinuesWhenAnotherAllocationTakesTheFirstCandidate()
    {
        var host = new RangeMemory(0x10000, 0x40000) { RejectFirst = true };
        using var memory = new PhysicalVirtualMemory(host);
        Assert.True(memory.TryAllocateWithinRange(0x10000, 0x40000, 0x2000, out var address));
        Assert.Equal(0x20000UL, address);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchContinuesWhenTheFreeRegionCandidateIsTaken(bool queryUnavailable)
    {
        var host = new RangeMemory(0x20000, 0x40000)
        {
            RejectedAddress = 0x20000,
            QueryUnavailable = queryUnavailable,
        };
        using var memory = new PhysicalVirtualMemory(host);
        Assert.True(memory.TryAllocateWithinRange(0x10000, 0x40000, 0x2000, out var address));
        Assert.Equal(0x30000UL, address);
    }

    [Theory]
    [InlineData(0x20000UL, 0x21fffUL, 0x2000UL)]
    [InlineData(0x20000UL, 0x30000UL, 0UL)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, 0x2000UL)]
    [InlineData(0x30000UL, 0x20000UL, 0x2000UL)]
    [InlineData(0x20000UL, 0x21800UL, 0x1800UL)]
    public void SearchRejectsAnInvalidOrInsufficientRange(ulong start, ulong end, ulong size)
    {
        using var memory = new PhysicalVirtualMemory(new RangeMemory(0x10000, 0x40000));
        Assert.False(memory.TryAllocateWithinRange(start, end, size, out var address));
        Assert.Equal(0UL, address);
    }

    private sealed class RangeMemory(ulong freeStart, ulong freeEnd) : IHostMemory
    {
        internal bool RejectFirst;
        internal ulong RejectedAddress;
        internal bool QueryUnavailable;
        public ulong Allocate(ulong address, ulong size, HostPageProtection protection)
        {
            if (RejectFirst) { RejectFirst = false; return 0; }
            if (address == RejectedAddress) return 0;
            return address >= freeStart && address < freeEnd && size <= freeEnd - address ? address : 0;
        }
        public ulong Reserve(ulong address, ulong size, HostPageProtection protection) => 0;
        public bool Commit(ulong address, ulong size, HostPageProtection protection) => false;
        public bool Free(ulong address) => true;
        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint previous)
        { previous = 0; return true; }
        public bool ProtectRaw(ulong address, ulong size, uint protection, out uint previous)
        { previous = 0; return true; }
        public void FlushInstructionCache(ulong address, ulong size) { }
        public bool Query(ulong address, out HostRegionInfo info)
        {
            if (QueryUnavailable) { info = default; return false; }
            var free = address >= freeStart && address < freeEnd;
            var start = address < freeStart ? 0UL : free ? freeStart : freeEnd;
            var end = address < freeStart ? freeStart : free ? freeEnd : ulong.MaxValue;
            info = new(start, start, end - start, free ? HostRegionState.Free : HostRegionState.Committed,
                0, HostPageProtection.NoAccess, 0, 0);
            return true;
        }
    }
}

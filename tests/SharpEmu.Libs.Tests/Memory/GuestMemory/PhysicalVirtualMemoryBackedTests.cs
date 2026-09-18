// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[Collection(GuestMemoryStateCollection.Name)]
public sealed unsafe class PhysicalVirtualMemoryBackedTests
{
    private sealed class QueryCountingHostMemory(IHostMemory inner) : IHostMemory
    {
        public int QueryCount { get; set; }
        public ulong Allocate(ulong address, ulong size, HostPageProtection protection) => inner.Allocate(address, size, protection);
        public ulong Reserve(ulong address, ulong size, HostPageProtection protection) => inner.Reserve(address, size, protection);
        public bool Commit(ulong address, ulong size, HostPageProtection protection) => inner.Commit(address, size, protection);
        public bool Free(ulong address) => inner.Free(address);
        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint previous) => inner.Protect(address, size, protection, out previous);
        public bool ProtectRaw(ulong address, ulong size, uint protection, out uint previous) => inner.ProtectRaw(address, size, protection, out previous);
        public void FlushInstructionCache(ulong address, ulong size) => inner.FlushInstructionCache(address, size);
        public bool Query(ulong address, out HostRegionInfo info)
        {
            QueryCount++;
            return inner.Query(address, out info);
        }
    }

    [Fact]
    public void SearchSkipsAFreeGapThatCannotFitTheAllocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = HostViewMemory.Create();
        var memoryHost = new QueryCountingHostMemory(PlatformMemory);
        using var memory = new PhysicalVirtualMemory(memoryHost, host, BackingSize);
        const ulong gapSize = 0x100000;
        var start = 0UL;
        for (var candidate = 0x3_0000_0000UL; candidate < 0x4_0000_0000UL; candidate += 0x1000000)
        {
            if (host.ReserveHole(candidate, 4 * gapSize) != candidate) continue;
            Assert.True(host.FreeHole(candidate, 4 * gapSize));
            start = candidate;
            break;
        }
        Assert.NotEqual(0UL, start);
        Assert.Equal(start + gapSize, host.ReserveHole(start + gapSize, gapSize));
        try
        {
            memoryHost.QueryCount = 0;
            Assert.True(memory.TryHoldRangeAtOrAbove(start, 2 * gapSize, host.Granularity, out var address));
            Assert.Equal(start + 2 * gapSize, address);
            Assert.InRange(memoryHost.QueryCount, 1, 3);
        }
        finally
        {
            Assert.True(host.FreeHole(start + gapSize, gapSize));
        }
    }

    [Fact]
    public void SearchSkipsKnownMappingsWithoutHostQueries()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        var memoryHost = new QueryCountingHostMemory(PlatformMemory);
        using var memory = new PhysicalVirtualMemory(memoryHost, host, BackingSize);
        Assert.True(memory.TryHoldRangeAtOrAbove(0x3_0000_0000, 2 * Segment, Segment, out var address));
        Assert.True(memory.TryMapBacked(address, Segment, 0, GuestPageProtection.Read, out _));
        memoryHost.QueryCount = 0;
        Assert.True(memory.TryHoldRangeAtOrAbove(address, Segment, Segment, out var selected));
        Assert.Equal(address + Segment, selected);
        Assert.Equal(0, memoryHost.QueryCount);
    }

    private static ulong Hold(PhysicalVirtualMemory memory, IHostViewMemory host)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var address = ProbeGuestAddress(host, HoleSize(host));
            if (memory.TryHoldRange(address, HoleSize(host)))
            {
                return address;
            }
        }

        Assert.Fail("could not hold a test range");
        return 0;
    }

    // A probed address can be taken before Map runs; retry like the hole helpers do.
    private static ulong MapPrivate(PhysicalVirtualMemory memory, IHostViewMemory host, ulong size, ProgramHeaderFlags flags)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var address = ProbeGuestAddress(host, size);
            try
            {
                memory.Map(address, size, 0, ReadOnlySpan<byte>.Empty, flags);
                return address;
            }
            catch (InvalidOperationException)
            {
            }
        }

        Assert.Fail("could not map a private test range");
        return 0;
    }

    [Fact]
    public void RegisterPacketCopiesValuesAcrossBackingViews()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var sourceViewAddress = Hold(memory, host);
        var destinationViewAddress = Hold(memory, host);
        Assert.True(memory.TryMapBacked(sourceViewAddress, Segment, 0, GuestPageProtection.Read, out _));
        Assert.True(memory.TryMapBacked(sourceViewAddress + Segment, Segment, 2 * Segment, GuestPageProtection.Read, out _));
        Assert.True(memory.TryMapBacked(destinationViewAddress, Segment, 4 * Segment, GuestPageProtection.Read, out _));
        Assert.True(memory.TryMapBacked(destinationViewAddress + Segment, Segment, 6 * Segment, GuestPageProtection.Read, out _));
        var context = new CpuContext(memory, Generation.Gen5);
        var commandBufferAddress = destinationViewAddress + 0x100;
        var packetAddress = destinationViewAddress + Segment - 24;
        var sourceAddress = sourceViewAddress + Segment - 8;
        byte[] values = Enumerable.Range(1, 16).Select(value => (byte)value).ToArray();
        Assert.True(memory.TryWrite(sourceAddress, values));
        Assert.True(context.TryWriteUInt64(commandBufferAddress + 0x10, packetAddress));
        Assert.True(context.TryWriteUInt64(commandBufferAddress + 0x18, destinationViewAddress + 2 * Segment));
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = 0x40;
        context[CpuRegister.Rdx] = sourceAddress;
        context[CpuRegister.Rcx] = 4;

        Assert.Equal(0, SharpEmu.Libs.Agc.AgcExports.CbSetShRegisterRangeDirect(context));

        Assert.Equal(packetAddress + 8, context[CpuRegister.Rax]);
        var copiedValues = new byte[16];
        Assert.True(memory.TryRead(packetAddress + 16, copiedValues));
        Assert.Equal(values, copiedValues);
        Assert.True(context.TryReadUInt64(commandBufferAddress + 0x10, out var commandCursor));
        Assert.Equal(packetAddress + 32, commandCursor);
    }

    [Fact]
    public void ViewsShareBackingAndKeepContentsAfterRemapping()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var first = Hold(memory, host);
        var second = Hold(memory, host);
        Assert.True(memory.TryMapBacked(first, Segment, 0, GuestPageProtection.Read, out _), "map first");
        Assert.True(memory.TryMapBacked(second, Segment, 0, GuestPageProtection.Read, out _), "map second");
        *(ulong*)first = Marker;
        Assert.Equal(Marker, *(ulong*)second);
        Assert.True(memory.TryProtect(first, Segment, GuestPageProtection.None), "protect");
        Assert.True(memory.TryWrite(first, BitConverter.GetBytes(~Marker)), "alias write");
        Assert.True(memory.TryCompare(second, BitConverter.GetBytes(~Marker)), "compare second");
        Assert.True(memory.TryCompare(first, BitConverter.GetBytes(~Marker)), "compare first");
        Assert.True(memory.TryCopy(first + 16, second, 8), "alias copy");
        Assert.Equal(~Marker, *(ulong*)(second + 16));
        Assert.True(memory.TryUnmapBacked(first, Segment));
        Assert.False(memory.IsBackedView(first));
        Assert.True(memory.TryMapBacked(first, Segment, 0, GuestPageProtection.Read, out _));
        Assert.Equal(~Marker, *(ulong*)first);
        Assert.True(memory.IsBackedView(first));
        Assert.Contains(memory.SnapshotRegions(), r => r.VirtualAddress == first);
    }

    [Fact]
    public void ClearKeepsTheSameMemoryObjectUsable()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var address = Hold(memory, host);
        Assert.True(memory.TryMapBacked(address, Segment, 0, GuestPageProtection.Read, out _));
        *(ulong*)address = Marker;
        memory.Clear();
        Assert.False(memory.IsBackedView(address));
        Assert.True(memory.TryMapBacked(address, Segment, 0, GuestPageProtection.Read, out _));
        Assert.Equal(Marker, *(ulong*)address);
        Assert.Equal(1, host.Log.Count(op => op == FailingHostViews.Op.CreateBacking));
        memory.Dispose();
        Assert.False(memory.TryHoldRange(address, HoleSize(host)));
        Assert.False(memory.TryMapBacked(address, Segment, 0, GuestPageProtection.Read, out _));
    }

    [Fact]
    public void CopyBetweenAliasesKeepsMoveSemanticsOnTheSharedBacking()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var first = Hold(memory, host);
        var second = Hold(memory, host);
        Assert.True(memory.TryMapBacked(first, Segment, 0, GuestPageProtection.Read, out _));
        Assert.True(memory.TryMapBacked(second, Segment, 0, GuestPageProtection.Read, out _));
        for (var i = 0; i < 0x100; i++)
        {
            ((byte*)first)[i] = (byte)i;
        }

        Assert.True(memory.TryCopy(second + 0x10, first, 0x100));
        for (var i = 0; i < 0x100; i++)
        {
            Assert.Equal((byte)i, ((byte*)second)[0x10 + i]);
        }

        Assert.True(memory.TryCopy(first, second + 0x20, 0x100));
        for (var i = 0; i < 0x100; i++)
        {
            Assert.Equal(i < 0xF0 ? (byte)(i + 0x10) : (byte)0, ((byte*)first)[i]);
        }

        var privateRange = memory.AllocateAt(0, 0x4000, false);
        Assert.NotEqual(0UL, privateRange);
        Assert.True(memory.TryCopy(privateRange, first, 0x100));
        Assert.True(memory.TryCopy(second + 0x200, privateRange, 0x100));
        Assert.Equal(((byte*)first)[7], ((byte*)second)[0x207]);
    }

    [Fact]
    public void MixedCopyWithProtectedPrivateMemoryDoesNotUpgradeUnderTheReadLock()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var view = Hold(memory, host);
        Assert.True(memory.TryMapBacked(view, Segment, 0, GuestPageProtection.Read, out _));
        for (var i = 0; i < 0x40; i++)
        {
            ((byte*)view)[i] = (byte)(0x80 + i);
        }

        var readOnly = MapPrivate(memory, host, 2 * Segment, ProgramHeaderFlags.Read);
        var noAccess = readOnly + Segment;
        memory.Map(noAccess, Segment, 0, ReadOnlySpan<byte>.Empty, (ProgramHeaderFlags)0);

        Assert.True(memory.TryCopy(readOnly, view, 0x40));
        Assert.True(memory.TryRead(readOnly, new byte[0x40]));
        Assert.True(memory.TryCompare(readOnly, new ReadOnlySpan<byte>((void*)view, 0x40)));

        Assert.True(memory.TryWrite(noAccess, BitConverter.GetBytes(Marker)));
        Assert.True(memory.TryCopy(view + 0x100, noAccess, 8));
        Assert.Equal(Marker, *(ulong*)(view + 0x100));
    }

    [Fact]
    public void CopyAcrossOverlappingDiscontinuousBackingStagesTheBytes()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var a = Hold(memory, host);
        var b = Hold(memory, host);
        Assert.True(memory.TryMapBacked(a, 2 * Segment, 0, GuestPageProtection.Read, out _));
        Assert.True(memory.TryMapBacked(b, Segment, Segment, GuestPageProtection.Read, out _));
        Assert.True(memory.TryMapBacked(b + Segment, Segment, 0, GuestPageProtection.Read, out _));
        *(ulong*)a = 1;
        *(ulong*)(a + Segment) = 2;

        Assert.True(memory.TryCopy(b, a, 2 * Segment));

        Assert.Equal(1UL, *(ulong*)b);
        Assert.Equal(2UL, *(ulong*)(b + Segment));
    }

    [Fact]
    public void SearchStaysBelowTheReferenceUpperBound()
    {
        if (!Supported) return;
        const ulong bound = 0xFC_0000_0000;
        var host = HostViewMemory.Create();
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        Assert.False(memory.TryHoldRangeAtOrAbove(bound, Segment, Segment, out _));
        Assert.False(memory.TryHoldRangeAtOrAbove(bound - Segment, 2 * Segment, Segment, out _));
        if (memory.TryHoldRangeAtOrAbove(bound - HoleSize(host), Segment, Segment, out var address))
        {
            Assert.True(address + Segment <= bound);
        }
    }

    [Fact]
    public void SearchChecksLowerAddressesBeforeReusingAHigherHole()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        var size = HoleSize(host);
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var lowerAddress = ProbeGuestAddress(host, 3 * size);
        var higherAddress = lowerAddress + 2 * size;
        Assert.True(memory.TryHoldRange(higherAddress, size));
        Assert.True(memory.TryHoldRangeAtOrAbove(lowerAddress, size, size, out var selectedAddress));
        Assert.Equal(lowerAddress, selectedAddress);
    }

    [Fact]
    public void PartialUnmapPreservesTheRemainingRegions()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        var address = Hold(memory, host);
        Assert.True(memory.TryMapBacked(address, 3 * Segment, 0, GuestPageProtection.Read, out _));
        Assert.True(memory.TryUnmapBacked(address + Segment, Segment));
        Assert.True(memory.IsBackedView(address));
        Assert.False(memory.IsBackedView(address + Segment));
        Assert.True(memory.IsBackedView(address + 2 * Segment));
        Assert.True(memory.TryMapBacked(address + Segment, Segment, Segment, GuestPageProtection.Read, out _));
        Assert.True(memory.TryWrite(address, new byte[3 * Segment]));
        Assert.True(memory.TryRead(address, new byte[3 * Segment]));
    }

    [Fact]
    public void SearchSkipsOccupiedMemoryWithoutChangingIt()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        var size = HoleSize(host);
        var candidate = ProbeGuestAddress(host, size);
        var occupied = PlatformMemory.Allocate(candidate, size, HostPageProtection.ReadWrite);

        Assert.NotEqual(0UL, occupied);
        try
        {
            *(ulong*)occupied = Marker;
            using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
            Assert.True(memory.TryHoldRangeAtOrAbove(occupied, Segment, Segment, out var found));
            Assert.True(found >= occupied + size);
            Assert.Equal(Marker, *(ulong*)occupied);
            Assert.True(memory.TryMapBacked(found, Segment, 0, GuestPageProtection.Read, out _));
        }
        finally
        {
            Assert.True(PlatformMemory.Free(occupied));
        }
    }

    [Fact]
    public void AdjacentReservationsCanSupplyOneSpanningView()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        var size = HoleSize(host);
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        ulong address = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = ProbeGuestAddress(host, 2 * size);
            if (memory.TryHoldRange(candidate, size) && memory.TryHoldRange(candidate + size, size))
            {
                address = candidate;
                break;
            }
        }

        Assert.NotEqual(0UL, address);
        Assert.True(memory.TryMapBacked(address, 2 * size, 0, GuestPageProtection.Read, out _));
        *(ulong*)(address + size) = Marker;
        Assert.True(memory.TryUnmapBacked(address, 2 * size));
        memory.Dispose();
        Assert.Equal(address, host.ReserveHole(address, 2 * size));
        Assert.True(host.FreeHole(address, 2 * size));
    }
}

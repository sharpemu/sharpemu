// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using SharpEmu.HLE.Host.Windows;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.HostViews;

[CollectionDefinition(HostViewStateCollection.Name, DisableParallelization = true)]
public sealed class HostViewStateCollection
{
    public const string Name = "HostViewState";
}

[Collection(HostViewStateCollection.Name)]
public sealed unsafe class HostViewMemoryTests
{
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;

    private static uint QueryState(ulong address)
    {
        Assert.NotEqual((nuint)0, VirtualQuery((void*)address, out var info, (nuint)sizeof(MemoryBasicInformation)));
        return info.State;
    }

    [Fact]
    public void CommitWriteRelease_RoundTripsThroughAPlaceholder()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        var hole = HoleSize(views);
        var baseAddress = ReserveFreeHole(views, hole);

        Assert.True(views.SplitHole(baseAddress, Segment));
        Assert.True(views.CommitPrivate(baseAddress, Segment, HostPageProtection.ReadWrite));
        *(ulong*)baseAddress = Marker;
        Assert.Equal(Marker, *(ulong*)baseAddress);
        Assert.True(views.ReleasePrivate(baseAddress, Segment));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(MEM_RESERVE, QueryState(baseAddress));
        }

        Assert.True(views.CommitPrivate(baseAddress, Segment, HostPageProtection.ReadWrite));
        Assert.True(views.ReleasePrivate(baseAddress, Segment));
        Assert.True(views.JoinHoles(baseAddress, hole));
        Assert.True(views.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void SubGranularityView_SharesBytesWithTheAlias()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        using var backing = CreateBacking(views);
        var hole = HoleSize(views);
        var baseAddress = ReserveFreeHole(views, hole);
        var viewAddress = baseAddress + Segment;

        Assert.True(views.SplitHole(baseAddress, Segment));
        Assert.True(views.SplitHole(viewAddress, Segment));
        Assert.True(views.TryMapView(backing, viewAddress, Segment, Segment, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.None, failure);

        *(ulong*)viewAddress = Marker;
        Assert.Equal(Marker, *(ulong*)(backing.AliasBase + Segment));
        *(ulong*)(backing.AliasBase + Segment + 8) = ~Marker;
        Assert.Equal(~Marker, *(ulong*)(viewAddress + 8));
        new Span<byte>((void*)viewAddress, (int)Segment).Clear();
        Assert.Equal(0UL, *(ulong*)(backing.AliasBase + Segment));

        Assert.True(views.UnmapView(viewAddress, Segment));
        Assert.True(views.JoinHoles(baseAddress, hole));
        Assert.True(views.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void AliasWrites_AreVisibleThroughANoAccessThenReadOnlyView()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        using var backing = CreateBacking(views);
        var hole = HoleSize(views);
        var baseAddress = ReserveFreeHole(views, hole);

        Assert.True(views.SplitHole(baseAddress, Segment));
        Assert.True(views.TryMapView(backing, baseAddress, 0, Segment, HostPageProtection.NoAccess, out _));
        *(ulong*)backing.AliasBase = Marker;
        Assert.True(views.UnmapView(baseAddress, Segment));
        Assert.True(views.TryMapView(backing, baseAddress, 0, Segment, HostPageProtection.ReadOnly, out _));
        Assert.Equal(Marker, *(ulong*)baseAddress);

        Assert.True(views.UnmapView(baseAddress, Segment));
        Assert.True(views.JoinHoles(baseAddress, hole));
        Assert.True(views.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void ChangeAccess_ReadOnlyViewStillSeesAliasWrites()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        using var backing = CreateBacking(views);
        var hole = HoleSize(views);
        var baseAddress = ReserveFreeHole(views, hole);

        Assert.True(views.SplitHole(baseAddress, Segment));
        Assert.True(views.TryMapView(backing, baseAddress, 0, Segment, HostPageProtection.ReadWrite, out _));
        Assert.True(views.ChangeAccess(baseAddress, Segment, HostPageProtection.ReadOnly));
        *(ulong*)backing.AliasBase = Marker;
        Assert.Equal(Marker, *(ulong*)baseAddress);
        Assert.True(views.ChangeAccess(baseAddress, Segment, HostPageProtection.ReadWrite));
        *(ulong*)baseAddress = ~Marker;
        Assert.Equal(~Marker, *(ulong*)backing.AliasBase);

        Assert.True(views.UnmapView(baseAddress, Segment));
        Assert.True(views.JoinHoles(baseAddress, hole));
        Assert.True(views.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void TwoViewsOfOneOffset_ShareBytesAndSurviveEachOther()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        using var backing = CreateBacking(views);
        var hole = HoleSize(views);
        var first = ReserveFreeHole(views, hole);
        var second = ReserveFreeHole(views, hole);

        Assert.True(views.SplitHole(first, Segment));
        Assert.True(views.SplitHole(second, Segment));
        Assert.True(views.TryMapView(backing, first, 0, Segment, HostPageProtection.ReadWrite, out _));
        Assert.True(views.TryMapView(backing, second, 0, Segment, HostPageProtection.ReadWrite, out _));

        *(ulong*)first = Marker;
        Assert.Equal(Marker, *(ulong*)second);
        Assert.True(views.UnmapView(first, Segment));
        Assert.Equal(Marker, *(ulong*)second);

        Assert.True(views.UnmapView(second, Segment));
        Assert.True(views.JoinHoles(first, hole));
        Assert.True(views.JoinHoles(second, hole));
        Assert.True(views.FreeHole(first, hole));
        Assert.True(views.FreeHole(second, hole));
    }

    [Fact]
    public void OccupiedAddress_IsRejectedByWindowsPlaceholders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var views = HostViewMemory.Create();
        using var backing = CreateBacking(views);
        var memory = PlatformMemory;
        var hole = HoleSize(views);
        var probe = ProbeFreeAddress(views, hole);
        var occupied = memory.Allocate(probe, hole, HostPageProtection.ReadWrite);
        Assert.Equal(probe, occupied);
        *(ulong*)occupied = Marker;

        Assert.Equal(0UL, views.ReserveHole(occupied, hole));
        Assert.False(views.TryMapView(backing, occupied, 0, Segment, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.PlaceholderMapFailed, failure);
        Assert.Equal(Marker, *(ulong*)occupied);
        Assert.Equal(MEM_COMMIT, QueryState(occupied));

        Assert.True(memory.Free(occupied));
    }

    [Fact]
    public void ProtectFailure_PreservesThePlaceholderOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var views = HostViewMemory.Create();
        using var backing = CreateBacking(views);
        var hole = HoleSize(views);
        var baseAddress = ReserveFreeHole(views, hole);

        Assert.True(views.SplitHole(baseAddress, Segment));
        WindowsHostViews.FailProtectForTests = true;
        try
        {
            Assert.False(views.TryMapView(backing, baseAddress, 0, Segment, HostPageProtection.ReadWrite, out var failure));
            Assert.Equal(HostViewFailure.ProtectFailed, failure);
        }
        finally
        {
            WindowsHostViews.FailProtectForTests = false;
        }

        Assert.Equal(MEM_RESERVE, QueryState(baseAddress));
        Assert.True(views.TryMapView(backing, baseAddress, 0, Segment, HostPageProtection.ReadWrite, out _));

        Assert.True(views.UnmapView(baseAddress, Segment));
        Assert.True(views.JoinHoles(baseAddress, hole));
        Assert.True(views.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void InvalidRanges_AreRejectedBeforeAnyHostCall()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        Assert.False(views.TryCreateBacking(0, out var none, out var createFailure));
        Assert.Null(none);
        Assert.Equal(HostViewFailure.BackingUnavailable, createFailure);

        using var backing = CreateBacking(views);
        var hole = HoleSize(views);
        var baseAddress = ReserveFreeHole(views, hole);
        var page = views.PageSize;

        Assert.Equal(HostViewFailure.OffsetOutOfBounds, MapFailure(views, backing, baseAddress, 0, 0));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, MapFailure(views, backing, baseAddress, BackingSize, page));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, MapFailure(views, backing, baseAddress, 0, BackingSize + page));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, MapFailure(views, backing, baseAddress, BackingSize - page, 2 * page));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, MapFailure(views, backing, baseAddress, ulong.MaxValue - 0xFFF, 2 * page));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, MapFailure(views, backing, baseAddress, 0x800, page));
        Assert.Equal(HostViewFailure.WrongHostAddress, MapFailure(views, backing, baseAddress + 1, 0, page));
        Assert.Equal(HostViewFailure.WrongHostAddress, MapFailure(views, backing, 0, 0, page));
        Assert.Equal(0UL, views.ReserveHole(0, hole));
        Assert.Equal(0UL, views.ReserveHole(ulong.MaxValue - 0xFFF, 0x10000));

        Assert.True(views.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void InjectedAliasFailure_ReleasesThePartialObject()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        SetAliasFailure(true);
        try
        {
            Assert.False(views.TryCreateBacking(BackingSize, out var backing, out var failure));
            Assert.Null(backing);
            Assert.Equal(HostViewFailure.BackingUnavailable, failure);
        }
        finally
        {
            SetAliasFailure(false);
        }

        using var recovered = CreateBacking(views);
        Assert.Equal(BackingSize, recovered.Size);
    }

    [Fact]
    public void Dispose_IsIdempotentAndBlocksLaterMaps()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        var backing = CreateBacking(views);
        var hole = HoleSize(views);
        var baseAddress = ReserveFreeHole(views, hole);

        Assert.True(views.SplitHole(baseAddress, Segment));
        Assert.True(views.TryMapView(backing, baseAddress, 0, Segment, HostPageProtection.ReadWrite, out _));
        *(ulong*)baseAddress = Marker;
        Assert.Equal(Marker, *(ulong*)backing.AliasBase);
        Assert.True(views.UnmapView(baseAddress, Segment));

        backing.Dispose();
        backing.Dispose();
        Assert.False(views.TryMapView(backing, baseAddress, 0, Segment, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.BackingUnavailable, failure);

        Assert.True(views.JoinHoles(baseAddress, hole));
        Assert.True(views.FreeHole(baseAddress, hole));
    }

    private static HostViewFailure MapFailure(IHostViewMemory views, HostBackingObject backing, ulong address, ulong offset, ulong size)
    {
        Assert.False(views.TryMapView(backing, address, offset, size, HostPageProtection.ReadWrite, out var failure));
        return failure;
    }

    private static void SetAliasFailure(bool fail)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsHostViews.FailAliasMapForTests = fail;
        }
        else
        {
            PosixHostViews.FailAliasMapForTests = fail;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [Fact]
    public void ReserveFreeAddressRanges_SkipsOccupiedHostRanges()
    {
        if (!Supported)
        {
            return;
        }

        var views = HostViewMemory.Create();
        const ulong span = 0x400_0000;
        const ulong minimum = 0x10_0000;
        var start = ProbeGuestAddress(views, span);
        var blocker = start + span / 4;
        var blockerSize = AlignUp(minimum, views.Granularity);
        Assert.Equal(blocker, views.ReserveHole(blocker, blockerSize));

        var ranges = views.ReserveFreeAddressRanges(start, start + span, minimum);
        try
        {
            Assert.NotEmpty(ranges);
            Assert.Equal(start, ranges[0].Address);
            Assert.All(ranges, range => Assert.True(
                range.Address + range.Size <= blocker || range.Address >= blocker + blockerSize));
            var reserved = ranges.Aggregate(0UL, (total, range) => total + range.Size);
            Assert.True(reserved >= span - blockerSize - 2 * minimum, $"Only 0x{reserved:X} bytes were reserved.");
        }
        finally
        {
            foreach (var range in ranges)
            {
                Assert.True(views.FreeHole(range.Address, range.Size));
            }

            Assert.True(views.FreeHole(blocker, blockerSize));
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(void* address, out MemoryBasicInformation info, nuint length);
}

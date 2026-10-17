// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class WriteTickMapTests
{
    [Fact]
    public void ARangeReportsOnlyTheWritesThatReachIt()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x100, 5);
        map.Assign(0x800, 0x100, 9);

        Assert.Equal(5UL, map.MaxTick(0x000, 0x100));
        Assert.Equal(9UL, map.MaxTick(0x880, 0x10));
        Assert.Equal(0UL, map.MaxTick(0x200, 0x400));
        Assert.Equal(9UL, map.MaxTick(0x000, 0x1000));
    }

    [Fact]
    public void ANewerWriteSplitsTheOlderRange()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x300, 3);
        map.Assign(0x100, 0x100, 7);

        Assert.Equal(3, map.Count);
        Assert.Equal(3UL, map.MaxTick(0x000, 0x100));
        Assert.Equal(7UL, map.MaxTick(0x180, 0x8));
        Assert.Equal(3UL, map.MaxTick(0x200, 0x100));
    }

    [Fact]
    public void AdjacentWritesOfOneTickMerge()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x100, 4);
        map.Assign(0x200, 0x100, 4);
        map.Assign(0x100, 0x100, 4);

        Assert.Equal(1, map.Count);
        Assert.Equal(4UL, map.MaxTick(0x2FF, 1));
    }

    [Fact]
    public void PrunedWritesReadAsTheFloor()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x100, 2);
        map.Assign(0x100, 0x100, 6);
        map.Prune(5);

        Assert.Equal(1, map.Count);
        Assert.Equal(2UL, map.Floor);
        Assert.Equal(2UL, map.MaxTick(0x400, 0x100));
        Assert.Equal(6UL, map.MaxTick(0x100, 0x100));
    }
}

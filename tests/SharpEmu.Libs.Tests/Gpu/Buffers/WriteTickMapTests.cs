// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class WriteTickMapTests
{
    private const ulong Chunk = 4UL * 1024 * 1024;

    [Fact]
    public void ARangeReportsOnlyTheWritesThatReachItsGranules()
    {
        var map = new WriteTickMap();
        map.Assign(0x0000, 0x1000, 5);
        map.Assign(0x8000, 0x1000, 9);

        Assert.Equal(5UL, map.MaxTick(0x0000, 0x1000));
        Assert.Equal(9UL, map.MaxTick(0x8800, 0x10));
        Assert.Equal(0UL, map.MaxTick(0x2000, 0x4000));
        Assert.Equal(9UL, map.MaxTick(0x0000, 0x10000));
    }

    [Fact]
    public void AWriteElsewhereInTheSameGranuleReachesTheWholeGranule()
    {
        var map = new WriteTickMap();
        map.Assign(0x000, 0x100, 3);
        map.Assign(0x800, 0x10, 7);

        Assert.Equal(7UL, map.MaxTick(0x000, 0x100));
        Assert.Equal(0UL, map.MaxTick(0x1000, 0x100));
    }

    [Fact]
    public void AWholeChunkWriteCoversItsEarlierPartialWrites()
    {
        var map = new WriteTickMap();
        map.Assign(0x1000, 0x1000, 4);
        map.Assign(0, Chunk, 6);
        Assert.Equal(6UL, map.MaxTick(0x1000, 1));
        Assert.Equal(6UL, map.MaxTick(0x5000, 1));

        map.Assign(0x3000, 0x1000, 8);
        Assert.Equal(6UL, map.MaxTick(0x1000, 1));
        Assert.Equal(8UL, map.MaxTick(0x3000, 1));
        Assert.Equal(8UL, map.MaxTick(0, Chunk));
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void AnOlderPartialWriteDoesNotLowerAUniformChunk()
    {
        var map = new WriteTickMap();
        map.Assign(0, Chunk, 9);
        map.Assign(0x1000, 0x1000, 4);

        Assert.Equal(9UL, map.MaxTick(0x1000, 1));
        Assert.Equal(9UL, map.MaxTick(0x2000, 1));
    }

    [Fact]
    public void AnOlderPartialWriteKeepsTheNewerGranules()
    {
        var map = new WriteTickMap();
        map.Assign(0x1000, 0x2000, 9);
        map.Assign(0x0000, 0x4000, 4);

        Assert.Equal(4UL, map.MaxTick(0x0000, 1));
        Assert.Equal(9UL, map.MaxTick(0x1000, 1));
        Assert.Equal(9UL, map.MaxTick(0x2000, 1));
        Assert.Equal(4UL, map.MaxTick(0x3000, 1));
    }

    [Fact]
    public void ReadsAndWritesCrossChunkBoundaries()
    {
        var map = new WriteTickMap();
        map.Assign(Chunk - 0x1000, 0x2000, 5);
        Assert.Equal(5UL, map.MaxTick(Chunk + 0x800, 1));
        Assert.Equal(5UL, map.MaxTick(Chunk - 0x10, 0x20));
        Assert.Equal(0UL, map.MaxTick(0, Chunk - 0x1000));
        Assert.Equal(2, map.Count);

        map.Assign(2 * Chunk + 0x10000, 0x1000, 9);
        Assert.Equal(9UL, map.MaxTick(0, 4 * Chunk));
        Assert.Equal(5UL, map.MaxTick(0, 2 * Chunk));
        Assert.Equal(0UL, map.MaxTick(8 * Chunk, Chunk));
        Assert.Equal(3, map.Count);

        map.Assign(0, 3 * Chunk, 11);
        Assert.Equal(11UL, map.MaxTick(2 * Chunk + 0x10000, 1));
        Assert.Equal(11UL, map.MaxTick(Chunk + Chunk / 2, 1));
    }

    [Fact]
    public void PrunedChunksReadAsTheFloor()
    {
        var map = new WriteTickMap();
        map.Assign(0x0000, 0x1000, 2);
        map.Assign(Chunk, 0x1000, 6);
        map.Prune(5);

        Assert.Equal(1, map.Count);
        Assert.Equal(2UL, map.Floor);
        Assert.Equal(2UL, map.MaxTick(0x0000, 0x1000));
        Assert.Equal(2UL, map.MaxTick(Chunk + 0x4000, 0x100));
        Assert.Equal(6UL, map.MaxTick(Chunk, 0x1000));
        Assert.Equal(2UL, map.MaxTick(16 * Chunk, 0x100));
    }
}

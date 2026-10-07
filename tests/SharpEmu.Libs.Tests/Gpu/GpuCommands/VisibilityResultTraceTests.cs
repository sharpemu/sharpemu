// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class VisibilityResultTraceTests
{
    [Fact]
    public void AggregatePublication_StoresTheHostSumInBlockZeroOnly()
    {
        const ulong address = 0x2000;
        const ulong value = (1UL << 63) | 17;
        var data = new byte[16 * 16];
        for (var block = 0; block < 16; block++)
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(block * 16), block == 0 ? value : 1UL << 63);
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(address, value, address, data, aggregate: true));
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), value);
        Assert.Equal(2u, VisibilityResultTrace.DifferentSlots(address, value, address, data, aggregate: true));
    }

    [Fact]
    public void DifferentSlots_ChecksAllDepthBlocksButNotTheOtherCounter()
    {
        var data = new byte[256];
        const ulong expected = (1UL << 63) | 17;
        for (var slot = 0; slot < 16; slot++)
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(slot * 16), expected);
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data));
        data[240] = 0;
        Assert.Equal(1u << 15, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data));
    }

    [Fact]
    public void DifferentSlots_RequiresACompleteOverlappingQword()
    {
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x1001, new byte[7]));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x1008, new byte[8]));
        Assert.Equal(2u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x100F, new byte[9]));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x1100, new byte[256]));
    }

    [Fact]
    public void DifferentSlots_DoesNotWrapAnAddress()
    {
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(ulong.MaxValue - 15, 1, 0, new byte[256]));
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(8u)]
    [InlineData(16u)]
    [InlineData(32u)]
    public void DifferentSlots_ChecksOnlyEnabledInstancesAtTheSelectedStride(uint strideBytes)
    {
        const ulong expected = (1UL << 63) | 17;
        const uint mask = (1u << 2) | (1u << 15) | (1u << 23);
        var data = new byte[24 * strideBytes + 8];
        Array.Fill(data, (byte)0xCD);
        foreach (var slot in new[] { 2, 15, 23 })
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(slot * (int)strideBytes), expected);
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data, mask, strideBytes));
        data[23 * strideBytes] = 0;
        Assert.Equal(1u << 23, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data, mask, strideBytes));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data, 0, strideBytes));
    }

    [Fact]
    public void DifferentSlots_RequiresTheCompleteSelectedResultInAPartialDownload()
    {
        const uint mask = 1u << 23;
        Assert.Equal(mask, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x12DF, new byte[9], mask, 32));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x12DF, new byte[8], mask, 32));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x12E1, new byte[8], mask, 32));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x1010, new byte[8], mask, 32));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(ulong.MaxValue - 3, 1, ulong.MaxValue - 3, new byte[8], 1, 4));
    }

    [Fact]
    public void DifferentSlots_UsesTheFinalBytesForOverlappingResults()
    {
        const ulong expected = (1UL << 63) | 17;
        const uint mask = (1u << 0) | (1u << 1) | (1u << 3);
        var data = new byte[20];
        foreach (var slot in new[] { 0, 1, 3 })
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(slot * 4), expected);
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data, mask, 4));
        data[4] = 0;
        Assert.Equal(3u, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data, mask, 4));
    }

    [Fact]
    public void ReplacedSlots_ChecksOnlyChangedResultsThatRemainPublished()
    {
        const ulong expected = (1UL << 63) | 17;
        const uint mask = (1u << 2) | (1u << 15) | (1u << 23);
        var reads = new List<ulong>();
        bool Read(ulong address, Span<byte> destination)
        {
            reads.Add(address);
            BinaryPrimitives.WriteUInt64LittleEndian(destination, address == 0x12E0 ? expected : 99);
            return address != 0x1040;
        }
        Assert.Equal(1u << 23, VisibilityResultTrace.ReplacedSlots(0x1000, expected, 0x1000, new byte[768], mask, 32, Read));
        Assert.Equal(new ulong[] { 0x1040, 0x11E0, 0x12E0 }, reads);
        reads.Clear();
        Assert.Equal(0u, VisibilityResultTrace.ReplacedSlots(0x1000, expected, 0x1010, new byte[8], mask, 32, Read));
        Assert.Empty(reads);
    }

    [Fact]
    public void ReplacedSlots_UsesTheFinalBytesForOverlappingResults()
    {
        const ulong expected = (1UL << 63) | 17;
        var backing = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(backing, expected);
        BinaryPrimitives.WriteUInt64LittleEndian(backing.AsSpan(4), expected);
        bool Read(ulong address, Span<byte> destination)
        {
            backing.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }
        Assert.Equal(3u, VisibilityResultTrace.ReplacedSlots(0x1000, expected, 0x1000, new byte[12], 3, 4, Read));
        Assert.Equal(0u, VisibilityResultTrace.ReplacedSlots(0x1000, expected, 0x1000, backing, 3, 4, Read));
    }
}

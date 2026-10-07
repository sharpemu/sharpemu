// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class OcclusionResultBlockTests
{
    private static ulong Slot(byte[] block, int depthBlock, bool end) =>
        BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(depthBlock * 16 + (end ? 8 : 0)));

    // What the guest computes: the sum of end - begin over every ready depth block.
    private static ulong GuestSum(byte[] block)
    {
        ulong sum = 0;
        for (var depthBlock = 0; depthBlock < OcclusionResultBlock.DepthBlocks; depthBlock++)
        {
            var begin = Slot(block, depthBlock, end: false);
            var end = Slot(block, depthBlock, end: true);
            Assert.NotEqual(0UL, begin & OcclusionResultBlock.ReadyBit);
            Assert.NotEqual(0UL, end & OcclusionResultBlock.ReadyBit);
            sum += (end & ~OcclusionResultBlock.ReadyBit) - (begin & ~OcclusionResultBlock.ReadyBit);
        }

        return sum;
    }

    [Fact]
    public void SegmentsAddUpThroughTheGuestSum()
    {
        var block = OcclusionResultBlock.Build(3, false, [10, 0, 32]);

        Assert.Equal(42UL, GuestSum(block));
        Assert.Equal(OcclusionResultBlock.ReadyBit | 10, Slot(block, 0, end: true));
        Assert.Equal(OcclusionResultBlock.ReadyBit | 32, Slot(block, 2, end: true));
    }

    [Fact]
    public void AQueryWithoutDrawsReportsZeroSamples()
    {
        Assert.Equal(0UL, GuestSum(OcclusionResultBlock.Build(0, false, [])));
    }

    [Fact]
    public void AnUncountedQueryStaysVisible()
    {
        Assert.Equal(OcclusionResultBlock.ConservativeCount, GuestSum(OcclusionResultBlock.Build(0, true, [])));
    }

    [Fact]
    public void MoreSegmentsThanDepthBlocksAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OcclusionResultBlock.Build(17, false, new ulong[17]));
    }
}

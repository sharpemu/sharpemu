// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class GuestBufferWriteTimelineTests
{
    [Fact]
    public void SignalPredictionExpiresOnSubmissionOrAnOverlappingWriter()
    {
        var writes = new GuestBufferWriteTimeline();
        writes.RecordSignal(0x1000, 8, 3, 0x1122334455667788);
        writes.Record(0x1010, 8, 3);
        Assert.True(writes.TryReadSignal(0x1000, 8, 3, out var wide));
        Assert.Equal(0x1122334455667788UL, wide);
        Assert.True(writes.TryReadSignal(0x1000, 4, 3, out var narrow));
        Assert.Equal(0x55667788UL, narrow);
        Assert.False(writes.TryReadSignal(0x1000, 8, 4, out _));
        writes.Record(0x1000, 8, 3);
        Assert.False(writes.TryReadSignal(0x1000, 8, 3, out _));
        writes.RecordSignal(0x1000, 8, 3, 7);
        writes.Record(0x1004, 4, 3);
        Assert.False(writes.TryReadSignal(0x1000, 8, 3, out _));
        Assert.Equal(3UL, writes.LastWriter(0x1000, 8));
        writes.RecordSignal(0x1000, 4, 3, 9);
        Assert.False(writes.TryReadSignal(0x1000, 8, 3, out _));
        writes.RetireThrough(3);
        Assert.False(writes.TryReadSignal(0x1000, 4, 3, out _));
    }

    [Fact]
    public void NeighboursAndPartialOverwritesRetainIndependentWriters()
    {
        var writes = new GuestBufferWriteTimeline();
        writes.Record(0x1000, 8, 3);
        writes.Record(0x1010, 8, 99);
        Assert.Equal(3UL, writes.LastWriter(0x1000, 8));
        Assert.Equal(0UL, writes.LastWriter(0x1008, 8));
        writes.Record(0x1002, 4, 7);
        Assert.Equal(3UL, writes.LastWriter(0x1000, 2));
        Assert.Equal(7UL, writes.LastWriter(0x1002, 4));
        Assert.Equal(3UL, writes.LastWriter(0x1006, 2));
        writes.Record(0x1001, 0x15, 0);
        Assert.Equal(3UL, writes.LastWriter(0x1000, 1));
        Assert.Equal(0UL, writes.LastWriter(0x1001, 0x15));
        Assert.Equal(99UL, writes.LastWriter(0x1016, 2));
    }

    [Fact]
    public void RetiringOldWritesRetainsNewerOverlappingAndNeighbourWrites()
    {
        var writes = new GuestBufferWriteTimeline();
        writes.Record(0x1000, 8, 3);
        writes.Record(0x1004, 8, 5);
        writes.Record(0x1010, 8, 3);
        writes.RetireThrough(3);
        Assert.Equal(0UL, writes.LastWriter(0x1000, 4));
        Assert.Equal(5UL, writes.LastWriter(0x1004, 8));
        Assert.Equal(0UL, writes.LastWriter(0x1010, 8));
    }

    [Fact]
    public void RandomOverwritesMatchByteReferenceIncludingUnmaps()
    {
        var writes = new GuestBufferWriteTimeline();
        var expected = new ulong[128];
        var random = new System.Random(643);
        for (ulong tick = 1; tick <= 3000; tick++)
        {
            var begin = random.Next(128);
            var count = random.Next(1, 129 - begin);
            var value = tick % 5 == 0 ? 0 : tick;
            writes.Record((ulong)begin, (ulong)count, value);
            Array.Fill(expected, value, begin, count);
            begin = random.Next(128);
            count = random.Next(1, 129 - begin);
            Assert.Equal(expected.Skip(begin).Take(count).Max(), writes.LastWriter((ulong)begin, (ulong)count));
        }
    }
}

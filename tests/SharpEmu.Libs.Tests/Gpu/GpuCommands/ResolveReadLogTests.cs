// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class ResolveReadLogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedReadStorageRemainsValidUntilRetirement(bool compact)
    {
        var log = new ResolveReadLog(compact);
        var initial = log.CapacityBytes;
        log.Add(0x100, new byte[1 << 20], clean: true);
        for (uint i = 0; i < 16384; i++) log.Add(0x200000UL + 8UL * i, i);
        var highWater = log.CapacityBytes;
        Assert.True(highWater > 1L << 20);
        Assert.Equal(1 << 20, log.Bytes(0, out _).Length);
        Assert.Equal(16385, log.Count);
        // Clearing a still-owned log must retain its metadata for the next validation.
        log.Clear();
        var beforeReturn = log.CapacityBytes;
        log.Add(0x100, new byte[1 << 20], clean: true);
        log.ClearForPool();
        Assert.Equal(compact ? initial : beforeReturn + (1 << 20) - 2048, log.CapacityBytes);
        log.Add(7, 0x11223344);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, log.Bytes(0, out var address).ToArray());
        Assert.Equal(7UL, address);
        Assert.Equal(1, log.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesLiveReadsWithoutPropagatingTheTemplatesHighWaterMark(bool compact)
    {
        var template = new ResolveReadLog(false);
        for (uint i = 0; i < 16384; i++) template.Add(i * 8UL, i);
        template.Clear();
        for (uint i = 0; i < 65; i++) template.Add(i * 8UL, i);
        template.Add(0x1000, new byte[2049], clean: true);
        var target = new ResolveReadLog(compact);
        target.CopyFrom(template);
        Assert.Equal(compact, target.CapacityBytes < template.CapacityBytes);
        Assert.Equal(template.Count, target.Count);
        for (var i = 0; i < target.Count; i++)
        {
            Assert.True(template.Bytes(i, out var source).SequenceEqual(target.Bytes(i, out var copied)));
            Assert.Equal(source, copied);
            Assert.Equal(template.IsClean(i), target.IsClean(i));
        }
    }

    [Fact]
    public void KeepsEveryReadWithItsAddressAndGrowsPastItsInitialCapacity()
    {
        var log = new ResolveReadLog();
        for (var index = 0; index < 300; index++)
        {
            var bytes = new byte[(index % 13) + 1];
            Array.Fill(bytes, (byte)index);
            log.Add(0x1000 + (ulong)index * 64, bytes);
        }

        log.Add(0x9000, 0x11223344u);
        Assert.Equal(301, log.Count);
        for (var index = 0; index < 300; index++)
        {
            var bytes = log.Bytes(index, out var address);
            Assert.Equal(0x1000 + (ulong)index * 64, address);
            Assert.Equal((index % 13) + 1, bytes.Length);
            Assert.All(bytes.ToArray(), value => Assert.Equal((byte)index, value));
        }

        var word = log.Bytes(300, out var wordAddress);
        Assert.Equal(0x9000UL, wordAddress);
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, word.ToArray());
    }

    // A clean read is checked again at the take with the command thread's clean test, so it never merges with a plain read
    // next to it; reads of one class still merge.
    [Fact]
    public void AdjacentReadsMergeOnlyWithinTheirClass()
    {
        var log = new ResolveReadLog();
        log.Add(0x1000, new byte[] { 1, 2, 3, 4 }, clean: true);
        log.Add(0x1004, new byte[] { 5, 6, 7, 8 }, clean: true);
        log.Add(0x1008, new byte[] { 9, 10, 11, 12 });
        log.Add(0x100C, new byte[] { 13, 14, 15, 16 });

        Assert.Equal(2, log.Count);
        Assert.True(log.IsClean(0));
        Assert.False(log.IsClean(1));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, log.Bytes(0, out var clean).ToArray());
        Assert.Equal(0x1000UL, clean);
        Assert.Equal(new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 }, log.Bytes(1, out var plain).ToArray());
        Assert.Equal(0x1008UL, plain);
    }

    [Fact]
    public void AdjacentReadsMergeIntoOneRange()
    {
        var log = new ResolveReadLog();
        log.Add(0x1000, 1u);
        log.Add(0x1004, 2u);
        log.Add(0x1008, new byte[] { 9, 9 });
        log.Add(0x2000, 3u);
        log.Add(0x1010, 4u);

        Assert.Equal(3, log.Count);
        var merged = log.Bytes(0, out var address);
        Assert.Equal(0x1000UL, address);
        Assert.Equal(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 9, 9 }, merged.ToArray());
        log.Bytes(1, out address);
        Assert.Equal(0x2000UL, address);
        log.Bytes(2, out address);
        Assert.Equal(0x1010UL, address);
    }

    [Fact]
    public void ClearForgetsTheReads()
    {
        var log = new ResolveReadLog();
        log.Add(0x1000, new byte[16]);
        log.Clear();
        Assert.Equal(0, log.Count);
        log.Add(0x2000, 5u);
        Assert.Equal(1, log.Count);
        log.Bytes(0, out var address);
        Assert.Equal(0x2000UL, address);
    }

    [Fact]
    public void PendingWritesTakeMarksFromSeveralThreadsAtOnce()
    {
        var pending = new PendingGpuWrites();
        var threads = new Thread[4];
        for (var thread = 0; thread < threads.Length; thread++)
        {
            var origin = 0x10_0000_0000UL * (ulong)(thread + 1);
            threads[thread] = new Thread(() =>
            {
                for (var index = 0; index < 2000; index++)
                {
                    pending.Mark(origin + (ulong)index * 0x100, 0x10, sequence: index + 1);
                }
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        for (var thread = 0; thread < threads.Length; thread++)
        {
            var origin = 0x10_0000_0000UL * (ulong)(thread + 1);
            Assert.True(pending.TryGetPending(origin + 1999 * 0x100, 4, out var sequence));
            Assert.Equal(2000, sequence);
            Assert.False(pending.TryGetPending(origin + 1999 * 0x100 + 0x20, 4, out _));
        }
    }
}

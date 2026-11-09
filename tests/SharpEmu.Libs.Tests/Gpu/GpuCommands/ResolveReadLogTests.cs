// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class ResolveReadLogTests
{
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

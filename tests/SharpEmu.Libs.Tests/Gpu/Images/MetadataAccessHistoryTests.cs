// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class MetadataAccessHistoryTests
{
    [Fact]
    public void RangeEvents_RecordOverlapWithoutChangingAdjacentHistories()
    {
        var history = new MetadataAccessHistory();
        history.Record(0x1000, 0x100, Entry(1, "bind-depth", 0x1000, 0x100));
        history.Record(0x2000, 0, Entry(2, "bind-color", 0x2000, 0));
        history.RecordRange(Entry(3, "gpu-write", 0x1080, 0x80));
        history.RecordRange(Entry(4, "unmap", 0x1fff, 2));

        Assert.Equal(new ulong[] { 1, 3 }, history.Read(0x1000).Select(entry => entry.Sequence));
        Assert.Equal(new ulong[] { 2, 4 }, history.Read(0x2000).Select(entry => entry.Sequence));
        history.RecordRange(Entry(5, "invalid", ulong.MaxValue, 2));
        Assert.Equal(2, history.Read(0x2000).Count());
    }

    [Fact]
    public void RepeatedBindings_KeepLatestSubmissionAndPreserveEarlierClear()
    {
        var history = new MetadataAccessHistory();
        history.Record(0x1000, 0x100, Entry(1, "clear", 0x1000, 0x100));
        for (ulong sequence = 2; sequence < 100; sequence++)
        {
            history.Record(0x1000, 0x100, Entry(sequence, "bind-depth", 0x1000, 0x100));
        }

        var entries = history.Read(0x1000).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(99UL, entries[1].SubmissionId);
    }

    [Fact]
    public void CapacityLimits_EvictOldestAddressesAndEventsInOrder()
    {
        var history = new MetadataAccessHistory();
        for (ulong sequence = 1; sequence <= 40; sequence++)
        {
            history.Record(0x1000, 1, Entry(sequence, "write", 0x1000, sequence));
        }
        Assert.Equal(Enumerable.Range(9, 32).Select(value => (ulong)value),
            history.Read(0x1000).Select(entry => entry.Sequence));
        for (ulong address = 0x2000; address < 0x2000 + MetadataAccessHistory.AddressCapacity; address++)
        {
            history.Record(address, 1, Entry(address, "bind", address, 1));
        }
        Assert.Empty(history.Read(0x1000));
        Assert.Single(history.Read(0x2000));
    }

    private static MetadataAccessEntry Entry(ulong sequence, string operation, ulong address, ulong size) =>
        new(sequence, sequence, 0, sequence, operation, address, size, 0, "test", 0);
}

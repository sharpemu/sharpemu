// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class ResourceAccessHistoryTests
{
    private static ResourceHistoryEntry Entry(ulong sequence, string operation, ulong address = 0x1000, ulong size = 16)
        => new(sequence, 1, 0, 1, operation, address, size, default, default, ImageRole.Texture);

    [Fact]
    public void WriteFlood_DoesNotEvictLifecycleOrBindings()
    {
        var history = new ResourceAccessHistory();
        history.Record(Entry(0, "create"));
        history.Record(Entry(1, "color-acquire"));
        history.Record(Entry(2, "gpu-buffer-write-notification"));
        history.Record(Entry(3, "buffer-fill"));
        for (ulong sequence = 4; sequence < 1_900_004; sequence++)
            history.Record(Entry(sequence, "cpu-write-notification", (sequence % 8192) * 4096));
        Assert.Equal("create", Assert.Single(history.Lifecycle.Read()).Operation);
        Assert.Equal("color-acquire", Assert.Single(history.Bindings.Read()).Operation);
        Assert.Equal(4096, history.Writes.Count);
        Assert.Equal(1_900_000ul - 4096, history.Writes.Dropped);
        Assert.Equal(1_900_003ul, history.Writes.Read().Last().Sequence);
        Assert.Equal(new[] { "gpu-buffer-write-notification", "buffer-fill" },
            history.GpuWrites.Read().Select(entry => entry.Operation));
        Assert.Equal(0ul, history.GpuWrites.Dropped);
    }

    [Fact]
    public void CoalescedWrites_PreserveEnvelopeAndSequenceBoundaries()
    {
        var history = new ResourceAccessHistory();
        history.Record(Entry(0, "cpu-write-notification", 0x1080));
        history.Record(Entry(1, "cpu-write-notification", 0x1000));
        var merged = Assert.Single(history.Writes.Read());
        Assert.Equal(0x1000ul, merged.Address);
        Assert.Equal(0x90ul, merged.Size);
        Assert.Equal(0ul, merged.Sequence);
        Assert.Equal(1ul, merged.LastSequence);
        Assert.Equal(2ul, merged.Notifications);
        history.Record(Entry(2, "create"));
        history.Record(Entry(3, "cpu-write-notification"));
        history.Record(Entry(4, "cpu-write-notification") with { Submission = 2 });
        history.Record(Entry(5, "gpu-buffer-write-notification") with { Submission = 2 });
        history.Record(Entry(6, "gpu-buffer-write-notification", 0x2000) with { Submission = 2 });
        Assert.Equal(3, history.Writes.Count);
        Assert.Equal(2, history.GpuWrites.Count);
        Assert.Equal(1ul, history.Writes.Coalesced);
    }

    [Fact]
    public void RingWrap_CountsAllDroppedNotifications()
    {
        var ring = new ResourceAccessHistory.Ring(2);
        ring.Add(Entry(0, "cpu-write-notification"), true);
        ring.Add(Entry(1, "cpu-write-notification"), true);
        ring.Add(Entry(2, "cpu-write-notification", 0x2000), true);
        ring.Add(Entry(3, "cpu-write-notification", 0x3000), true);
        Assert.Equal(2ul, ring.Dropped);
        Assert.Equal(new ulong[] { 2, 3 }, ring.Read().Select(entry => entry.Sequence));
    }

    [Fact]
    public void InvalidRanges_DoNotEnterHistory()
    {
        var history = new ResourceAccessHistory();
        history.Record(Entry(0, "create", ulong.MaxValue, 2));
        history.Record(Entry(1, "cpu-write-notification", 0, 0));
        Assert.Empty(history.Lifecycle.Read());
        Assert.Empty(history.Writes.Read());
    }
}

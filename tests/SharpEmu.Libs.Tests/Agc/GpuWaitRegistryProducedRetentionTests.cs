// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GpuWaitRegistryProducedRetentionTests
{
    private const ulong WatchedLabel = 0x7020_0000_1000UL;

    // A suspended DCB whose label the guest has recycled can only be released by
    // replaying the value a real producer wrote to that label. Recording enough
    // unrelated producers to cross the table's soft bound must not discard that
    // value, or the waiter is stranded and the graphics queue never resumes.
    [Fact]
    public void ProducedValueSurvivesBoundCrossingWhileAWaiterWatchesIt()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        Assert.True(GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1));

        // Cross the soft bound with labels nobody is waiting on.
        for (var i = 0; i < 9000; i++)
        {
            GpuWaitRegistry.RecordProduced(memory, 0x7030_0000_0000UL + ((ulong)i * 8), 1);
        }

        // The guest has since recycled the label, so its memory no longer holds
        // the produced value — the registry's record is the only way back.
        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.NotNull(broken);
        Assert.Contains(broken!, waiter => waiter.WaitAddress == WatchedLabel);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void CircularHangPrefersBreakingAgedComputeBeforeGraphics()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        var graphics = NewWaiter(memory, 0x4002_02DC0UL);
        graphics.QueueName = "dcb.graphics";
        GpuWaitRegistry.Register(0x4002_02DC0UL, graphics);

        var compute = NewWaiter(memory, 0x4002_03980UL);
        compute.QueueName = "acb.compute[72]";
        GpuWaitRegistry.Register(0x4002_03980UL, compute);

        var broken = GpuWaitRegistry.CollectCircularComputeBreaks(
            memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.NotNull(broken);
        Assert.Single(broken!);
        Assert.StartsWith("acb.compute", broken![0].QueueName);
        Assert.Equal(0x4002_03980UL, broken[0].WaitAddress);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void StaleProducedValueDoesNotBreakWaiterRegisteredInLaterFrame()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
        GpuWaitRegistry.AdvanceFrame();

        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.Null(broken);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void SameFrameProducedValueCanBreakAfterLabelReset()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));

        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.NotNull(broken);
        Assert.Contains(broken!, waiter => waiter.WaitAddress == WatchedLabel);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void UnwatchedProducedValuesArePrunedAtTheBound()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();

        for (var i = 0; i < 9000; i++)
        {
            GpuWaitRegistry.RecordProduced(memory, 0x7030_0000_0000UL + ((ulong)i * 8), 1);
        }

        // Nothing was watching any of them, so a waiter registered afterwards on
        // a pruned label has no produced value to replay and stays suspended.
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));
        var broken = GpuWaitRegistry.CollectDeadlockBroken(memory, nowTicks: 1_000_000, minAgeTicks: 1);

        Assert.Null(broken);
        GpuWaitRegistry.Clear();
    }

    private static GpuWaitRegistry.WaitingDcb NewWaiter(object memory, ulong address) => new()
    {
        WaitAddress = address,
        ReferenceValue = 1,
        Mask = 0xFFFF_FFFFUL,
        CompareFunction = 3, // equal
        Memory = memory,
        QueueName = "dcb.graphics",
        RegisteredTicks = 0,
    };
}

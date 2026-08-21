// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GpuWaitRegistryProducedRetentionTests
{
    private const ulong WatchedLabel = 0x7020_0000_1000UL;

    [Fact]
    public void LabelDependencyMergeKeepsLatestValueForEachQueue()
    {
        var merged = new GuestGpuLabelDependency(7, 13).Merge(
            new GuestGpuLabelDependency(11, 5));

        Assert.Equal(new GuestGpuLabelDependency(11, 13), merged);
    }

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

    [Fact]
    public void VirtualLabelWakesWaiterWithProducerDependency()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var dependency = new GuestGpuLabelDependency(17, 0);
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));

        GpuWaitRegistry.RecordVirtualProduced(
            memory,
            WatchedLabel,
            1,
            dependency);
        var satisfied = GpuWaitRegistry.CollectSatisfied(
            memory,
            static (_, _) => 0);

        var waiter = Assert.Single(Assert.IsType<List<GpuWaitRegistry.WaitingDcb>>(satisfied));
        Assert.Equal(dependency, waiter.Dependency);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void VirtualLabelKeepsSatisfiedWaitLatchedAfterCpuReset()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var dependency = new GuestGpuLabelDependency(17, 0);
        GpuWaitRegistry.Register(WatchedLabel, NewWaiter(memory, WatchedLabel));

        GpuWaitRegistry.RecordVirtualProduced(
            memory,
            WatchedLabel,
            1,
            dependency);
        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 0);

        var satisfied = GpuWaitRegistry.CollectSatisfied(
            memory,
            static (_, _) => 0);

        var waiter = Assert.Single(Assert.IsType<List<GpuWaitRegistry.WaitingDcb>>(satisfied));
        Assert.Equal(dependency, waiter.Dependency);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void Virtual64BitLabelMergesBothProducerDependencies()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel);
        waiter.Is64Bit = true;
        waiter.ReferenceValue = 0x0000_0002_0000_0001UL;
        waiter.Mask = ulong.MaxValue;
        GpuWaitRegistry.Register(WatchedLabel, waiter);

        GpuWaitRegistry.RecordVirtualProducedRange(
            memory,
            WatchedLabel,
            [1, 2],
            new GuestGpuLabelDependency(11, 13),
            cachePolicy: 0,
            engine: GpuWaitRegistry.VirtualLabelEngine.Pfp);
        var satisfied = GpuWaitRegistry.CollectSatisfied(
            memory,
            static (_, _) => 0);

        var resumed = Assert.Single(
            Assert.IsType<List<GpuWaitRegistry.WaitingDcb>>(satisfied));
        Assert.Equal(new GuestGpuLabelDependency(11, 13), resumed.Dependency);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void Virtual64BitLabelRejectsDwordsFromDifferentPublications()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.RecordVirtualProducedRange(
            memory,
            WatchedLabel,
            [1, 2],
            new GuestGpuLabelDependency(11, 0),
            cachePolicy: 0,
            engine: GpuWaitRegistry.VirtualLabelEngine.Pfp);

        GpuWaitRegistry.RecordVirtualProduced(
            memory,
            WatchedLabel,
            3,
            new GuestGpuLabelDependency(12, 0));

        Assert.False(
            GpuWaitRegistry.TryReadVirtual(
                memory,
                WatchedLabel,
                is64Bit: true,
                out _,
                out _));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void Virtual64BitLabelCanReadAnInteriorPairFromOnePublication()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.RecordVirtualProducedRange(
            memory,
            WatchedLabel,
            [10, 20, 30],
            new GuestGpuLabelDependency(8, 0),
            cachePolicy: 0,
            engine: GpuWaitRegistry.VirtualLabelEngine.Pfp);

        Assert.True(
            GpuWaitRegistry.TryReadVirtual(
                memory,
                WatchedLabel + sizeof(uint),
                is64Bit: true,
                out var value,
                out var dependency));
        Assert.Equal(20UL | (30UL << 32), value);
        Assert.Equal(new GuestGpuLabelDependency(8, 0), dependency);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void GpuCachePoliciesShareTheVirtualLabelVisibilityDomain()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.RecordVirtualProducedRange(
            memory,
            WatchedLabel,
            [1],
            new GuestGpuLabelDependency(7, 0),
            cachePolicy: 1,
            engine: GpuWaitRegistry.VirtualLabelEngine.Me);

        foreach (var cachePolicy in new uint[] { 0, 1, 2 })
        {
            Assert.True(
                GpuWaitRegistry.TryReadVirtual(
                    memory,
                    WatchedLabel,
                    is64Bit: false,
                    out var value,
                    out var dependency,
                    requiredCachePolicy: cachePolicy));
            Assert.Equal(1UL, value);
            Assert.Equal(new GuestGpuLabelDependency(7, 0), dependency);
        }

        Assert.False(
            GpuWaitRegistry.TryReadVirtual(
                memory,
                WatchedLabel,
                is64Bit: false,
                out _,
                out _,
                requiredCachePolicy: 3));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void NoallocWaiterConsumesStreamPolicyVirtualLabel()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel);
        waiter.ControlValue = 2u << 25;
        GpuWaitRegistry.Register(WatchedLabel, waiter);

        GpuWaitRegistry.RecordVirtualProducedRange(
            memory,
            WatchedLabel,
            [1],
            new GuestGpuLabelDependency(9, 0),
            cachePolicy: 1,
            engine: GpuWaitRegistry.VirtualLabelEngine.Me);
        var satisfied = GpuWaitRegistry.CollectSatisfied(
            memory,
            static (_, _) => 0);

        var resumed = Assert.Single(
            Assert.IsType<List<GpuWaitRegistry.WaitingDcb>>(satisfied));
        Assert.Equal(new GuestGpuLabelDependency(9, 0), resumed.Dependency);
        GpuWaitRegistry.Clear();
    }

    [Theory]
    [InlineData("dcb.graphics", 4u, 0)]
    [InlineData("dcb.graphics", 5u, 1)]
    [InlineData("acb.compute", 2u, 2)]
    public void GpuLabelWriteAcceptsSdkGl2Destinations(
        string queueName,
        uint destination,
        int expectedEngine)
    {
        Assert.True(
            AgcExports.TryClassifyGpuLabelWrite(
                queueName,
                destination,
                incrementAddress: true,
                writeConfirm: true,
                cachePolicy: 0,
                out var engine));
        Assert.Equal((GpuWaitRegistry.VirtualLabelEngine)expectedEngine, engine);
    }

    [Theory]
    [InlineData("dcb.graphics", 2u)]
    [InlineData("acb.compute", 4u)]
    [InlineData("acb.compute", 5u)]
    public void GpuLabelWriteRejectsDestinationsFromTheWrongEngine(
        string queueName,
        uint destination)
    {
        Assert.False(
            AgcExports.TryClassifyGpuLabelWrite(
                queueName,
                destination,
                incrementAddress: true,
                writeConfirm: true,
                cachePolicy: 0,
                out _));
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    [InlineData(3u, false)]
    public void GpuLabelWriteAcceptsOnlyGpuCachePolicies(
        uint cachePolicy,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.TryClassifyGpuLabelWrite(
                "dcb.graphics",
                destination: 4,
                incrementAddress: true,
                writeConfirm: true,
                cachePolicy: cachePolicy,
                engine: out _));
    }

    [Fact]
    public void CpuVisibleWriteReplacesVirtualLabel()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.RecordVirtualProduced(
            memory,
            WatchedLabel,
            1,
            new GuestGpuLabelDependency(5, 0));

        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 0);

        Assert.False(
            GpuWaitRegistry.TryReadVirtual(
                memory,
                WatchedLabel,
                is64Bit: false,
                out _,
                out _));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void CpuVisible64BitWriteLatchesHighDwordWait()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel + sizeof(uint));
        waiter.ReferenceValue = 2;
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

        Assert.True(GpuWaitRegistry.RecordProduced(
            memory,
            WatchedLabel,
            1UL | (2UL << 32),
            hasHighDword: true));

        var resumed = Assert.Single(
            Assert.IsType<List<GpuWaitRegistry.WaitingDcb>>(
                GpuWaitRegistry.CollectSatisfied(memory, static (_, _) => 0)));
        Assert.Equal(WatchedLabel + sizeof(uint), resumed.WaitAddress);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void CpuVisible64BitWriteDoesNotLatchOverlapping64BitWait()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var waiter = NewWaiter(memory, WatchedLabel + sizeof(uint));
        waiter.Is64Bit = true;
        waiter.ReferenceValue = 2;
        waiter.Mask = ulong.MaxValue;
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

        Assert.False(GpuWaitRegistry.RecordProduced(
            memory,
            WatchedLabel,
            1UL | (2UL << 32),
            hasHighDword: true));
        Assert.Null(GpuWaitRegistry.CollectSatisfied(memory, static (_, _) => 0));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void HighDwordWriteIsAvailableToDelayedWaiter()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.RecordProduced(
            memory,
            WatchedLabel,
            1UL | (2UL << 32),
            hasHighDword: true);

        var waiter = NewWaiter(memory, WatchedLabel + sizeof(uint));
        waiter.ReferenceValue = 2;
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);

        var resumed = Assert.Single(
            Assert.IsType<List<GpuWaitRegistry.WaitingDcb>>(
                GpuWaitRegistry.CollectDeadlockBroken(
                    memory,
                    nowTicks: 1_000_000,
                    minAgeTicks: 1)));
        Assert.Equal(WatchedLabel + sizeof(uint), resumed.WaitAddress);
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void RemoveAllByState_LeavesOtherQueuesAndMemoriesRegistered()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var otherMemory = new object();
        var stoppedState = new object();
        var otherState = new object();
        var first = NewWaiter(memory, WatchedLabel);
        first.State = stoppedState;
        var second = NewWaiter(memory, WatchedLabel + sizeof(uint));
        second.State = stoppedState;
        var otherQueue = NewWaiter(memory, WatchedLabel + 8);
        otherQueue.State = otherState;
        var otherGuest = NewWaiter(otherMemory, WatchedLabel + 12);
        otherGuest.State = stoppedState;
        GpuWaitRegistry.Register(first.WaitAddress, first);
        GpuWaitRegistry.Register(second.WaitAddress, second);
        GpuWaitRegistry.Register(otherQueue.WaitAddress, otherQueue);
        GpuWaitRegistry.Register(otherGuest.WaitAddress, otherGuest);

        Assert.Equal(2, GpuWaitRegistry.RemoveAllByState(memory, stoppedState));
        Assert.Equal(2, GpuWaitRegistry.Count);
        Assert.Equal(1, GpuWaitRegistry.CountForMemory(memory));
        Assert.Equal(1, GpuWaitRegistry.CountForMemory(otherMemory));
        GpuWaitRegistry.Clear();
    }

    [Fact]
    public void DelayedSubmissionReplaysAProducedValueAfterReset()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var generation = GpuWaitRegistry.BeginSubmission(memory);
        try
        {
            GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
            GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 0);
            var waiter = NewWaiter(memory, WatchedLabel);
            waiter.SubmissionPublicationGeneration = generation;

            var result = GpuWaitRegistry.RegisterIfUnsatisfied(
                waiter,
                static (_, _) => 0,
                out var value,
                out _);

            Assert.Equal(
                GpuWaitRegistry.WaitRegistrationResult.SatisfiedByHistory,
                result);
            Assert.Equal(1UL, value);
            Assert.Equal(0, GpuWaitRegistry.Count);
        }
        finally
        {
            GpuWaitRegistry.EndSubmission(memory, generation);
            GpuWaitRegistry.Clear();
        }
    }

    [Fact]
    public void NewSubmissionDoesNotReplayAnOlderProducedValue()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 0);
        var generation = GpuWaitRegistry.BeginSubmission(memory);
        try
        {
            var waiter = NewWaiter(memory, WatchedLabel);
            waiter.SubmissionPublicationGeneration = generation;

            var result = GpuWaitRegistry.RegisterIfUnsatisfied(
                waiter,
                static (_, _) => 0,
                out var value,
                out _);

            Assert.Equal(GpuWaitRegistry.WaitRegistrationResult.Registered, result);
            Assert.Equal(0UL, value);
            Assert.Equal(1, GpuWaitRegistry.Count);
        }
        finally
        {
            GpuWaitRegistry.EndSubmission(memory, generation);
            GpuWaitRegistry.Clear();
        }
    }

    [Fact]
    public void ActiveSubmissionRetainsAProducedValueAcrossLabelReuse()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var generation = GpuWaitRegistry.BeginSubmission(memory);
        try
        {
            GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
            for (var index = 0; index < 256; index++)
            {
                GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 0);
            }

            var waiter = NewWaiter(memory, WatchedLabel);
            waiter.SubmissionPublicationGeneration = generation;
            var result = GpuWaitRegistry.RegisterIfUnsatisfied(
                waiter,
                static (_, _) => 0,
                out var value,
                out _);

            Assert.Equal(
                GpuWaitRegistry.WaitRegistrationResult.SatisfiedByHistory,
                result);
            Assert.Equal(1UL, value);
        }
        finally
        {
            GpuWaitRegistry.EndSubmission(memory, generation);
            GpuWaitRegistry.Clear();
        }
    }

    [Fact]
    public void Replayed64BitValueUsesDwordsFromOnePublication()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var generation = GpuWaitRegistry.BeginSubmission(memory);
        try
        {
            const ulong published = 0x0000_0002_0000_0001UL;
            GpuWaitRegistry.RecordProduced(
                memory,
                WatchedLabel,
                published,
                hasHighDword: true);
            GpuWaitRegistry.RecordProduced(
                memory,
                WatchedLabel,
                0,
                hasHighDword: true);
            var waiter = NewWaiter(memory, WatchedLabel);
            waiter.Is64Bit = true;
            waiter.Mask = ulong.MaxValue;
            waiter.ReferenceValue = published;
            waiter.SubmissionPublicationGeneration = generation;

            var result = GpuWaitRegistry.RegisterIfUnsatisfied(
                waiter,
                static (_, _) => 0,
                out var value,
                out _);

            Assert.Equal(
                GpuWaitRegistry.WaitRegistrationResult.SatisfiedByHistory,
                result);
            Assert.Equal(published, value);
        }
        finally
        {
            GpuWaitRegistry.EndSubmission(memory, generation);
            GpuWaitRegistry.Clear();
        }
    }

    [Fact]
    public void PublicationHistoryEndsWithItsLastSubmission()
    {
        GpuWaitRegistry.Clear();
        var memory = new object();
        var first = GpuWaitRegistry.BeginSubmission(memory);
        var second = GpuWaitRegistry.BeginSubmission(memory);

        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
        Assert.Equal(
            1,
            GpuWaitRegistry.GetPublicationHistoryCountForTests(
                memory,
                WatchedLabel));

        GpuWaitRegistry.EndSubmission(memory, first);
        Assert.Equal(
            1,
            GpuWaitRegistry.GetPublicationHistoryCountForTests(
                memory,
                WatchedLabel));

        GpuWaitRegistry.EndSubmission(memory, second);
        Assert.Equal(
            0,
            GpuWaitRegistry.GetPublicationHistoryCountForTests(
                memory,
                WatchedLabel));

        GpuWaitRegistry.RecordProduced(memory, WatchedLabel, 1);
        Assert.Equal(
            0,
            GpuWaitRegistry.GetPublicationHistoryCountForTests(
                memory,
                WatchedLabel));
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

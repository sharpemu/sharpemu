// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.VideoOut;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class PendingGuestPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredCompletionReadsPublishedCountWithoutStoppingPackets(bool flip)
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            using var harness = new DeferredSignalHarness();
            var runner = harness.Runner;
            harness.AddDump();
            harness.Scheduler.Flush();
            var packet = flip
                ? StreamRunner.CustomPacket(PacketOpcode.Nop, PacketCustomCode.Flip, 1, 0, 0, 0, 0)
                : StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x528, 2u << 24, 0, 0, 0, 0, 0);
            Assert.Equal(SubmissionProgress.Complete, runner.Run(packet));
            Assert.Empty(harness.Observed);
            harness.Scheduler.Flush();
            harness.Device.Complete(2);
            harness.Scheduler.RunCompletedOperations();
            Assert.Equal([(1UL << 63) | 37], harness.Observed);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DelayedDumpHoldsLabelsInterruptsAndFlipsBeforeTheirStores(int kind)
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            using var harness = new DeferredSignalHarness();
            var runner = harness.Runner;
            harness.AddDump();
            harness.Scheduler.Flush();
            var label = StreamRunner.LabelAddress;
            var packet = kind switch
            {
                2 => StreamRunner.CustomPacket(PacketOpcode.Nop, PacketCustomCode.Flip, 1, 0, 0, 0, 0),
                3 => StreamRunner.CustomPacket(PacketOpcode.Nop, 0, 0x6875_0778,
                    StreamRunner.Low(label), StreamRunner.High(label), 7),
                _ => StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x528,
                    (1u << 29) | (kind == 1 ? 2u << 24 : 0),
                    StreamRunner.Low(label), StreamRunner.High(label), 7, 0, 0),
            };
            var wait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
                StreamRunner.Low(label), StreamRunner.High(label), 7, uint.MaxValue, 0);
            runner.Load(kind == 2 ? [packet] : [packet, wait]);
            Assert.Equal(SubmissionProgress.Complete, runner.Run());
            Assert.Empty(runner.Host.EndOfPipeWrites);
            Assert.Equal(0u, runner.Host.ReadDword(label));
            harness.Scheduler.Flush();
            harness.Device.Complete(2);
            harness.Scheduler.RunCompletedOperations();
            Assert.Equal(kind == 2 ? 0u : 7u, runner.Host.ReadDword(label));
            Assert.Equal(0, harness.Pending.Count);
            Assert.Equal((1UL << 63) | 37, runner.Host.ReadQword(StreamRunner.DataAddress));
        }
    }

    [Fact]
    public void CommandProcessorReadsPartOfDeferredLabelButNotPendingDump()
    {
        var pending = new PendingGuestPublications();
        pending.Add(0, 0x2000, 8);
        Assert.NotNull(pending.AddAfterPending(0, 0x1000, 8, 0x1122_3344_5566_7788));
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(pending.TryOverlayLabels(0x1004, bytes, out var covered));
        Assert.Equal(0xF, covered);
        Assert.Equal(0x1122_3344u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.True(pending.TryOverlayLabels(0x0FFC, bytes.ToArray(), out covered));
        Assert.Equal(0x0, covered);
        Assert.True(pending.TryOverlayLabels(0x0FFE, bytes, out covered));
        Assert.Equal(0xC, covered);
        Assert.False(pending.TryOverlayLabels(0x2004, bytes, out _));
    }

    [Fact]
    public void PageIndexFindsEntryAcrossPagesUntilPublished()
    {
        var pending = new PendingGuestPublications();
        var entry = pending.Add(0, 0x1FFC, 8);
        pending.Register(entry, 5);
        Assert.True(pending.HasOverlap(0x2000, 4));
        Assert.True(pending.HasOverlap(0x0, 0x10_0000));
        Assert.False(pending.HasOverlap(0x3000, 4));
        Assert.Equal(5UL, pending.OverlapTick(0x1000, 0x1000));
        pending.Publish(entry, static () => { });
        Assert.False(pending.HasOverlap(0x2000, 4));
        Assert.Equal(0UL, pending.OverlapTick(0x0, 0x10_0000));
    }

    [Fact]
    public void ResourceReadsWaitForDumpsButNotDeferredLabels()
    {
        var pending = new PendingGuestPublications();
        var dump = pending.Add(0, 0x2000, 8);
        pending.Register(dump, 3);
        var label = pending.AddAfterPending(0, 0x5000, 4, 7)!;
        pending.Register(label, 4);
        Assert.Equal(0UL, pending.OverlapTick(0x5000, 0x100, includeLabels: false));
        Assert.Equal(4UL, pending.OverlapTick(0x5000, 0x100));
        Assert.Equal(3UL, pending.OverlapTick(0x2000, 0x100, includeLabels: false));
        Assert.True(pending.HasOverlap(0x0, 0x10_0000, includeLabels: false));
        pending.Publish(dump, static () => { });
        Assert.False(pending.HasOverlap(0x0, 0x10_0000, includeLabels: false));
        Assert.True(pending.HasOverlap(0x0, 0x10_0000));
    }

    [Fact]
    public void OverlaidReadsSkipOnlyDumpsWithGpuSlots()
    {
        var pending = new PendingGuestPublications();
        var shadowed = pending.Add(0, 0x2000, 8);
        pending.Register(shadowed, 3);
        shadowed.GpuSlot = 0;
        var plain = pending.Add(0, 0x3000, 8);
        pending.Register(plain, 4);
        Assert.Equal(0UL, pending.OverlapTick(0x2000, 0x100, includeLabels: false, skipGpuSlots: true));
        Assert.Equal(3UL, pending.OverlapTick(0x2000, 0x100, includeLabels: false));
        Assert.Equal(4UL, pending.OverlapTick(0x2000, 0x1100, includeLabels: false, skipGpuSlots: true));
        Assert.Equal(4UL, pending.OverlapTick(0x2000, 0x1100));
        Assert.Contains(shadowed, pending.PendingDumps);
    }

    [Fact]
    public void OtherQueueLabelsStayImmediate()
    {
        var runner = new StreamRunner(1);
        var pending = new PendingGuestPublications();
        pending.Add(0, StreamRunner.DataAddress, 8);
        runner.Host.PendingPublications = pending.HasQueue;
        var label = StreamRunner.LabelAddress;
        Assert.Equal(SubmissionProgress.Complete, runner.Run(StreamRunner.Packet(PacketOpcode.ReleaseMemory,
            0x528, 1u << 29, StreamRunner.Low(label), StreamRunner.High(label), 7, 0, 0)));
        Assert.Equal(7u, runner.Host.ReadDword(label));
    }

    [Fact]
    public void PublicationUsesItsRegisteredTickAndRunsOutsideCallbackScope()
    {
        var device = new FakeTickDevice();
        using var scheduler = NewActiveScheduler(device);
        var pending = new PendingGuestPublications();
        var entry = pending.Add(0, 0x1000, 8);
        pending.Register(entry, scheduler.CurrentTick);
        var result = 0;
        scheduler.QueueCompletionAction(() => result = 37);
        scheduler.QueueMemoryPublication(() => pending.Publish(entry, () =>
        {
            Assert.False(SubmissionScheduler.InDeferredOperation);
            Assert.Equal(37, result);
        }));
        scheduler.Flush();
        scheduler.RunCompletedOperations();
        Assert.True(pending.HasQueue(0));
        device.Complete(1);
        scheduler.RunCompletedOperations();
        Assert.False(pending.HasQueue(0));
        Assert.Equal(2UL, scheduler.CurrentTick);
        device.CompleteOnSubmit = true;
    }

    [Fact]
    public void ReentrantCollectionDoesNotPublishLaterWritesFirst()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        var order = new List<int>();
        scheduler.QueueMemoryPublication(() =>
        {
            scheduler.RunCompletedOperations();
            order.Add(1);
        });
        scheduler.QueueMemoryPublication(() => order.Add(2));
        scheduler.Flush();
        scheduler.RunCompletedOperations();
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void MappingDrainIncludesPublicationAndAllowsRequiredMemoryDrain()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        var published = false;
        scheduler.QueueMemoryPublication(() =>
        {
            Assert.False(scheduler.InsideTickCallback);
            scheduler.FinishMemoryAccess();
            published = true;
        });
        scheduler.FinishMemoryAccess();
        Assert.True(published);
    }

    [Fact]
    public void LaterOverlappingStoreRunsAfterPublication()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        var pending = new PendingGuestPublications();
        var entry = pending.Add(0, 0x1000, 8);
        pending.Register(entry, scheduler.CurrentTick);
        ulong value = 0;
        scheduler.QueueMemoryPublication(() => pending.Publish(entry, () => value = (1UL << 63) | 37));
        scheduler.Flush();
        scheduler.Wait(pending.OverlapTick(0x1000, 8));
        scheduler.RunCompletedOperations();
        Assert.Equal((1UL << 63) | 37, value);
        value = 5;
        scheduler.RunCompletedOperations();
        Assert.Equal(5UL, value);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void OverlapTracksOnlyRecordedWorkAndFailureClearsPendingEntries()
    {
        var pending = new PendingGuestPublications();
        var entry = pending.Add(0, 0x1000, 8);
        Assert.True(pending.HasOverlap(0x1004, 8));
        Assert.False(pending.HasOverlap(0x1008, 8));
        Assert.Equal(0UL, pending.OverlapTick(0x1000, 8));
        pending.Register(entry, 3);
        Assert.Equal(3UL, pending.OverlapTick(0x1004, 8));
        Assert.Throws<IOException>(() => pending.Publish(entry, () => throw new IOException("write failed")));
        Assert.Equal(0, pending.Count);
        Assert.Throws<InvalidOperationException>(() => pending.HasQueue(0));
    }
}

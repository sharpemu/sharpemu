// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class OrderedOcclusionDumpTests
{
    [Theory]
    [InlineData(false, 0UL)]
    [InlineData(true, 0UL)]
    [InlineData(false, 4UL)]
    public void SubmittedDumpsRecordWithoutWaitingOrSubmitting(bool reset, ulong offset)
    {
        using var fatal = new FatalScope();
        var device = new FakeTickDevice { FailWait = true };
        using var scheduler = NewActiveScheduler(device);
        using var queries = new VulkanOcclusionQueries(null!, default);
        var host = new RecordingCommandStreamHost();
        var pending = new PendingGuestPublications();
        var type = PresenterUnderTest.PresenterType;
        var presenter = RuntimeHelpers.GetUninitializedObject(type);
        void Set(string name, object value) => type.GetField(name, PresenterUnderTest.InstanceMembers)!.SetValue(presenter, value);
        Set("_scheduler", scheduler);
        Set("_guestPublications", pending);
        Set("_occlusionQueries", queries);
        Set("_guestMemory", host.Memory);
        Set("_supportsPreciseOcclusion", true);
        var streamHost = (ICommandStreamHost)presenter;

        // Model an already submitted query; use the production dump and publication paths.
        var group = typeof(VulkanOcclusionQueries).GetMethod("GroupOf", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(queries, [0])!;
        group.GetType().GetField("Recorded")!.SetValue(group, 1L);
        group.GetType().GetField("SubmittedTick")!.SetValue(group, 1UL);
        var dumps = (Queue<(long Needed, Action<ulong> Publish)>)group.GetType().GetField("Dumps")!.GetValue(group)!;
        scheduler.QueueCompletionAction(() =>
        {
            group.GetType().GetField("Completed")!.SetValue(group, 1L);
            group.GetType().GetField("Sum")!.SetValue(group, 37UL);
            while (dumps.TryDequeue(out var dump)) dump.Publish(37);
        });
        scheduler.Flush();
        try
        {
            Assert.True(streamHost.DumpOcclusionCounter(0, StreamRunner.DataAddress, 1, 8));
            if (reset) Assert.True(streamHost.ResetOcclusionCounter(0));
            Assert.True(streamHost.DumpOcclusionCounter(0, StreamRunner.DataAddress + offset, 1, 8));
            Assert.Equal(2, pending.Count);
            Assert.Single(device.Submits);
            Assert.DoesNotContain(device.Log, item => item.StartsWith("wait ", StringComparison.Ordinal));
            Assert.Equal(0UL, host.ReadQword(StreamRunner.DataAddress));

            device.Complete(1);
            scheduler.RunCompletedOperations();
            Assert.Equal((1UL << 63) | (reset ? 0UL : 37UL), host.ReadQword(StreamRunner.DataAddress + offset));
            Assert.Equal(0, pending.Count);
            Assert.Single(device.Submits);
        }
        finally
        {
            device.Complete(1);
            device.FailWait = false;
            device.CompleteOnSubmit = true;
        }
    }

    [Fact]
    public void AdoptedWorkerQueriesCountInTheGroupCurrentAtTheJoin()
    {
        using var queries = new VulkanOcclusionQueries(null!, default);
        queries.Adopt(new VulkanOcclusionQueries.Block(default), 3);
        Assert.False(queries.HasUnsubmitted);

        queries.Reset(3);
        var block = new VulkanOcclusionQueries.Block(default) { Count = 2 };
        queries.Adopt(block, 3);

        Assert.True(queries.HasUnsubmitted);
        Assert.All(block.Queues.Take(2), queue => Assert.Equal(3, queue));
        var group = typeof(VulkanOcclusionQueries).GetMethod("GroupOf", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(queries, [3])!;
        Assert.Same(group, block.Groups[0]);
        Assert.Equal(2L, group.GetType().GetField("Recorded")!.GetValue(group));
        Assert.False(queries.Dump(3, _ => { }));
    }

    [Fact]
    public void PublicationRetainsEarlierUnsubmittedDependency()
    {
        var device = new FakeTickDevice();
        using var scheduler = NewActiveScheduler(device);
        var order = new List<int>();
        scheduler.QueueCompletionAction(() => order.Add(1));
        var tick = scheduler.QueueMemoryPublication(() => order.Add(2), 0);
        Assert.Equal(scheduler.CurrentTick, tick);
        scheduler.RunCompletedOperations();
        Assert.Empty(order);
        device.CompleteOnSubmit = true;
        scheduler.Flush();
        scheduler.RunCompletedOperations();
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public void ReadyPublicationNeedsNoSubmissionAndRejectsUnknownTick()
    {
        using var fatal = new FatalScope();
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        using var scheduler = NewActiveScheduler(device);
        var published = false;
        Assert.Equal(0UL, scheduler.QueueMemoryPublication(() => published = true, 0));
        scheduler.RunCompletedOperations();
        Assert.True(published);
        Assert.Empty(device.Submits);
        Assert.Throws<SchedulerFatalException>(() => scheduler.QueueMemoryPublication(() => { }, scheduler.CurrentTick + 1));
    }
}

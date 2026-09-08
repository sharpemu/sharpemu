// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Memory.GpuMemory;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class PresenterSubmissionTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type PresenterType = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;

    [Fact]
    public void ImageUploadProfile_AddsBytesAndSeparatesPreparationCosts()
    {
        var statistics = new RenderPhaseProfile.ImageUploadStatistics();
        statistics.Add(256, 10, 20, 30);
        statistics.Add(512, 40, 50, 60);
        Assert.Equal(2, statistics.Count);
        Assert.Equal(768UL, statistics.SourceBytes);
        Assert.Equal(50, statistics.WatchTicks);
        Assert.Equal(70, statistics.SourceTicks);
        Assert.Equal(90, statistics.RecordTicks);
    }

    [Fact]
    public void ImageUploadProfile_BoundsDetailsPreservesOverflowAndResets()
    {
        var report = typeof(RenderPhaseProfile).GetMethod("ReportImageUploads", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousError = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            report.Invoke(null, null);
            output.GetStringBuilder().Clear();
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Draw))
            {
                for (var index = 0; index < 260; index++)
                {
                    var description = SharpEmu.Libs.Gpu.Images.ImageDescription.Create();
                    description.Data = new GuestSpan((ulong)(index + 1) * 4096, 256);
                    RenderPhaseProfile.RecordImageUpload(description, "cpu-dirty", 1, 2, 3,
                        "before-clear", description.Data.Address, 256);
                }
            }

            report.Invoke(null, null);
            if (RenderPhaseProfile.Enabled)
            {
                Assert.Equal(9, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
                Assert.Contains("other=1 uploads=252 source_bytes=64512", output.ToString());
                Assert.Contains("path=before-clear", output.ToString());
                Assert.Contains("write_bytes=256", output.ToString());
            }
            else
            {
                Assert.Empty(output.ToString());
            }

            output.GetStringBuilder().Clear();
            report.Invoke(null, null);
            Assert.Empty(output.ToString());
        }
        finally
        {
            report.Invoke(null, null);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task SubmissionCompletion_WakesForTheSubmittedTickBeforeFutureCallbacks()
    {
        var device = new FakeTickDevice();
        var firstCompletion = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var futureCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new SubmissionScheduler(device, new RecordingRenderingState(),
            completed: tick => firstCompletion.TrySetResult(tick));
        scheduler.Begin(new SubmissionContext { QueueName = "test.queue" });
        try
        {
            Assert.Equal(1UL, scheduler.Flush());
            scheduler.QueuePriorityCompletionAction(() => futureCallback.TrySetResult());
            Assert.False(firstCompletion.Task.IsCompleted);
            device.Complete(1);
            Assert.Equal(1UL, await firstCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(futureCallback.Task.IsCompleted);
            Assert.Equal(2UL, scheduler.CurrentTick);
            scheduler.Flush();
            device.Complete(2);
            await futureCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            device.CompleteOnSubmit = true;
            device.Complete(ulong.MaxValue);
        }
    }

    [Fact]
    public void SubmissionCompletion_AlreadyFinishedTicksAreNotLost()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        var completedTicks = new System.Collections.Concurrent.ConcurrentQueue<ulong>();
        using var scheduler = new SubmissionScheduler(device, new RecordingRenderingState(),
            completed: completedTicks.Enqueue);
        scheduler.Begin(new SubmissionContext { QueueName = "test.queue" });
        scheduler.Flush();
        scheduler.Flush();
        scheduler.WaitForAllPriorityOperations();
        Assert.Equal(new ulong[] { 1, 2 }, completedTicks.ToArray());
        scheduler.Shutdown();
        Assert.Equal(new ulong[] { 1, 2, 3 }, completedTicks.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmissionCompletion_WakesThePresenterWithoutLosingEarlyCompletion(bool completeBeforeWait)
    {
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var gate = typeof(VulkanVideoPresenter).GetField("_gate", staticMembers)!.GetValue(null)!;
        var wake = PresenterType.GetMethod("WakeRenderThread", staticMembers)!.CreateDelegate<Action>();
        var device = new FakeTickDevice();
        using var scheduler = new SubmissionScheduler(device, new RecordingRenderingState(), completed: _ => wake());
        scheduler.Begin(new SubmissionContext { QueueName = "test.queue" });
        scheduler.Flush();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? waiter = null;
        try
        {
            if (completeBeforeWait)
            {
                device.Complete(1);
                scheduler.WaitForAllPriorityOperations();
            }

            waiter = Task.Run(() =>
            {
                lock (gate)
                {
                    entered.SetResult();
                    while (scheduler.Timeline.CompletedTick < 1)
                    {
                        Assert.True(Monitor.Wait(gate, TimeSpan.FromSeconds(5)), "Completion did not wake the presenter.");
                    }
                }
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            device.Complete(1);
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            device.CompleteOnSubmit = true;
            device.Complete(ulong.MaxValue);
            wake();
            if (waiter is not null)
            {
                await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public void RenderProfile_DetailRequiresAnActiveScopeAndRestoresItsParent()
    {
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var profile = typeof(RenderPhaseProfile);
        var entries = (long[])profile.GetField("_entries", staticMembers)!.GetValue(null)!;
        var depth = profile.GetField("_scopeDepth", staticMembers)!;
        var current = profile.GetField("_current", staticMembers)!;
        var initialEntries = entries[(int)RenderPhaseProfile.Phase.ImageLookup];
        Assert.Equal(0, (int)depth.GetValue(null)!);

        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageLookup)) { }
        Assert.Equal(initialEntries, entries[(int)RenderPhaseProfile.Phase.ImageLookup]);

        using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Draw))
        {
            try
            {
                using var detail = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageLookup);
                if (RenderPhaseProfile.Enabled)
                {
                    Assert.Equal(2, (int)depth.GetValue(null)!);
                    Assert.Equal(RenderPhaseProfile.Phase.ImageLookup, current.GetValue(null));
                }

                throw new InvalidOperationException("Test scope cleanup.");
            }
            catch (InvalidOperationException)
            {
                if (RenderPhaseProfile.Enabled)
                {
                    Assert.Equal(1, (int)depth.GetValue(null)!);
                    Assert.Equal(RenderPhaseProfile.Phase.Draw, current.GetValue(null));
                }
            }
        }

        Assert.Equal(0, (int)depth.GetValue(null)!);
        Assert.Equal(initialEntries + (RenderPhaseProfile.Enabled ? 1 : 0),
            entries[(int)RenderPhaseProfile.Phase.ImageLookup]);
    }

    [Fact]
    public void RenderProfile_WindowScopesRestoreTheRenderPhase()
    {
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var profile = typeof(RenderPhaseProfile);
        var entries = (long[])profile.GetField("_entries", staticMembers)!.GetValue(null)!;
        var depth = profile.GetField("_scopeDepth", staticMembers)!;
        var current = profile.GetField("_current", staticMembers)!;
        var phases = new[]
        {
            RenderPhaseProfile.Phase.WindowEvents,
            RenderPhaseProfile.Phase.CursorUpdate,
            RenderPhaseProfile.Phase.GamepadPoll,
            RenderPhaseProfile.Phase.WindowState,
            RenderPhaseProfile.Phase.WindowDelay,
            RenderPhaseProfile.Phase.QueueContext,
            RenderPhaseProfile.Phase.FollowupWait,
            RenderPhaseProfile.Phase.PresentationPreparation,
        };
        var initialEntries = phases.Select(phase => entries[(int)phase]).ToArray();
        Assert.Equal(0, (int)depth.GetValue(null)!);

        using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.WindowLoop))
        {
            foreach (var phase in phases)
            {
                using (RenderPhaseProfile.MeasureDetail(phase))
                {
                    using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Unattributed)) { }
                    if (RenderPhaseProfile.Enabled)
                    {
                        Assert.Equal(phase, current.GetValue(null));
                        Assert.Equal(2, (int)depth.GetValue(null)!);
                    }
                }
                if (RenderPhaseProfile.Enabled)
                {
                    Assert.Equal(RenderPhaseProfile.Phase.WindowLoop, current.GetValue(null));
                }
            }
        }

        Assert.Equal(0, (int)depth.GetValue(null)!);
        for (var index = 0; index < phases.Length; index++)
        {
            Assert.Equal(initialEntries[index] + (RenderPhaseProfile.Enabled ? 1 : 0),
                entries[(int)phases[index]]);
        }
    }

    [Fact]
    public void FollowupWork_DoesNotRetryBlockedQueueHeads()
    {
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var owner = typeof(VulkanVideoPresenter);
        var gate = owner.GetField("_gate", staticMembers)!.GetValue(null)!;
        var queues = (IDictionary)owner.GetField("_pendingGuestWorkByQueue", staticMembers)!.GetValue(null)!;
        var count = owner.GetField("_pendingGuestWorkCount", staticMembers)!;
        var completed = owner.GetField("_completedGuestWorkSequence", staticMembers)!;
        var wait = owner.GetMethod("WaitForFollowupGuestWork", staticMembers)!;
        var ready = owner.GetMethod("HasReadyGuestWorkLocked", staticMembers)!;
        var pendingType = owner.GetNestedType("PendingGuestWork", BindingFlags.NonPublic)!;
        var queueType = typeof(LinkedList<>).MakeGenericType(pendingType);

        object CreateQueue(string name, long dependency, long sequence = 1)
        {
            var queue = Activator.CreateInstance(queueType)!;
            var work = Activator.CreateInstance(pendingType,
                [new object(), 0UL, sequence, dependency, 0L, new VulkanGuestQueueIdentity(name, 1)])!;
            queueType.GetMethod("AddLast", [pendingType])!.Invoke(queue, [work]);
            return queue;
        }

        bool HasFollowup(HashSet<string>? excluded) => (bool)wait.Invoke(null, [0, excluded])!;

        lock (gate)
        {
            Assert.Empty(queues);
            var previousCount = count.GetValue(null);
            var previousCompleted = completed.GetValue(null);
            try
            {
                completed.SetValue(null, 0L);
                queues.Add("blocked", CreateQueue("blocked", 0));
                count.SetValue(null, 1);
                Assert.True(HasFollowup(null));
                Assert.False(HasFollowup(new HashSet<string> { "blocked" }));

                queues.Add("sibling", CreateQueue("sibling", long.MaxValue));
                count.SetValue(null, 2);
                Assert.False(HasFollowup(new HashSet<string> { "blocked" }));
                queues["sibling"] = CreateQueue("sibling", 0);
                Assert.True(HasFollowup(new HashSet<string> { "blocked" }));
                Assert.False(HasFollowup(new HashSet<string> { "blocked", "sibling" }));
                var blockedTicks = new Dictionary<long, ulong> { [1] = 7 };
                Assert.False((bool)ready.Invoke(null, [null, blockedTicks, 6UL])!);
                Assert.True((bool)ready.Invoke(null, [null, blockedTicks, 7UL])!);
                queues["sibling"] = CreateQueue("sibling", 0, 2);
                Assert.True((bool)ready.Invoke(null, [null, blockedTicks, 6UL])!);
                queues["sibling"] = CreateQueue("sibling", long.MaxValue, 2);
                Assert.False((bool)ready.Invoke(null, [null, blockedTicks, 6UL])!);
                Assert.Equal(2, queues.Count);
            }
            finally
            {
                queues.Clear();
                count.SetValue(null, previousCount);
                completed.SetValue(null, previousCompleted);
            }
        }
    }

    [Theory]
    [InlineData("unmap")]
    [InlineData("shutdown")]
    [InlineData("wait")]
    [InlineData("flush")]
    public void EverySubmissionRoutePublishesAndRetiresTheExactBatch(string route)
    {
        // Use the presenter bookkeeping without creating a window or Vulkan resources.
        var presenter = RuntimeHelpers.GetUninitializedObject(PresenterType);
        foreach (var name in new[]
        {
            "_batchResources", "_batchRetireBuffers",
            "_pendingGuestSubmissions", "_lastSubmittedTimelineByGuestQueue",
            "_lastSubmittedGpuLabelDependencyByGuestQueue", "_gpuLabelHostPublications",
            "_recycledDescriptorPools", "_deferredResourceDestroys", "_deferredGuestImageVersionDestroys",
        })
        {
            var field = PresenterType.GetField(name, InstanceMembers)!;
            field.SetValue(presenter, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }

        Set(presenter, "_batchOpen", true);
        Set(presenter, "_activeGuestQueue", new VulkanGuestQueueIdentity("test.queue", 7));
        Set(presenter, "_gpuLabelTimelineEnabled", true);
        Set(presenter, "_graphicsGuestTimelineSemaphore", new Silk.NET.Vulkan.Semaphore(100));

        var binding = NewNested("GlobalBufferResource");
        Set(binding, "Writable", true);
        var resources = NewNested("TranslatedDrawResources");
        var bindings = Array.CreateInstance(binding.GetType(), 1);
        bindings.SetValue(binding, 0);
        Set(resources, "GlobalMemoryBuffers", bindings);
        Set(resources, "DescriptorPool", new DescriptorPool(123));
        ((IList)Get(presenter, "_batchResources")).Add(resources);

        var device = new FakeTickDevice { CompleteOnSubmit = route != "flush" };
        using var scheduler = new SubmissionScheduler(
            device,
            (IRenderingState)presenter,
            PresenterType.GetMethod("PrepareGuestSubmission", InstanceMembers)!.CreateDelegate<Action<SubmitBundle>>(presenter),
            PresenterType.GetMethod("CompleteGuestSubmission", InstanceMembers)!.CreateDelegate<Action<ulong>>(presenter));
        Set(presenter, "_scheduler", scheduler);
        scheduler.Begin(new SubmissionContext { QueueName = "test.queue", SubmissionId = 7 });

        switch (route)
        {
            case "unmap":
                using (var memory = new GuestGpuMemory(new RecordingAddressSpace()))
                {
                    memory.Register(0x10000, 0x1000, GuestPageProtection.Read | GuestPageProtection.Write);
                    memory.AttachGpuQueue(null, scheduler);
                    memory.Unregister(0x10000, 0x1000);
                    Assert.False(memory.Covers(0x10000, 0x1000));
                }
                break;
            case "shutdown": scheduler.Shutdown(); break;
            case "wait": scheduler.Wait(scheduler.CurrentTick); break;
            case "flush": Invoke(presenter, "FlushBatchedGuestCommands", new object?[] { null }); break;
        }

        Assert.Equal(1UL, Assert.Single(device.Submits).Tick);
        Assert.Equal(2, device.Submits[0].Signals);
        Assert.False((bool)Get(presenter, "_batchOpen"));
        Assert.Empty((IEnumerable)Get(presenter, "_batchResources"));
        Assert.Equal(1UL, Get(presenter, "_submitTimeline"));
        var pending = Assert.Single(((IEnumerable)Get(presenter, "_pendingGuestSubmissions")).Cast<object>());
        Assert.Equal(1UL, pending.GetType().GetProperty("Tick")!.GetValue(pending));
        Assert.Same(resources, Assert.Single(((IEnumerable)pending.GetType().GetProperty("Resources")!.GetValue(pending)!).Cast<object>()));

        if (route == "flush")
        {
            Invoke(presenter, "CollectCompletedGuestSubmissions", false);
            Assert.Single(((IEnumerable)Get(presenter, "_pendingGuestSubmissions")).Cast<object>());
            Assert.Empty((IEnumerable)Get(presenter, "_recycledDescriptorPools"));
            device.Complete(1);
            device.CompleteOnSubmit = true;
        }

        Invoke(presenter, "CollectCompletedGuestSubmissions", false);
        Assert.Empty((IEnumerable)Get(presenter, "_pendingGuestSubmissions"));
        Assert.Single(((IEnumerable)Get(presenter, "_recycledDescriptorPools")).Cast<object>());
        Assert.Equal(1UL, Get(presenter, "_completedTimeline"));

        if (route != "shutdown")
        {
            scheduler.Finish();
            Assert.Equal(2UL, Get(presenter, "_submitTimeline"));
            Assert.Equal(1, device.Submits[1].Signals);
            Assert.Empty((IEnumerable)Get(presenter, "_pendingGuestSubmissions"));
            Invoke(presenter, "CollectCompletedGuestSubmissions", false);
            Assert.Equal(2UL, Get(presenter, "_completedTimeline"));
        }
    }

    private static object NewNested(string name) =>
        Activator.CreateInstance(PresenterType.GetNestedType(name, BindingFlags.NonPublic)!, nonPublic: true)!;

    private static object Get(object target, string name) => target.GetType().GetField(name, InstanceMembers)!.GetValue(target)!;

    private static void Set(object target, string name, object value) => target.GetType().GetField(name, InstanceMembers)!.SetValue(target, value);

    private static void Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, InstanceMembers)!.Invoke(target, args);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.GpuCommands;
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
    public void FlipCapacity_SuspendsAtTheBoundAndReopensAfterDequeue()
    {
        var queueField = typeof(VulkanVideoPresenter).GetField("_pendingGuestImagePresentations", BindingFlags.Static | BindingFlags.NonPublic)!;
        var queue = queueField.GetValue(null)!;
        var enqueue = queue.GetType().GetMethod("Enqueue")!;
        var dequeue = queue.GetType().GetMethod("Dequeue")!;
        var clear = queue.GetType().GetMethod("Clear")!;
        var presentation = Activator.CreateInstance(queue.GetType().GenericTypeArguments[0])!;
        var host = (ICommandStreamHost)RuntimeHelpers.GetUninitializedObject(PresenterType);
        var capacity = (int)typeof(VulkanVideoPresenter).GetField("MaxPendingGuestFlipVersions", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Assert.Empty((IEnumerable)queue);
        try
        {
            for (var index = 0; index < capacity; index++)
            {
                Assert.True(host.HasFlipSlot());
                enqueue.Invoke(queue, new[] { presentation });
            }
            Assert.False(host.HasFlipSlot());
            dequeue.Invoke(queue, null);
            Assert.True(host.HasFlipSlot());
        }
        finally
        {
            clear.Invoke(queue, null);
        }
    }

    [Fact]
    public void ClosedPortFrame_IsRemovedBeforeCheckingTheNextFramesGpuTick()
    {
        var context = new CpuContext(new FakeCpuMemory(0x10000, 0x1000), Generation.Gen5);
        var closedHandle = VideoOutExports.VideoOutOpen(context);
        var liveHandle = VideoOutExports.VideoOutOpen(context);
        Assert.True(closedHandle > 0);
        Assert.True(liveHandle > 0);
        Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(closedHandle, -1, 0, 0, true, out var closedRequest));
        Assert.Equal(0, VideoOutExports.TryReserveFlipRequest(liveHandle, -1, 0, 0, true, out var liveRequest));
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var queue = typeof(VulkanVideoPresenter).GetField("_pendingGuestImagePresentations", staticMembers)!.GetValue(null)!;
        var presentationType = queue.GetType().GenericTypeArguments[0];
        var enqueue = queue.GetType().GetMethod("Enqueue")!;
        var clear = queue.GetType().GetMethod("Clear")!;
        var presenter = RuntimeHelpers.GetUninitializedObject(PresenterType);
        var commands = new CommandStreamQueue((ICommandStreamHost)presenter);
        Set(presenter, "_commandStream", commands);
        Assert.Empty((IEnumerable)queue);
        try
        {
            foreach (var requestId in new[] { closedRequest, liveRequest })
            {
                var frame = Activator.CreateInstance(presentationType)!;
                presentationType.GetProperty("Sequence")!.SetValue(frame, requestId == closedRequest ? 1L : 2L);
                presentationType.GetProperty("FlipRequestId")!.SetValue(frame, requestId);
                presentationType.GetProperty("RequiredTick")!.SetValue(frame, ulong.MaxValue);
                enqueue.Invoke(queue, new[] { frame });
            }
            context[CpuRegister.Rdi] = (ulong)closedHandle;
            VideoOutExports.VideoOutClose(context);
            Assert.True((bool)PresenterType.GetMethod("HasReadyPresentationLocked", InstanceMembers)!.Invoke(presenter, null)!);
            var take = PresenterType.GetMethod("TryTakePresentation", InstanceMembers)!;
            Assert.False((bool)take.Invoke(presenter, new object?[] { null })!);
            var remaining = Assert.Single(((IEnumerable)queue).Cast<object>());
            Assert.Equal(liveRequest, presentationType.GetProperty("FlipRequestId")!.GetValue(remaining));
            Assert.True(VideoOutExports.IsFlipPresentationPending(liveRequest));
            Assert.False((bool)PresenterType.GetMethod("HasReadyPresentationLocked", InstanceMembers)!.Invoke(presenter, null)!);
            Assert.Equal(1L, (long)commands.BlockedRetries);
        }
        finally
        {
            clear.Invoke(queue, null);
            context[CpuRegister.Rdi] = (ulong)closedHandle;
            VideoOutExports.VideoOutClose(context);
            context[CpuRegister.Rdi] = (ulong)liveHandle;
            VideoOutExports.VideoOutClose(context);
        }
    }

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
    public void RenderProfile_DetailScopesRestoreTheRenderPhase()
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
            RenderPhaseProfile.Phase.PresentationPreparation,
            RenderPhaseProfile.Phase.CommandMemorySync,
            RenderPhaseProfile.Phase.CommandMemoryRead,
            RenderPhaseProfile.Phase.CommandGpuWait,
            RenderPhaseProfile.Phase.CommandMemoryTransfer,
            RenderPhaseProfile.Phase.CommandEndOfPipe,
            RenderPhaseProfile.Phase.CommandDrawTranslation,
            RenderPhaseProfile.Phase.CommandDispatchTranslation,
            RenderPhaseProfile.Phase.GeometrySnapshotValidation,
            RenderPhaseProfile.Phase.CommandDrawStateCreation,
            RenderPhaseProfile.Phase.GpuCompletionWait,
            RenderPhaseProfile.Phase.SubmissionCapacity,
            RenderPhaseProfile.Phase.CompletedSubmissionCleanup,
            RenderPhaseProfile.Phase.MovieFramePolling,
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
    public void RenderProfile_CommandReadsCountOnlyInsideRenderScopes()
    {
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var profile = typeof(RenderPhaseProfile);
        var calls = (long[])profile.GetField("_commandReadCalls", staticMembers)!.GetValue(null)!;
        var bytes = (long[])profile.GetField("_commandReadBytes", staticMembers)!.GetValue(null)!;
        var category = RenderPhaseProfile.CommandReadKind.RegisterTable;
        var index = (int)category;
        var initialCalls = calls[index];
        var initialBytes = bytes[index];
        try
        {
            RenderPhaseProfile.RecordCommandRead(category, 4);
            Assert.Equal(initialCalls, calls[index]);
            Assert.Equal(initialBytes, bytes[index]);
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CommandStream))
            {
                RenderPhaseProfile.RecordCommandRead(category, 4);
                RenderPhaseProfile.RecordCommandRead(category, 8);
            }
            Assert.Equal(initialCalls + (RenderPhaseProfile.Enabled ? 2 : 0), calls[index]);
            Assert.Equal(initialBytes + (RenderPhaseProfile.Enabled ? 12 : 0), bytes[index]);
        }
        finally
        {
            calls[index] = initialCalls;
            bytes[index] = initialBytes;
        }
    }

    [Fact]
    public void RenderProfile_SequentialPhasesRestoreTheParentAfterAnException()
    {
        const BindingFlags staticMembers = BindingFlags.Static | BindingFlags.NonPublic;
        var profile = typeof(RenderPhaseProfile);
        var current = profile.GetField("_current", staticMembers)!;
        var depth = profile.GetField("_scopeDepth", staticMembers)!;
        using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CommandDrawTranslation))
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var creation = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandDrawStateCreation);
                creation.SwitchPhase(RenderPhaseProfile.Phase.DrawVertexShaderSetup);
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawVertexEvaluation)) { }
                if (RenderPhaseProfile.Enabled)
                {
                    Assert.Equal(RenderPhaseProfile.Phase.DrawVertexShaderSetup, current.GetValue(null));
                }
                creation.SwitchPhase(RenderPhaseProfile.Phase.DrawBindingAssembly);
                if (RenderPhaseProfile.Enabled)
                {
                    Assert.Equal(RenderPhaseProfile.Phase.DrawBindingAssembly, current.GetValue(null));
                    Assert.Equal(2, (int)depth.GetValue(null)!);
                }
                throw new InvalidOperationException();
            }));
            if (RenderPhaseProfile.Enabled)
            {
                Assert.Equal(RenderPhaseProfile.Phase.CommandDrawTranslation, current.GetValue(null));
                Assert.Equal(1, (int)depth.GetValue(null)!);
            }
        }
        Assert.Equal(0, (int)depth.GetValue(null)!);
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
        Set(presenter, "_commandStream", new CommandStreamQueue((ICommandStreamHost)presenter));
        foreach (var name in new[]
        {
            "_batchResources", "_batchRetireBuffers",
            "_pendingGuestSubmissions",
            "_deferredGuestImageVersionDestroys",
        })
        {
            var field = PresenterType.GetField(name, InstanceMembers)!;
            field.SetValue(presenter, Activator.CreateInstance(field.FieldType, nonPublic: true));
        }

        Set(presenter, "_batchOpen", true);
        Set(presenter, "_activeGuestQueue", new VulkanGuestQueueIdentity("test.queue", 7));

        var resources = NewNested("SubmissionUploadResources");
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
            case "flush": Invoke(presenter, "FlushBatchedGuestCommands"); break;
        }

        Assert.Equal(1UL, Assert.Single(device.Submits).Tick);
        Assert.Equal(1, device.Submits[0].Signals);
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
            device.Complete(1);
            device.CompleteOnSubmit = true;
        }

        Invoke(presenter, "CollectCompletedGuestSubmissions", false);
        Assert.Empty((IEnumerable)Get(presenter, "_pendingGuestSubmissions"));
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

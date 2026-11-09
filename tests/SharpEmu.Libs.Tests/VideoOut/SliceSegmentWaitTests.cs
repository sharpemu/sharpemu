// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class SliceSegmentWaitTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type PresenterType = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;
    private static readonly Type SliceType = typeof(VulkanVideoPresenter).GetNestedType("RenderSlice", BindingFlags.NonPublic)!;
    private static readonly Type SegmentType = typeof(VulkanVideoPresenter).GetNestedType("SliceSegment", BindingFlags.NonPublic)!;

    [Fact]
    public void RelayCommandPostedDuringABlockedSegmentWaitRunsBeforeTheSegmentArrives()
    {
        var (presenter, relay) = CreatePresenter();
        var slice = Activator.CreateInstance(SliceType, nonPublic: true)!;
        var order = new ConcurrentQueue<string>();
        object? returned = null;
        var waiter = new Thread(() =>
        {
            relay.BindCurrentThread();
            returned = NextSegment(presenter, slice, relay.RunPendingCommands);
            order.Enqueue("segment");
        }) { IsBackground = true };
        waiter.Start();
        WaitUntilBlocked(presenter, slice);

        relay.RunOnGpuQueue(() => order.Enqueue("relay"));
        Assert.True(waiter.IsAlive);

        var segment = Activator.CreateInstance(SegmentType, nonPublic: true)!;
        Enqueue(slice, segment);
        Progress(slice).Set();
        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
        Assert.Same(segment, returned);
        Assert.Equal(["relay", "segment"], order);
    }

    [Fact]
    public void FinishedSliceWithoutSegmentsReturnsNullAfterRunningPendingRelayWork()
    {
        var (presenter, relay) = CreatePresenter();
        var slice = Activator.CreateInstance(SliceType, nonPublic: true)!;
        var waiter = new Thread(() =>
        {
            relay.BindCurrentThread();
            Assert.Null(NextSegment(presenter, slice, relay.RunPendingCommands));
        }) { IsBackground = true };
        waiter.Start();
        WaitUntilBlocked(presenter, slice);

        var ran = false;
        relay.RunOnGpuQueue(() => ran = true);
        Assert.True(ran);
        ((ManualResetEventSlim)SliceType.GetField("Done")!.GetValue(slice)!).Set();
        Progress(slice).Set();
        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
    }

    private static (object Presenter, GpuWorkerRelay Relay) CreatePresenter()
    {
        var presenter = RuntimeHelpers.GetUninitializedObject(PresenterType);
        var wake = PresenterType.GetMethod("WakeRenderThreadForRelay", InstanceMembers)!.CreateDelegate<Action>(presenter);
        var relay = new GpuWorkerRelay(wake);
        PresenterType.GetField("_relay", InstanceMembers)!.SetValue(presenter, relay);
        return (presenter, relay);
    }

    private static object? NextSegment(object presenter, object slice, Action whileWaiting) =>
        PresenterType.GetMethod("NextSegment", InstanceMembers)!.Invoke(presenter, [slice, whileWaiting]);

    private static void WaitUntilBlocked(object presenter, object slice)
    {
        var field = PresenterType.GetField("_segmentWaitSlice", InstanceMembers)!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ReferenceEquals(field.GetValue(presenter), slice))
        {
            Assert.True(DateTime.UtcNow < deadline, "The segment wait never blocked.");
            Thread.Yield();
        }
    }

    private static ManualResetEventSlim Progress(object slice) => (ManualResetEventSlim)SliceType.GetField("Progress")!.GetValue(slice)!;

    private static void Enqueue(object slice, object segment)
    {
        var segments = SliceType.GetField("Segments")!.GetValue(slice)!;
        segments.GetType().GetMethod("Enqueue")!.Invoke(segments, [segment]);
    }
}

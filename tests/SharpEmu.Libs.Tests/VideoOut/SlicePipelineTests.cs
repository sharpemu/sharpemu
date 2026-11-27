// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class SlicePipelineTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private sealed record Marker(int Position) : AgcExports.DeferredWork;

    // Use the real cutter, queues and joiner. Manually publish the terminal segment so the
    // first serial callback can observe precisely which later work has already been dispatched.
    [Theory]
    [InlineData(41, true)]
    [InlineData(6, false)]
    [InlineData(512, false)]
    public void SuffixAndFirstWavePreserveEveryStreamPosition(int count, bool terminal)
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan!);
        var owner = typeof(VulkanVideoPresenter);
        var workers = owner.GetNestedType("RenderWorkerPool", BindingFlags.NonPublic)!;
        var pool = Activator.CreateInstance(workers, presenter.Instance, 0)!;
        // Advertise four lanes but leave their queues undrained until our simulated worker publishes.
        workers.GetField("_threads", PresenterUnderTest.InstanceMembers)!.SetValue(pool, new Thread[4]);
        using var semaphore = (SemaphoreSlim)workers.GetField("_available", PresenterUnderTest.InstanceMembers)!.GetValue(pool)!;
        presenter.SetField("_workers", pool);
        try
        {
            presenter.Run(() =>
            {
                var observed = new List<int>();
                var type = owner.GetNestedType("CommandThread", BindingFlags.NonPublic)!;
                Action<AgcExports.DeferredWork> execute = work => observed.Add(((Marker)work).Position);
                var thread = Activator.CreateInstance(type, [presenter.Instance, null, null, execute, null])!;
                var enqueue = type.GetMethod("EnqueueDeferred")!.CreateDelegate<Func<AgcExports.DeferredWork, bool, long>>(thread);
                for (var i = 0; i < count; i++) enqueue(new Marker(i), true);
                type.GetMethod("Post")!.Invoke(thread, [(Action)(() => observed.Add(count)), "boundary"]);
                type.GetMethod("FillUnits", PresenterUnderTest.InstanceMembers)!.Invoke(thread, null);
                var units = (IList)type.GetField("_units", PresenterUnderTest.InstanceMembers)!.GetValue(thread)!;
                var sliceField = units[0]!.GetType().GetField("Slice")!;
                var slices = units.Cast<object>().Select(unit => sliceField.GetValue(unit)).OfType<object>().ToList();
                if (count == 512)
                {
                    var sizes = slices.Select(s => ((IList)s.GetType().GetField("Items")!.GetValue(s)!).Count);
                    Assert.Equal(new[] { 64, 64, 64, 64, 256 }, sizes);
                }
                if (terminal)
                {
                    var slice = Assert.Single(slices);
                    var sliceType = slice.GetType();
                    Assert.True((bool)sliceType.GetMethod("TryClaim")!.Invoke(slice, null)!);
                    var segmentType = owner.GetNestedType("SliceSegment", BindingFlags.NonPublic)!;
                    var segment = Activator.CreateInstance(segmentType)!;
                    segmentType.GetField("SerialReason")!.SetValue(segment, "ordered fallback");
                    segmentType.GetField("StopsRecording")!.SetValue(segment, true);
                    var segments = sliceType.GetField("Segments")!.GetValue(slice)!;
                    segments.GetType().GetMethod("Enqueue")!.Invoke(segments, [segment]);
                    sliceType.GetField("Resume")!.SetValue(slice, 1);
                    ((ManualResetEventSlim)sliceType.GetField("Done")!.GetValue(slice)!).Set();
                }
                var initialUnits = units.Count;
                Action between = () =>
                {
                    if (terminal && observed.Count == 1) Assert.True(units.Count > initialUnits);
                };
                var ran = type.GetMethod("RunBatch")!.Invoke(thread, [long.MaxValue, int.MaxValue, between]);
                Assert.Equal(count + 1, ran);
                Assert.Equal(Enumerable.Range(0, count + 1), observed);
            });
        }
        finally
        {
            presenter.SetField("_workers", null!);
        }
    }
}

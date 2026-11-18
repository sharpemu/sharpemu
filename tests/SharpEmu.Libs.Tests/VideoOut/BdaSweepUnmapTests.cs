// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

// Launches 341/342 (02.002, ezo40) failed fast in RecordSlicePrologue -> PrepareBda -> FlushSweepUploads reading a
// buffer over memory the guest had unmapped. A guest unmap marks the buffers it covers CPU-dirty, so any device-address
// sweep that still includes the range afterwards reads memory that is gone.
[Collection(SchedulingStateCollection.Name)]
public sealed class BdaSweepUnmapTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly Type SegmentType = typeof(VulkanVideoPresenter).GetNestedType("SliceSegment", BindingFlags.NonPublic)!;
    private const BindingFlags Members = PresenterUnderTest.InstanceMembers;

    // A worker captured the GPU spans for a slice's sweep, the render thread then ran the guest munmap from the relay
    // between items, and the join swept the captured spans.
    [Fact]
    public void ASegmentJoinedAfterAGuestUnmapDoesNotSweepTheUnmappedRange()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var fatal = new FatalScope();
        using var p = new PresenterUnderTest(fixture.Vulkan!);
        var h = p.Harness;
        GuestGpuMemoryHook.Attach(h.Gpu);
        try
        {
            var kept = h.MapBacked(0x20000, ReadWrite);
            var gone = h.MapBacked(0x20000, ReadWrite);
            var (keptBuffer, keptOffset) = p.Run(() => h.Cache.ObtainBuffer(kept, 0x8000, isWritten: false));
            p.Run(() => h.Cache.ObtainBuffer(gone, 0x8000, isWritten: false));

            // What PrepareDeviceAddresses records on a worker.
            var segment = Activator.CreateInstance(SegmentType, nonPublic: true)!;
            var requests = SegmentType.GetField("Requests")!.GetValue(segment)!;
            var host = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.RecordingHostType);
            var spans = PresenterUnderTest.RecordingHostType.GetMethod("DeviceAddressSpans", Members)!.Invoke(host, [h.Gpu])!;
            var mapping = PresenterUnderTest.RecordingHostType.GetField("_bdaSpanMapping", Members)!.GetValue(host)!;
            requests.GetType().GetField("NeedsBdaSweep")!.SetValue(requests, true);
            requests.GetType().GetField("BdaSpans")!.SetValue(requests, spans);
            requests.GetType().GetField("BdaMapping")!.SetValue(requests, mapping);

            // The guest unmaps before the render thread joins the segment.
            h.Gpu.Unregister(gone, 0x20000);
            Assert.True(h.Cache.HasCpuDirtyPages(gone, 0x8000));
            Assert.True(h.Memory.TryUnmapBacked(gone, 0x20000));
            Assert.False(h.Memory.TryRead(gone, new byte[16]), "unmapped read");

            // The mapping that stays is still swept.
            h.Write(kept + 0x4000, ImageTestHarness.Pattern(0x20, 3));
            Assert.True(h.Store.MarkCpuWrite(kept + 0x4000, 0x20));

            p.Run(() => p.InvokeMethod("RecordSlicePrologue", requests));

            Assert.Empty(fatal.Messages);
            Assert.False(h.Cache.HasCpuDirtyPages(kept, 0x8000), "kept swept");
            Assert.Equal(ImageTestHarness.Pattern(0x20, 3), h.ReadBack(keptBuffer, keptOffset + 0x4000, 0x20));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
            h.Shutdown();
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

[Collection(SchedulingStateCollection.Name)]
public sealed class VulkanCommandProfileTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(100UL, 150UL, 64U, 50UL)]
    [InlineData(ulong.MaxValue - 3, 2UL, 64U, 6UL)]
    [InlineData(254UL, 3UL, 8U, 5UL)]
    [InlineData(0x1FEUL, 0x203UL, 8U, 5UL)]
    public void ElapsedTicksHandlesCounterWrap(ulong start, ulong end, uint validBits, ulong expected)
    {
        Assert.Equal(expected, VulkanCommandProfile.ElapsedTicks(start, end, validBits));
    }

    [Fact]
    public void DefaultDeviceDoesNotCreateQueryResources()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using (var device = fixture.Vulkan.NewTickDevice())
            Assert.Null(device.CommandProfile);
        fixture.Vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void CompletedBufferReuseCollectsQueriesAndPreservesTheCapacityLimit()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var device = new VulkanTickDevice(vulkan.Vk, vulkan.Device, vulkan.Queue, vulkan.QueueFamily, vulkan.QueueGate, vulkan.Physical);
        var profile = Assert.IsType<VulkanCommandProfile>(device.CommandProfile);
        using (device)
        {
            if (!profile.Supported) return;
            var buffer = device.AllocateBuffers(1)[0];
            var command = new CommandBuffer(buffer);
            device.BeginBuffer(buffer);
            for (var index = 0; index < VulkanCommandProfile.QueryCapacity * 2; index++)
                profile.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            device.EndBuffer(buffer);
            Assert.Equal(0UL, profile.CollectedIntervals);
            SubmitAndWait(device, buffer, 1);
            device.BeginBuffer(buffer);
            Assert.Equal((ulong)VulkanCommandProfile.QueryCapacity - 1, profile.CollectedIntervals);
            Assert.Equal(1, profile.ProfiledBufferCount);
            device.EndBuffer(buffer);
            SubmitAndWait(device, buffer, 2);
        }
        Assert.Equal((ulong)VulkanCommandProfile.QueryCapacity, profile.CollectedIntervals);
        vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public unsafe void MarkersInsideDynamicRenderingPassValidation()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan) || !vulkan.SupportsDynamicRendering || vulkan.ApiVersion < Vk.Version13) return;
        using (var device = new VulkanTickDevice(vulkan.Vk, vulkan.Device, vulkan.Queue, vulkan.QueueFamily, vulkan.QueueGate, vulkan.Physical))
        {
            var buffer = device.AllocateBuffers(1)[0];
            var command = new CommandBuffer(buffer);
            device.BeginBuffer(buffer);
            var rendering = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(default, new Extent2D(1, 1)),
                LayerCount = 1,
            };
            vulkan.Vk.CmdBeginRendering(command, &rendering);
            device.CommandProfile!.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
            device.CommandProfile.WriteMarker(command, VulkanCommandProfile.IntervalKind.Draw, 1, 3, 1);
            vulkan.Vk.CmdEndRendering(command);
            device.EndBuffer(buffer);
            SubmitAndWait(device, buffer, 1);
        }
        vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void UnsubmittedQueriesAreNotCollected()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var device = new VulkanTickDevice(vulkan.Vk, vulkan.Device, vulkan.Queue, vulkan.QueueFamily, vulkan.QueueGate, vulkan.Physical);
        var profile = Assert.IsType<VulkanCommandProfile>(device.CommandProfile);
        using (device)
        {
            var buffer = device.AllocateBuffers(1)[0];
            device.BeginBuffer(buffer);
            device.EndBuffer(buffer);
        }
        Assert.Equal(0UL, profile.CollectedIntervals);
        vulkan.AssertNoValidationMessages();
    }

    private static void SubmitAndWait(VulkanTickDevice device, nint buffer, ulong tick)
    {
        var submission = new SubmitBundle();
        submission.AddSignal(device.TimelineHandle, tick);
        lock (device.QueueGate)
            Assert.True(device.TrySubmit(buffer, submission, out var submitFailure), submitFailure);
        Assert.True(device.TryWaitTimeline(tick, out var waitFailure), waitFailure);
    }
}

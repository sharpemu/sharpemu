// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The persistent heap that runtime image descriptors index: how big it may be, and how slots are given out and
// taken back as views come and go.
[Collection(SchedulingStateCollection.Name)]
public sealed class RuntimeDescriptorHeapTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static RuntimeDescriptorHeap.Limits Limits(uint perStage, uint perStageUpdateAfterBind, uint set, uint total, uint samplers = 4096) =>
        new(perStage, samplers, perStageUpdateAfterBind, samplers, set, samplers, total);

    [Fact]
    public void CapacityIsCappedWhereTheDeviceAllowsMore()
    {
        var (images, samplers) = RuntimeDescriptorHeap.Capacities(Limits(1_000_000, 1_000_000, 1_000_000, uint.MaxValue));

        Assert.Equal(16u * 1024, images);
        Assert.Equal(2048u, samplers);
    }

    [Fact]
    public void CapacityFollowsTheSmallestLimit()
    {
        Assert.Equal(300u, RuntimeDescriptorHeap.Capacities(Limits(1_000_000, 300, 1_000_000, uint.MaxValue)).Images);
        Assert.Equal(200u, RuntimeDescriptorHeap.Capacities(Limits(200, 1_000_000, 1_000_000, uint.MaxValue)).Images);
        Assert.Equal(150u, RuntimeDescriptorHeap.Capacities(Limits(1_000_000, 1_000_000, 150, uint.MaxValue)).Images);
        Assert.Equal(64u, RuntimeDescriptorHeap.Capacities(Limits(1_000_000, 1_000_000, 1_000_000, uint.MaxValue, samplers: 64)).Samplers);
    }

    [Fact]
    public void SamplersTakeTheirShareOfThePoolWideTotalFirst()
    {
        var (images, samplers) = RuntimeDescriptorHeap.Capacities(Limits(1_000_000, 1_000_000, 1_000_000, total: 3000));

        Assert.Equal(2048u, samplers);
        Assert.Equal(3000u - 2048u, images);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 4096u)]
    public void ADeviceWithNoRoomHasNoHeap(uint images, uint total)
    {
        Assert.Equal((0u, 0u), RuntimeDescriptorHeap.Capacities(Limits(images, images, images, total)));
        Assert.Equal((0u, 0u), RuntimeDescriptorHeap.Capacities(Limits(1000, 1000, 1000, total: 2048)));
    }

    private static ImageDescription Describe(ulong guestOffset)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(ArrayBackedSpace.Base + guestOffset, 64);
        description.PixelFormat = Format.R32Sfloat;
        description.GuestFormat = GuestPixelFormat.Bits32Float;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = new SubresourceCount(1, 1);
        description.Pitch = 1;
        description.BytesPerBlock = 4;
        return description;
    }

    private static ImageView ViewOf(CachedImage image) =>
        image.GetOrCreateView(ImageViewDescription.Default with { Format = Format.R32Sfloat, Usage = ImageUsageFlags.SampledBit });

    [Fact]
    public void SlotsAreStablePerViewAndLayoutAndFreedSlotsAreReusedAfterTheTickCompletes()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, runtimeDescriptors: true))
        {
            return;
        }

        using var harness = new ImageTestHarness(vulkan);
        using var heap = harness.Run(() => new RuntimeDescriptorHeap(harness.Device, harness.Scheduler, vulkan.RuntimeDescriptorLimits));
        var first = harness.CreateImage(Describe(0));
        var second = harness.CreateImage(Describe(0x1000));
        var firstView = ViewOf(first);
        var secondView = ViewOf(second);

        var a = heap.GetOrCreateImageSlot(firstView, ImageLayout.ShaderReadOnlyOptimal);
        var b = heap.GetOrCreateImageSlot(secondView, ImageLayout.ShaderReadOnlyOptimal);
        var aGeneral = heap.GetOrCreateImageSlot(firstView, ImageLayout.General);

        Assert.NotEqual(0u, a);
        Assert.Equal(a, heap.GetOrCreateImageSlot(firstView, ImageLayout.ShaderReadOnlyOptimal));
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, aGeneral);
        Assert.Equal(0u, heap.GetOrCreateImageSlot(default, ImageLayout.ShaderReadOnlyOptimal));

        harness.Run(() => heap.InvalidateViews([firstView]));
        // The slot stops naming the view at once, but is not handed out again before the tick completed.
        var fresh = heap.GetOrCreateImageSlot(firstView, ImageLayout.ShaderReadOnlyOptimal);
        Assert.NotEqual(a, fresh);
        harness.Finish();
        var third = harness.CreateImage(Describe(0x2000));
        var reused = heap.GetOrCreateImageSlot(ViewOf(third), ImageLayout.ShaderReadOnlyOptimal);
        Assert.Contains(reused, new[] { a, aGeneral });
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void SamplerSlotsAreStablePerSampler()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, runtimeDescriptors: true))
        {
            return;
        }

        using var harness = new ImageTestHarness(vulkan);
        using var heap = harness.Run(() => new RuntimeDescriptorHeap(harness.Device, harness.Scheduler, vulkan.RuntimeDescriptorLimits));
        using var runner = new SamplerFactory(harness);
        var linear = runner.Create(Filter.Linear);
        var nearest = runner.Create(Filter.Nearest);

        var linearSlot = heap.GetOrCreateSamplerSlot(linear);
        var nearestSlot = heap.GetOrCreateSamplerSlot(nearest);

        Assert.NotEqual(0u, linearSlot);
        Assert.NotEqual(linearSlot, nearestSlot);
        Assert.Equal(linearSlot, heap.GetOrCreateSamplerSlot(linear));
        Assert.Equal(0u, heap.GetOrCreateSamplerSlot(default));
        harness.AssertNoValidationMessages();
    }

    private sealed unsafe class SamplerFactory(ImageTestHarness harness) : IDisposable
    {
        private readonly List<Sampler> _samplers = [];

        public Sampler Create(Filter filter)
        {
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = filter,
                MinFilter = filter,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
            };
            Assert.Equal(Result.Success, harness.Vk.CreateSampler(harness.Device.Device, &info, null, out var sampler));
            _samplers.Add(sampler);
            return sampler;
        }

        public void Dispose()
        {
            foreach (var sampler in _samplers)
            {
                harness.Vk.DestroySampler(harness.Device.Device, sampler, null);
            }
        }
    }
}

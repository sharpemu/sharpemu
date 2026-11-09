// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Descriptor sets come in batches from one pool; a full pool retires with its tick and is reused only once that tick completed.
[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class DescriptorHeapTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint LargeSamplerCount = 48;

    private static DescriptorSetLayout CreateSamplerLayout(ImageTestHarness harness, uint samplerCount)
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 37,
            DescriptorType = DescriptorType.Sampler,
            DescriptorCount = samplerCount,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        var create = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        Assert.Equal(Result.Success, harness.Vk.CreateDescriptorSetLayout(harness.Device.Device, &create, null, out var layout));
        return layout;
    }

    private static HashSet<ulong> Commit(DescriptorHeap heap, DescriptorSetLayout layout, int count, uint samplers = LargeSamplerCount)
    {
        var sets = new HashSet<ulong>();
        var demand = new DescriptorSetDemand(Samplers: samplers);
        for (var index = 0; index < count; index++)
        {
            Assert.True(sets.Add(heap.Commit(layout, in demand).Handle));
        }

        return sets;
    }

    // Port of the reference's large-set sequence: 22 commits fill the first pool, then the wait, then 21 more.
    [Fact]
    public void DescriptorHeapLargeSet()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(vulkan);
        var layout = CreateSamplerLayout(harness, LargeSamplerCount);
        DescriptorHeap? heap = null;
        try
        {
            harness.Run(() =>
            {
                heap = new DescriptorHeap(harness.Device, harness.Scheduler);
                var firstPool = heap.CurrentPool;
                var sets = Commit(heap, layout, 22);
                Assert.Equal(22, sets.Count);
                Assert.Equal(1, heap.PendingPoolCount);
                Assert.NotEqual(firstPool.Handle, heap.CurrentPool.Handle);
            });
            harness.Finish();
            harness.Run(() =>
            {
                Assert.Equal(21, Commit(heap!, layout, 21).Count);
                Assert.Equal(1, heap!.PendingPoolCount);
            });
        }
        finally
        {
            // The retired pools wait for their ticks; the finish submits the last one before the heap goes.
            harness.Finish();
            harness.Run(() =>
            {
                heap?.Dispose();
                harness.Vk.DestroyDescriptorSetLayout(harness.Device.Device, layout, null);
            });
        }

        vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void RetiredPool_IsResetOnlyAfterItsTickCompletes()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(vulkan);
        var layout = CreateSamplerLayout(harness, LargeSamplerCount);
        DescriptorHeap? heap = null;
        DescriptorPool firstPool = default;
        try
        {
            harness.Run(() =>
            {
                heap = new DescriptorHeap(harness.Device, harness.Scheduler);
                firstPool = heap.CurrentPool;
                // The first pool fills and retires; its tick is still open, so a fresh pool takes over.
                Commit(heap, layout, 22);
                Assert.Equal(1, heap.PendingPoolCount);
                Assert.NotEqual(firstPool.Handle, heap.CurrentPool.Handle);
                // The second pool fills too; the tick is still incomplete, so a third pool is created.
                Commit(heap, layout, 21);
                Assert.Equal(2, heap.PendingPoolCount);
                Assert.NotEqual(firstPool.Handle, heap.CurrentPool.Handle);
            });
            harness.Finish();
            harness.Run(() =>
            {
                // The tick completed: the next exhaustion resets the oldest pool and reuses it.
                Commit(heap!, layout, 22);
                Assert.Equal(2, heap!.PendingPoolCount);
                Assert.Equal(firstPool.Handle, heap.CurrentPool.Handle);
            });
        }
        finally
        {
            harness.Finish();
            harness.Run(() =>
            {
                heap?.Dispose();
                harness.Vk.DestroyDescriptorSetLayout(harness.Device.Device, layout, null);
            });
        }

        vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void Commit_HandsOutDistinctSetsPerLayoutAndRejectsANullLayout()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan))
        {
            return;
        }

        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(vulkan);
        var small = CreateSamplerLayout(harness, 1);
        var large = CreateSamplerLayout(harness, LargeSamplerCount);
        try
        {
            harness.Run(() =>
            {
                using var heap = new DescriptorHeap(harness.Device, harness.Scheduler);
                var smallSets = Commit(heap, small, (int)DescriptorHeap.SetBatch + 1, samplers: 1);
                var largeSets = Commit(heap, large, 3);
                Assert.Empty(smallSets.Intersect(largeSets));
                Assert.Equal(0, heap.PendingPoolCount);
                Assert.Throws<SchedulerFatalException>(() => heap.Commit(default, new DescriptorSetDemand(Samplers: 1)));
            });
        }
        finally
        {
            harness.Run(() =>
            {
                harness.Vk.DestroyDescriptorSetLayout(harness.Device.Device, small, null);
                harness.Vk.DestroyDescriptorSetLayout(harness.Device.Device, large, null);
            });
        }

        vulkan.AssertNoValidationMessages();
    }
}

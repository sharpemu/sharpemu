// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class SliceDescriptorHeapTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private sealed class Recording : IDisposable
    {
        internal readonly ImageTestHarness Harness;
        internal readonly DescriptorPoolStock Stock;
        internal readonly SliceDescriptorHeap Heap;
        internal readonly DescriptorSetLayout Layout;
        internal readonly GpuBuffer Buffer;
        private readonly DescriptorSetDemand _demand;
        private ulong[]? _words;

        internal Recording(HeadlessVulkan vk, uint count = 1)
        {
            Harness = new ImageTestHarness(vk);
            Stock = new DescriptorPoolStock(Harness.Device);
            Heap = new SliceDescriptorHeap(Harness.Device, Stock);
            Buffer = Harness.Upload([], 4096);
            _demand = new DescriptorSetDemand(StorageBuffers: count);
            Layout = CreateLayout(count);
        }

        internal DescriptorSetLayout CreateLayout(uint count)
        {
            var binding = new DescriptorSetLayoutBinding(0, DescriptorType.StorageBuffer, count, ShaderStageFlags.ComputeBit);
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1, PBindings = &binding };
            Assert.Equal(Result.Success, Harness.Vk.CreateDescriptorSetLayout(Harness.Device.Device, &info, null, out var layout));
            return layout;
        }

        internal DescriptorSet Write(GpuBuffer? buffer = null, ulong offset = 0, ulong range = 64,
            ulong pipeline = 1, DescriptorSetLayout? layout = null)
        {
            var info = new DescriptorBufferInfo((buffer ?? Buffer).Handle, offset, range);
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1, PBufferInfo = &info };
            var writes = new Span<WriteDescriptorSet>(&write, 1);
            return Heap.Commit(pipeline, layout ?? Layout, in _demand, DescriptorWriter.DescriptorKey(writes, ref _words), writes);
        }

        public void Dispose()
        {
            Harness.Finish();
            Stock.Dispose();
            Harness.Vk.DestroyDescriptorSetLayout(Harness.Device.Device, Layout, null);
            Harness.Dispose();
            Harness.Vulkan.AssertNoValidationMessages();
        }
    }

    [Fact]
    public void ReuseRequiresTheSamePipelineLayoutAndDescriptorWords()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var recording = new Recording(fixture.Vulkan);
        var otherLayout = recording.CreateLayout(1);
        try
        {
            var first = recording.Write();
            Assert.Equal(first, recording.Write());
            Assert.NotEqual(first, recording.Write(offset: 256));
            Assert.NotEqual(first, recording.Write(range: 32));
            Assert.NotEqual(first, recording.Write(pipeline: 2));
            Assert.NotEqual(first, recording.Write(layout: otherLayout));
            recording.Heap.TakePools().ForEach(recording.Stock.Return);
            Assert.Equal(first, recording.Write());
        }
        finally { recording.Harness.Vk.DestroyDescriptorSetLayout(recording.Harness.Device.Device, otherLayout, null); }
    }

    [Fact]
    public void DestroyingABoundBufferInvalidatesCompletedSets()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var recording = new Recording(fixture.Vulkan);
        var destroyed = recording.Harness.Upload([], 4096);
        var first = recording.Write(destroyed);
        var second = recording.Write();
        Assert.NotEqual(first, second);
        recording.Heap.TakePools().ForEach(recording.Stock.Return);
        destroyed.Dispose();

        // The surviving buffer has identical words. Its old set cannot hit after destruction.
        // The first idle set, previously pointing at the destroyed buffer, takes the new write.
        Assert.Equal(first, recording.Write());
        Assert.Equal(first, recording.Write());
    }

    [Fact]
    public void DestroyingImageViewsAndSamplersInvalidatesWrittenSets()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var recording = new Recording(fixture.Vulkan);
        var image = recording.Harness.CreateImage(CachedImageTests.Color2D(16, 16));
        image.GetOrCreateView(ImageViewDescription.Default with { Format = image.Backing.Format, Usage = ImageUsageFlags.SampledBit });
        var first = recording.Write();
        image.Dispose();
        var second = recording.Write();
        Assert.NotEqual(first, second);

        using (var samplers = new SamplerStore(recording.Harness.Device))
        {
            samplers.GetSampler(new SamplerDescriptorWords([0, 0, 0, 0]), false);
            Assert.Equal(second, recording.Write());
        }
        Assert.NotEqual(second, recording.Write());
    }

    [Fact]
    public void SubmittedPoolsRemainExclusiveUntilCompletion()
    {
        var vk = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vk)) return;
        using var recording = new Recording(vk);
        var layout = recording.Layout;
        var info = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &layout };
        Assert.Equal(Result.Success, vk.Vk.CreatePipelineLayout(vk.Device, &info, null, out var pipelineLayout));
        try
        {
            var harness = recording.Harness;
            harness.Run(() =>
            {
                var first = recording.Write();
                vk.Vk.CmdBindDescriptorSets(new CommandBuffer(harness.Scheduler.Current.Handle), PipelineBindPoint.Compute,
                    pipelineLayout, 0, 1, &first, 0, null);
                // Rewriting this bound set would invalidate the recorded command.
                Assert.Equal(first, recording.Write());
                var submitted = recording.Heap.TakePools();
                harness.Scheduler.QueueCompletionAction(() => submitted.ForEach(recording.Stock.Return));
                Assert.NotEqual(first, recording.Write());
                var pending = recording.Heap.TakePools();
                harness.Scheduler.Finish();
                Assert.Equal(first, recording.Write());
                pending.ForEach(recording.Stock.Return);
            });
            harness.AssertNoValidationMessages();
        }
        finally
        {
            recording.Harness.Finish();
            vk.Vk.DestroyPipelineLayout(vk.Device, pipelineLayout, null);
        }
    }

    [Fact]
    public void SaturatedPoolFallsBackAndMakesRoomForNewLayouts()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var recording = new Recording(fixture.Vulkan);
        var sets = new HashSet<DescriptorSet>();
        for (ulong pipeline = 1; pipeline <= 65; pipeline++)
            Assert.True(sets.Add(recording.Write(pipeline: pipeline)));
        var pools = recording.Heap.TakePools();
        Assert.Equal(2, pools.Count);
        pools.ForEach(recording.Stock.Return);

        Assert.NotEqual(recording.Write(pipeline: 66), recording.Write(pipeline: 66));
        recording.Heap.TakePools().ForEach(recording.Stock.Return);
        Assert.Equal(recording.Write(pipeline: 66), recording.Write(pipeline: 66));
    }

    [Fact]
    public void OutstandingSlicesCannotExceedTheRetainedPoolLimit()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var recording = new Recording(fixture.Vulkan);
        var outstanding = new HashSet<DescriptorPool>();
        for (var i = 0; i < 64; i++)
            Assert.True(outstanding.Add(Assert.IsType<RetainedDescriptorPool>(recording.Stock.RentRetained()).Pool));
        Assert.Null(recording.Stock.RentRetained());
        var completed = outstanding.First();
        recording.Stock.Return(completed);
        Assert.Equal(completed, recording.Stock.RentRetained()!.Pool);
    }

    [Fact]
    public void PartialArrayWritesAlwaysUseFreshSets()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan)) return;
        using var recording = new Recording(fixture.Vulkan, count: 2);
        Assert.NotEqual(recording.Write(), recording.Write());
    }
}

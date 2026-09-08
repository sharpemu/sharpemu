// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Reads every sample of texel (0, 0) of a multisample depth image as raw float bits.
internal sealed unsafe class MultisampleDepthSampleReader : IDisposable
{
    private readonly HeadlessVulkan _vulkan;
    private readonly SubmissionScheduler _scheduler;
    private readonly Action<Action> _run;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly DescriptorPool _pool;
    private readonly Pipeline _pipeline;

    public MultisampleDepthSampleReader(ImageTestHarness harness)
        : this(harness.Vulkan, harness.Scheduler, harness.Run)
    {
    }

    // Any harness: the run callback executes work on the scheduler's worker thread.
    public MultisampleDepthSampleReader(HeadlessVulkan vulkan, SubmissionScheduler scheduler, Action<Action> run)
    {
        _vulkan = vulkan;
        _scheduler = scheduler;
        _run = run;
        var vk = vulkan.Vk;
        var device = vulkan.DeviceInfo.Device;
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.SampledImage, 1, ShaderStageFlags.ComputeBit);
        bindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        Require(vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _setLayout));
        var push = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, 4);
        fixed (DescriptorSetLayout* layout = &_setLayout)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = layout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &push,
            };
            Require(vk.CreatePipelineLayout(device, &pipelineLayoutInfo, null, out _pipelineLayout));
        }

        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.SampledImage, 64);
        sizes[1] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 64);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 64, PoolSizeCount = 2, PPoolSizes = sizes };
        Require(vk.CreateDescriptorPool(device, &poolInfo, null, out _pool));

        var spirv = BlitShaders.CreateMultisampleDepthSampleReadback();
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Require(vk.CreateShaderModule(device, &moduleInfo, null, out module));
        }

        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry },
                Layout = _pipelineLayout,
            };
            Require(vk.CreateComputePipelines(device, default, 1, &info, null, out _pipeline));
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
            vk.DestroyShaderModule(device, module, null);
        }
    }

    private static void Require(Result result)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"Vulkan call failed with {result}");
        }
    }

    public uint[] ReadSamples(CachedImage depth, uint samples)
    {
        var vk = _vulkan.Vk;
        var device = _vulkan.DeviceInfo.Device;
        using var output = new GpuBuffer(_vulkan.DeviceInfo, _scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, samples * 4);
        var bytes = Array.Empty<byte>();
        _run(() =>
        {
            output.Fill(0, output.Size, 0xFFFFFFFF);
            var command = new CommandBuffer(_scheduler.Current.Handle);
            depth.Transition(ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, null, command);
            var view = depth.GetOrCreateView(ImageViewDescription.Default with { Format = depth.Backing.Format, Aspect = ImageAspectFlags.DepthBit, Usage = ImageUsageFlags.SampledBit });
            var setLayout = _setLayout;
            var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
            Require(vk.AllocateDescriptorSets(device, &allocateInfo, out var set));
            var imageInfo = new DescriptorImageInfo { ImageView = view, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            var bufferInfo = new DescriptorBufferInfo(output.Handle, 0, output.Size);
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.SampledImage, PImageInfo = &imageInfo };
            writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
            vk.UpdateDescriptorSets(device, 2, writes, 0, null);
            vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
            vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
            var count = samples;
            vk.CmdPushConstants(command, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, 4, &count);
            vk.CmdDispatch(command, 1, 1, 1);
            var barrier = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = output.Handle,
                Offset = 0,
                Size = output.Size,
            };
            vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &barrier, 0, null);
            _scheduler.Finish();
            output.Invalidate(0, output.Size);
            bytes = output.Mapped[..(int)(samples * 4)].ToArray();
        });
        var result = new uint[samples];
        for (var index = 0; index < samples; index++)
        {
            result[index] = BitConverter.ToUInt32(bytes, index * 4);
        }

        return result;
    }

    public void Dispose()
    {
        var vk = _vulkan.Vk;
        var device = _vulkan.DeviceInfo.Device;
        _run(() => _scheduler.Finish());
        vk.DestroyPipeline(device, _pipeline, null);
        vk.DestroyDescriptorPool(device, _pool, null);
        vk.DestroyPipelineLayout(device, _pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(device, _setLayout, null);
    }
}

[Collection(SchedulingStateCollection.Name)]
public sealed class ColorToMultisampleDepthBlitTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public ColorToMultisampleDepthBlitTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    private static ImageDescription ColorSource(uint width, uint height)
    {
        var description = ImageDescription.Create();
        description.Data = new SharpEmu.HLE.GpuMemory.GuestSpan(ArrayBackedSpace.Base, (ulong)width * height * 16);
        description.PixelFormat = Format.R32G32B32A32Sfloat;
        description.GuestFormat = GuestPixelFormat.Bits32_32_32_32Float;
        description.Extent = new Extent3D(width, height, 1);
        description.Pitch = width;
        description.BytesPerBlock = 16;
        return description;
    }

    private static ImageDescription DepthTarget(uint width, uint height, uint samples)
    {
        var description = ImageDescription.Create();
        description.Data = new SharpEmu.HLE.GpuMemory.GuestSpan(ArrayBackedSpace.Base + 0x100000, (ulong)width * height * 4 * samples);
        description.PixelFormat = Format.D32Sfloat;
        description.GuestFormat = GuestPixelFormat.Bits32Float;
        description.Extent = new Extent3D(width, height, 1);
        description.Pitch = width;
        description.BytesPerBlock = 4;
        description.Samples = samples;
        return description;
    }

    private static (CachedImage Source, float[] Texel) UploadSource(ImageTestHarness harness, uint width, uint height, uint seed)
    {
        var description = ColorSource(width, height);
        var source = harness.CreateImage(description);
        var random = new System.Random((int)seed);
        var floats = new float[width * height * 4];
        for (var index = 0; index < floats.Length; index++)
        {
            floats[index] = (float)random.NextDouble();
        }

        harness.UploadImage(source, MemoryMarshal.AsBytes(floats.AsSpan()), ImageTestHarness.WholeImageCopies(description, 0));
        return (source, floats[..4]);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    public void Reinterpret_WritesOneSourceChannelPerSample(uint samples)
    {
        if (!GatePrerequisites.Ready(_vulkan, sampleRateShading: true)) return;
        using var harness = new ImageTestHarness(_vulkan);
        using var reader = new MultisampleDepthSampleReader(harness);
        var (source, texel) = UploadSource(harness, 4, 4, samples);
        var destination = harness.CreateImage(DepthTarget(4, 4, samples));
        harness.Run(() => harness.Blit.Reinterpret(source, destination));
        Assert.Equal(ColorToMultisampleDepthBlit.DestinationLayout, destination.Backing.State.Layout);
        var bits = reader.ReadSamples(destination, samples);
        for (var sample = 0; sample < samples; sample++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(texel[sample]), bits[sample]);
        }
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Reinterpret_KeepsPerCallResourcesApartWithinOneTick()
    {
        if (!GatePrerequisites.Ready(_vulkan, sampleRateShading: true)) return;
        using var harness = new ImageTestHarness(_vulkan);
        using var reader = new MultisampleDepthSampleReader(harness);
        var (first, firstTexel) = UploadSource(harness, 8, 8, 1);
        var (second, secondTexel) = UploadSource(harness, 8, 8, 2);
        var firstDestination = harness.CreateImage(DepthTarget(8, 8, 4));
        var secondDestination = harness.CreateImage(DepthTarget(8, 8, 2));
        harness.Run(() =>
        {
            harness.Blit.Reinterpret(first, firstDestination);
            harness.Blit.Reinterpret(second, secondDestination);
        });
        var firstBits = reader.ReadSamples(firstDestination, 4);
        var secondBits = reader.ReadSamples(secondDestination, 2);
        Assert.Equal(firstTexel.Select(BitConverter.SingleToUInt32Bits), firstBits);
        Assert.Equal(secondTexel[..2].Select(BitConverter.SingleToUInt32Bits), secondBits);
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Reinterpret_RejectsMismatchedImages()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(_vulkan);
        var source = harness.CreateImage(ColorSource(4, 4));
        var single = harness.CreateImage(DepthTarget(4, 4, 1));
        var larger = harness.CreateImage(DepthTarget(8, 8, 2));
        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Blit.Reinterpret(source, single)));
        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Blit.Reinterpret(source, larger)));
        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Blit.Reinterpret(single, larger)));
        Assert.Equal(3, fatal.Messages.Count);
        Assert.All(fatal.Messages, message => Assert.Contains("2D single-sample color source", message));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Reinterpret_MatchesTheReferenceModules()
    {
        if (!GatePrerequisites.Ready(_vulkan, sampleRateShading: true, referenceSpirv: true)) return;
        var vertex = ReferenceShaders.TryLoad("gpu_blit_shaders/gpu_blit_fs_triangle.spv");
        var fragment = ReferenceShaders.TryLoad("gpu_blit_shaders/gpu_blit_color_to_ms_depth.spv");
        if (vertex is null || fragment is null)
        {
            return;
        }

        using var harness = new ImageTestHarness(_vulkan);
        using var reader = new MultisampleDepthSampleReader(harness);
        using var referenceBlit = new ColorToMultisampleDepthBlit(harness.Device, harness.Scheduler, vertex, fragment);
        foreach (var samples in new[] { 2u, 4u })
        {
            var (source, _) = UploadSource(harness, 4, 4, samples + 10);
                var referenceDestination = harness.CreateImage(DepthTarget(4, 4, samples));
            var sharpDestination = harness.CreateImage(DepthTarget(4, 4, samples));
            harness.Run(() =>
            {
                referenceBlit.Reinterpret(source, referenceDestination);
                harness.Blit.Reinterpret(source, sharpDestination);
            });
            Assert.Equal(reader.ReadSamples(referenceDestination, samples), reader.ReadSamples(sharpDestination, samples));
        }

        harness.Finish();
        harness.AssertNoValidationMessages();
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Guest memory for image tests: one byte array at a fixed guest base, reachable only through the backing alias.
internal sealed class ArrayBackedSpace : IGuestBackedSpace
{
    public const ulong Base = 0x2_0000_0000;

    public byte[] Bytes { get; } = new byte[16 * 1024 * 1024];

    private bool Contains(ulong address, int length) => address >= Base && address - Base + (ulong)length <= (ulong)Bytes.Length;

    public bool TryHoldRange(ulong address, ulong size) => false;

    public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address)
    {
        address = 0;
        return false;
    }

    public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure)
    {
        failure = HostViewFailure.None;
        return false;
    }

    public bool TryUnmapBacked(ulong address, ulong size) => false;

    public bool TryClearBacking(ulong offset, ulong size) => false;

    public bool IsBackedView(ulong address) => Contains(address, 1);

    public bool IsBackedRange(ulong address, ulong size) => size <= int.MaxValue && Contains(address, (int)size);

    public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data)
    {
        if (!Contains(address, data.Length))
        {
            return false;
        }

        data.CopyTo(Bytes.AsSpan((int)(address - Base)));
        return true;
    }

    public bool TryReadBacking(ulong address, Span<byte> data)
    {
        if (!Contains(address, data.Length))
        {
            return false;
        }

        Bytes.AsSpan((int)(address - Base), data.Length).CopyTo(data);
        return true;
    }
}

// Locates the reference compiled shader blobs; the require switch turns a missing blob into a failure.
internal static class ReferenceShaders
{
    public const string DirectoryVariable = "SHARPEMU_TEST_REFERENCE_SPIRV_DIR";
    public const string RequireVariable = "SHARPEMU_TEST_REQUIRE_REFERENCE_SPIRV";

    public static readonly (string Name, string Sha256)[] Blobs =
    [
        ("gpu_tiler_shaders/gpu_tiler_standard256.spv", "8ee8a2073f23adbdb4cd5c2a00e207f707d3e11daace06fba2841ee86d22c450"),
        ("gpu_tiler_shaders/gpu_tiler_standard4.spv", "8519949e64c4bd73443d0310c84849e2b26689d4d3f9a9af494ba49fa59da41f"),
        ("gpu_tiler_shaders/gpu_tiler_standard4_3d.spv", "b570ca8738bb946024f763f633911f6ab6a0bcb08818a3171ef25a1fc1cfd197"),
        ("gpu_tiler_shaders/gpu_tiler_standard64.spv", "661f32d7e85b6640e8c629cfe37541ec3271384f38687a5a6190c1e437d7bb82"),
        ("gpu_tiler_shaders/gpu_tiler_standard64_3d.spv", "6372c8dee9505189203d547f692b01c4367090a793e3cca7134b16f9a779fba9"),
        ("gpu_tiler_shaders/gpu_tiler_prt.spv", "399a0f95b4ccebb620a3597161315c87078bca07d14449ae059a5db818443c79"),
        ("gpu_tiler_shaders/gpu_tiler_prt_3d.spv", "aa728d680d94c6cf688ed30f2a683483c266ec784dd499205604ba2b102c9f6c"),
        ("gpu_tiler_shaders/gpu_tiler_render_target.spv", "55809eb0c2d9069322d8929ff2699df0a886bfcc60e9127c7550c7b13c9179cf"),
        ("gpu_tiler_shaders/gpu_tiler_depth.spv", "dc26f2109e519a022c6fee88b9f0bc2f847f3b098111e365bd781f8b0963cb1d"),
        ("gpu_tiler_shaders/gpu_tiler_promote_d16.spv", "94121064ffaf15595928336936f2cc361153b235562ea69238aad777307175cb"),
        ("gpu_tiler_shaders/gpu_tiler_demote_d16.spv", "c69870c10d50b920c17aeffdf25a45abcbda99517582aa6f2b247961739ab4a8"),
        ("gpu_tiler_shaders/gpu_tiler_swap_bgra16.spv", "10198e6afe21eafeb22fc79becd5d0efc1243800935eb8437f93c8711c1f71b3"),
        ("gpu_blit_shaders/gpu_blit_color_to_ms_depth.spv", "f05f63f81f5727b279b12afe6e38622a71a8db1c4cb82d65e78499ebb157282a"),
        ("gpu_blit_shaders/gpu_blit_fs_triangle.spv", "d82030b903f72a9a34ffee685725ca3a9adecaebd2ad3785ff6d08e7959cddbf"),
    ];

    public static bool Required => Environment.GetEnvironmentVariable(RequireVariable) == "1";

    public static string? Directory => Environment.GetEnvironmentVariable(DirectoryVariable);

    // Null when the blob is absent and not required; a hash mismatch always fails.
    public static byte[]? TryLoad(string name)
    {
        var expected = Array.Find(Blobs, blob => blob.Name == name).Sha256;
        Assert.False(string.IsNullOrEmpty(expected), $"The reference blob is not in the recorded table: {name}");
        var directory = Directory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            Assert.False(Required, $"Set {DirectoryVariable} to the reference shader directory for the required gate.");
            return null;
        }

        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            Assert.False(Required, $"The required reference blob is missing: {path}");
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.True(expected == actual, $"The reference blob hash changed: {name} expected={expected} actual={actual}");
        return bytes;
    }
}

// A missing device or feature skips a test; the required switch turns that into a failure.
internal static class GatePrerequisites
{
    // Set SHARPEMU_TEST_REQUIRE_DEVICE=1 to fail, instead of skip, when a device or feature is missing.
    public const string RequireDeviceVariable = "SHARPEMU_TEST_REQUIRE_DEVICE";

    public static bool DeviceRequired => Environment.GetEnvironmentVariable(RequireDeviceVariable) == "1";

    public static bool Ready([NotNullWhen(true)] HeadlessVulkan? vulkan, bool sampleRateShading = false, bool samplerAnisotropy = false, bool referenceSpirv = false, bool shaderInt64 = false)
    {
        var missing = vulkan is null ? "a Vulkan device"
            : sampleRateShading && !vulkan.SampleRateShading ? "the sampleRateShading device feature"
            : samplerAnisotropy && !vulkan.SamplerAnisotropy ? "the samplerAnisotropy device feature"
            : referenceSpirv && !vulkan.SupportsSpirv16 ? "a Vulkan 1.3 device for the SPIR-V 1.6 reference modules"
            : shaderInt64 && !vulkan.ShaderInt64 ? "the shaderInt64 device feature"
            : null;
        if (missing is null)
        {
            return true;
        }

        Assert.False(DeviceRequired || ReferenceShaders.Required, $"The required gate cannot run without {missing}.");
        return false;
    }
}

// A worker, a scheduler, the guest array, a stream ring, the tiler and the blit for one test.
internal sealed unsafe class ImageTestHarness : IDisposable
{
    private readonly List<GpuBuffer> _retired = new();
    private readonly List<CachedImage> _images = new();

    public ImageTestHarness(HeadlessVulkan vulkan)
    {
        Vulkan = vulkan;
        Worker = new CacheWorker(vulkan);
        Worker.Run(() => Worker.Scheduler.Begin(new SubmissionContext { QueueName = "image.test", SubmissionId = 1 }));
        Stream = new GpuRingBuffer(Device, Scheduler, GpuBufferUsage.Stream, 4 * 1024 * 1024);
        Tiler = new GpuTiler(Device, Scheduler, Stream);
        Blit = new ColorToMultisampleDepthBlit(Device, Scheduler);
    }

    public HeadlessVulkan Vulkan { get; }

    public CacheWorker Worker { get; }

    public SubmissionScheduler Scheduler => Worker.Scheduler;

    public GpuDeviceInfo Device => Vulkan.DeviceInfo;

    public Vk Vk => Vulkan.Vk;

    public ArrayBackedSpace Guest { get; } = new();

    public GpuRingBuffer Stream { get; }

    public GpuTiler Tiler { get; }

    public ColorToMultisampleDepthBlit Blit { get; }

    public void Run(Action work) => Worker.Run(work);

    // Finishes the recorded work, then fails on any validation-layer message of the test.
    public void AssertNoValidationMessages()
    {
        Finish();
        Vulkan.AssertNoValidationMessages();
    }

    public T Run<T>(Func<T> work) => Worker.Run(work);

    public void Finish() => Run(() => Scheduler.Finish());

    // Host-visible storage-capable buffer holding the data; kept until the harness is disposed.
    public GpuBuffer Upload(ReadOnlySpan<byte> data, ulong minimumSize = 0)
    {
        var size = Math.Max((ulong)data.Length, minimumSize);
        size = (size + 3) & ~3UL;
        var buffer = new GpuBuffer(Device, Scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, Math.Max(size, 4));
        buffer.Mapped.Clear();
        buffer.Write(0, data);
        lock (_retired)
        {
            _retired.Add(buffer);
        }

        return buffer;
    }

    public GpuBuffer CreateDeviceLocalBuffer(ulong size)
    {
        var buffer = new GpuBuffer(Device, Scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, Math.Max((size + 3) & ~3UL, 4));
        lock (_retired)
        {
            _retired.Add(buffer);
        }

        return buffer;
    }

    // Copies a device range into a download buffer, finishes the tick and returns the bytes.
    public byte[] ReadBack(VkBuffer source, ulong offset, ulong size) => Run(() =>
    {
        var aligned = (size + 3) & ~3UL;
        using var download = new GpuBuffer(Device, Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, aligned);
        var command = new CommandBuffer(Scheduler.Current.Handle);
        var before = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit | AccessFlags.HostWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = source,
            Offset = offset,
            Size = aligned,
        };
        Vk.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, PipelineStageFlags.TransferBit, 0, 0, null, 1, &before, 0, null);
        var region = new BufferCopy(offset, 0, aligned);
        Vk.CmdCopyBuffer(command, source, download.Handle, 1, &region);
        var after = before with { Buffer = download.Handle, Offset = 0, SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.HostReadBit };
        Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &after, 0, null);
        Scheduler.Finish();
        download.Invalidate(0, aligned);
        return download.Mapped[..(int)size].ToArray();
    });

    // Images live until the harness has finished all recorded work; a failing assertion cannot destroy one early.
    public CachedImage CreateImage(ImageDescription description)
    {
        var image = Run(() => new CachedImage(Device, Scheduler, Guest, description));
        lock (_images)
        {
            _images.Add(image);
        }

        return image;
    }

    public void UploadImage(CachedImage image, ReadOnlySpan<byte> data, BufferImageCopy[] copies)
    {
        var upload = Upload(data);
        Run(() => image.UploadFromBuffer(copies, upload.Handle, 0, upload.Size));
    }

    public byte[] ReadImage(CachedImage image, BufferImageCopy[] copies, ulong size) => Run(() =>
    {
        var aligned = (size + 3) & ~3UL;
        using var download = new GpuBuffer(Device, Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, aligned);
        image.DownloadToBuffer(copies, download.Handle, 0, aligned);
        Scheduler.Finish();
        download.Invalidate(0, aligned);
        return download.Mapped[..(int)size].ToArray();
    });

    public static BufferImageCopy[] WholeImageCopies(in ImageDescription description, ulong sliceStride)
    {
        var layers = description.TransferLayers;
        var volume = description.IsVolume;
        var copies = new List<BufferImageCopy>();
        ulong offset = 0;
        for (uint level = 0; level < description.Resources.Levels; level++)
        {
            var width = Math.Max(description.Extent.Width >> (int)level, 1);
            var height = Math.Max(description.Extent.Height >> (int)level, 1);
            var depth = volume ? Math.Max(description.Extent.Depth >> (int)level, 1) : layers;
            for (uint z = 0; z < depth; z++)
            {
                copies.Add(new BufferImageCopy
                {
                    BufferOffset = offset,
                    ImageSubresource = new ImageSubresourceLayers(ViewFormatRules.FullAspects(description.PixelFormat) & ~ImageAspectFlags.StencilBit, level, volume ? 0 : z, 1),
                    ImageOffset = new Offset3D(0, 0, volume ? (int)z : 0),
                    ImageExtent = new Extent3D(width, height, 1),
                });
                offset += (ulong)width * height * description.BytesPerBlock;
            }
        }

        return copies.ToArray();
    }

    public static ulong WholeImageBytes(in ImageDescription description)
    {
        var layers = description.TransferLayers;
        ulong total = 0;
        for (uint level = 0; level < description.Resources.Levels; level++)
        {
            var width = Math.Max(description.Extent.Width >> (int)level, 1);
            var height = Math.Max(description.Extent.Height >> (int)level, 1);
            var depth = description.IsVolume ? Math.Max(description.Extent.Depth >> (int)level, 1) : layers;
            total += (ulong)width * height * depth * description.BytesPerBlock;
        }

        return total;
    }

    public static byte[] Pattern(int length, uint seed)
    {
        var bytes = new byte[length];
        var state = seed * 2654435761u + 1;
        for (var index = 0; index < length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[index] = (byte)state;
        }

        return bytes;
    }

    public void Dispose()
    {
        Run(() =>
        {
            Scheduler.Finish();
            Scheduler.WaitForAllPriorityOperations();
            foreach (var image in _images)
            {
                image.Dispose();
            }

            _images.Clear();
            Blit.Dispose();
            Tiler.Dispose();
            Stream.Dispose();
            foreach (var buffer in _retired)
            {
                buffer.Dispose();
            }

            _retired.Clear();
            Scheduler.Shutdown();
        });
        Worker.Dispose();
    }
}

// Runs one compute module with the tiler's binding layout; both the reference and the Sharp modules use it.
internal sealed unsafe class TilerComputeRunner : IDisposable
{
    private readonly ImageTestHarness _harness;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _pipelineLayout;
    private readonly DescriptorPool _pool;
    private readonly List<Pipeline> _pipelines = new();

    public TilerComputeRunner(ImageTestHarness harness)
    {
        _harness = harness;
        var vk = harness.Vk;
        var device = harness.Device.Device;
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        bindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.UniformBuffer, 1, ShaderStageFlags.ComputeBit);
        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = bindings };
        Require(vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");
        var push = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, TileTransferArguments.Size);
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
            Require(vk.CreatePipelineLayout(device, &pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");
        }

        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize(DescriptorType.StorageBuffer, 2 * 1024);
        sizes[1] = new DescriptorPoolSize(DescriptorType.UniformBuffer, 1024);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1024, PoolSizeCount = 2, PPoolSizes = sizes };
        Require(vk.CreateDescriptorPool(device, &poolInfo, null, out _pool), "vkCreateDescriptorPool");
    }

    private static void Require(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed with {result}");
        }
    }

    // Specialization values are 4-byte constants in constant identifier order; empty means none.
    public Pipeline CreatePipeline(byte[] spirv, ReadOnlySpan<uint> specialization)
    {
        var vk = _harness.Vk;
        var device = _harness.Device.Device;
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Require(vk.CreateShaderModule(device, &moduleInfo, null, out module), "vkCreateShaderModule");
        }

        var entries = stackalloc SpecializationMapEntry[Math.Max(specialization.Length, 1)];
        for (var index = 0; index < specialization.Length; index++)
        {
            entries[index] = new SpecializationMapEntry((uint)index, (uint)index * 4, 4);
        }

        var values = stackalloc uint[Math.Max(specialization.Length, 1)];
        specialization.CopyTo(new Span<uint>(values, Math.Max(specialization.Length, 1)));
        var specializationInfo = new SpecializationInfo
        {
            MapEntryCount = (uint)specialization.Length,
            PMapEntries = entries,
            DataSize = (nuint)(specialization.Length * 4),
            PData = values,
        };
        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        Pipeline pipeline;
        Result created;
        try
        {
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                    PSpecializationInfo = specialization.IsEmpty ? null : &specializationInfo,
                },
                Layout = _pipelineLayout,
            };
            created = vk.CreateComputePipelines(device, default, 1, &info, null, out pipeline);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
            vk.DestroyShaderModule(device, module, null);
        }

        Require(created, "vkCreateComputePipelines");
        _pipelines.Add(pipeline);
        return pipeline;
    }

    // Records one dispatch on the worker; arguments go to binding 2 and to the push range.
    public void Dispatch(Pipeline pipeline, GpuBuffer input, GpuBuffer output, GpuBuffer arguments, ulong argumentsOffset, in TileTransferArguments push, uint groupsX, uint groupsY, uint groupsZ)
    {
        var vk = _harness.Vk;
        var device = _harness.Device.Device;
        var setLayout = _setLayout;
        var allocateInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
        Require(vk.AllocateDescriptorSets(device, &allocateInfo, out var set), "vkAllocateDescriptorSets");
        var infos = stackalloc DescriptorBufferInfo[3];
        infos[0] = new DescriptorBufferInfo(input.Handle, 0, input.Size);
        infos[1] = new DescriptorBufferInfo(output.Handle, 0, output.Size);
        infos[2] = new DescriptorBufferInfo(arguments.Handle, argumentsOffset, TileTransferArguments.Size);
        var writes = stackalloc WriteDescriptorSet[3];
        for (uint index = 0; index < 3; index++)
        {
            writes[index] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = index,
                DescriptorCount = 1,
                DescriptorType = index == 2 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                PBufferInfo = infos + index,
            };
        }

        vk.UpdateDescriptorSets(device, 3, writes, 0, null);
        var command = new CommandBuffer(_harness.Scheduler.Current.Handle);
        var barriers = stackalloc BufferMemoryBarrier[3];
        for (var index = 0; index < 3; index++)
        {
            barriers[index] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.HostWriteBit | AccessFlags.MemoryWriteBit,
                DstAccessMask = index == 1 ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : index == 2 ? AccessFlags.UniformReadBit : AccessFlags.ShaderReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = infos[index].Buffer,
                Offset = infos[index].Offset,
                Size = infos[index].Range,
            };
        }

        vk.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 3, barriers, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, pipeline);
        vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        fixed (TileTransferArguments* pointer = &push)
        {
            vk.CmdPushConstants(command, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, TileTransferArguments.Size, pointer);
        }

        vk.CmdDispatch(command, groupsX, groupsY, groupsZ);
        var after = barriers[1] with { SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.TransferReadBit | AccessFlags.MemoryReadBit };
        vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 1, &after, 0, null);
    }

    public void Dispose()
    {
        var vk = _harness.Vk;
        var device = _harness.Device.Device;
        _harness.Run(() => _harness.Scheduler.Finish());
        foreach (var pipeline in _pipelines)
        {
            vk.DestroyPipeline(device, pipeline, null);
        }

        vk.DestroyDescriptorPool(device, _pool, null);
        vk.DestroyPipelineLayout(device, _pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(device, _setLayout, null);
    }
}

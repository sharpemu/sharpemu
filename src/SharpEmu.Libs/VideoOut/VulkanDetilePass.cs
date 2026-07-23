// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Self-contained GPU deswizzle pass: runs the ExactXor detile equation from
/// <see cref="GnmTiling.GetDetileParams"/> as a Vulkan compute shader
/// (<see cref="SpirvFixedShaders.CreateDetileCompute"/>), writing a linear buffer
/// and copying it into a sampled image — the GPU equivalent of the CPU
/// <c>GnmTiling.TryDetile</c> + staging upload.
///
/// Two entry points share the same (verified) recording:
/// <see cref="DetileIntoImage"/> is a self-contained one-shot (submit + wait) used
/// by the isolation self-test; <see cref="RecordDetile"/> records into a caller's
/// command buffer and hands back its transient buffers + descriptor pool for the
/// caller to retire with that command buffer's fence — the render-path variant,
/// which must never block the render thread.
///
/// Only ExactXor 4-bytes/element surfaces are handled; <see cref="Supports"/> lets
/// the caller fall back to the CPU path for everything else.
/// </summary>
internal sealed unsafe class VulkanDetilePass : IDisposable
{
    private const uint LocalSize = 8;
    private const uint PushConstantBytes = 8 * sizeof(uint);

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly PhysicalDevice _physicalDevice;
    private readonly uint _queueFamilyIndex;

    private ShaderModule _shaderModule;
    private DescriptorSetLayout _descriptorSetLayout;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;
    private CommandPool _commandPool;
    private bool _initialized;
    private bool _disposed;

    public VulkanDetilePass(
        Vk vk,
        Device device,
        Queue queue,
        PhysicalDevice physicalDevice,
        uint queueFamilyIndex)
    {
        _vk = vk;
        _device = device;
        _queue = queue;
        _physicalDevice = physicalDevice;
        _queueFamilyIndex = queueFamilyIndex;
    }

    /// <summary>The kernel handles only the exact-XOR modes at 4 bytes/element.</summary>
    public static bool Supports(in DetileParams parameters) =>
        parameters.Equation == DetileEquation.ExactXor && parameters.BytesPerElement == 4;

    /// <summary>Transient per-detile resources the caller must retire once the
    /// command buffer they were recorded into has completed.</summary>
    public readonly record struct Transients(
        (VkBuffer Buffer, DeviceMemory Memory)[] Buffers,
        DescriptorPool DescriptorPool);

    private struct DetileResources
    {
        public VkBuffer Tiled;
        public DeviceMemory TiledMemory;
        public VkBuffer XTerm;
        public DeviceMemory XMemory;
        public VkBuffer YTerm;
        public DeviceMemory YMemory;
        public VkBuffer Output;
        public DeviceMemory OutputMemory;
        public DescriptorPool Pool;
        public DescriptorSet Set;
        public ulong OutputBytes;
    }

    /// <summary>
    /// Records the deswizzle of <paramref name="tiled"/> into <paramref name="image"/>
    /// (RGBA8, <paramref name="width"/> x <paramref name="height"/>, currently in
    /// <paramref name="currentLayout"/>) onto <paramref name="commandBuffer"/>,
    /// leaving the image <see cref="ImageLayout.ShaderReadOnlyOptimal"/>. Does not
    /// submit; the caller retires <paramref name="transients"/> with the command
    /// buffer's fence. Returns false (with empty transients) when unsupported.
    /// </summary>
    public bool RecordDetile(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout currentLayout,
        uint width,
        uint height,
        ReadOnlySpan<byte> tiled,
        in DetileParams parameters,
        out Transients transients)
    {
        transients = new Transients([], default);
        if (_disposed || !Supports(parameters) || width == 0 || height == 0 || tiled.IsEmpty)
        {
            return false;
        }

        EnsurePipeline();

        var resources = default(DetileResources);
        try
        {
            PrepareResources(tiled, parameters, ref resources);
            RecordCommands(commandBuffer, in resources, image, currentLayout, width, height, in parameters);
        }
        catch
        {
            DestroyResources(in resources);
            throw;
        }

        transients = new Transients(
            [
                (resources.Tiled, resources.TiledMemory),
                (resources.XTerm, resources.XMemory),
                (resources.YTerm, resources.YMemory),
                (resources.Output, resources.OutputMemory),
            ],
            resources.Pool);
        return true;
    }

    /// <summary>
    /// One-shot variant used by the isolation self-test: records the detile onto a
    /// private command buffer, submits, waits, and frees every transient. Never
    /// call this on the render thread — its blocking wait would deadlock the
    /// present pipeline; use <see cref="RecordDetile"/> there.
    /// </summary>
    public bool DetileIntoImage(
        Image image,
        ImageLayout currentLayout,
        uint width,
        uint height,
        ReadOnlySpan<byte> tiled,
        in DetileParams parameters)
    {
        if (_disposed || !Supports(parameters) || width == 0 || height == 0 || tiled.IsEmpty)
        {
            return false;
        }

        EnsurePipeline();

        var resources = default(DetileResources);
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        try
        {
            PrepareResources(tiled, parameters, ref resources);

            commandBuffer = AllocateCommandBuffer();
            BeginCommandBuffer(commandBuffer);
            RecordCommands(commandBuffer, in resources, image, currentLayout, width, height, in parameters);
            Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer(detile)");

            fence = CreateFence();
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            Check(_vk.QueueSubmit(_queue, 1, &submitInfo, fence), "vkQueueSubmit(detile)");
            Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences(detile)");
            return true;
        }
        finally
        {
            if (fence.Handle != 0)
            {
                _vk.DestroyFence(_device, fence, null);
            }

            if (commandBuffer.Handle != 0)
            {
                _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
            }

            DestroyResources(in resources);
        }
    }

    private void PrepareResources(ReadOnlySpan<byte> tiled, in DetileParams parameters, ref DetileResources resources)
    {
        // GetDetileParams' term tables are byte offsets; the kernel indexes a
        // uint[], so it wants element offsets. For a power-of-two element size the
        // low log2(bpp) bits of every term are 0, so the shift is exact.
        var shift = BitOperations.TrailingZeroCount((uint)parameters.BytesPerElement);
        var xTerm = ToElementTerms(parameters.XByteTerm, shift);
        var yTerm = ToElementTerms(parameters.YByteTerm, shift);

        resources.OutputBytes = (ulong)parameters.ElementsWide * (ulong)parameters.ElementsHigh * sizeof(uint);

        resources.Tiled = CreateHostBuffer((ulong)tiled.Length, BufferUsageFlags.StorageBufferBit, out resources.TiledMemory);
        UploadBytes(resources.TiledMemory, tiled);
        resources.XTerm = CreateHostBuffer((ulong)xTerm.Length * sizeof(uint), BufferUsageFlags.StorageBufferBit, out resources.XMemory);
        UploadUInts(resources.XMemory, xTerm);
        resources.YTerm = CreateHostBuffer((ulong)yTerm.Length * sizeof(uint), BufferUsageFlags.StorageBufferBit, out resources.YMemory);
        UploadUInts(resources.YMemory, yTerm);
        resources.Output = CreateHostBuffer(
            resources.OutputBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            out resources.OutputMemory);

        resources.Pool = CreateDescriptorPool();
        resources.Set = AllocateDescriptorSet(resources.Pool);
        WriteDescriptors(
            resources.Set,
            (resources.Tiled, (ulong)tiled.Length),
            (resources.XTerm, (ulong)xTerm.Length * sizeof(uint)),
            (resources.YTerm, (ulong)yTerm.Length * sizeof(uint)),
            (resources.Output, resources.OutputBytes));
    }

    private void RecordCommands(
        CommandBuffer commandBuffer,
        in DetileResources resources,
        Image image,
        ImageLayout currentLayout,
        uint width,
        uint height,
        in DetileParams parameters)
    {
        var descriptorSet = resources.Set;
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _pipeline);
        _vk.CmdBindDescriptorSets(
            commandBuffer, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &descriptorSet, 0, null);

        Span<uint> push =
        [
            width,
            height,
            (uint)parameters.BlockWidth,
            (uint)parameters.BlockHeight,
            (uint)parameters.BlockElements,
            (uint)parameters.BlocksPerRow,
            (uint)parameters.XMask,
            (uint)parameters.YMask,
        ];
        fixed (uint* pushPointer = push)
        {
            _vk.CmdPushConstants(
                commandBuffer, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, PushConstantBytes, pushPointer);
        }

        _vk.CmdDispatch(
            commandBuffer,
            (width + LocalSize - 1) / LocalSize,
            (height + LocalSize - 1) / LocalSize,
            1);

        // Compute store -> transfer read on the linear output buffer.
        var outputBarrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = resources.Output,
            Offset = 0,
            Size = resources.OutputBytes,
        };
        _vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            1,
            &outputBarrier,
            0,
            null);

        var initialized = currentLayout == ImageLayout.ShaderReadOnlyOptimal;
        TransitionImage(
            commandBuffer,
            image,
            currentLayout,
            ImageLayout.TransferDstOptimal,
            initialized ? AccessFlags.ShaderReadBit : 0,
            AccessFlags.TransferWriteBit,
            initialized ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit);

        var copyRegion = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = default,
            ImageExtent = new Extent3D(width, height, 1),
        };
        _vk.CmdCopyBufferToImage(
            commandBuffer, resources.Output, image, ImageLayout.TransferDstOptimal, 1, &copyRegion);

        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit);
    }

    private void DestroyResources(in DetileResources resources)
    {
        if (resources.Pool.Handle != 0)
        {
            _vk.DestroyDescriptorPool(_device, resources.Pool, null);
        }

        DestroyBuffer(resources.Output, resources.OutputMemory);
        DestroyBuffer(resources.YTerm, resources.YMemory);
        DestroyBuffer(resources.XTerm, resources.XMemory);
        DestroyBuffer(resources.Tiled, resources.TiledMemory);
    }

    private void EnsurePipeline()
    {
        if (_initialized)
        {
            return;
        }

        var spirv = SpirvFixedShaders.CreateDetileCompute();
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            Check(
                _vk.CreateShaderModule(_device, &moduleInfo, null, out _shaderModule),
                "vkCreateShaderModule(detile)");
        }

        var bindings = stackalloc DescriptorSetLayoutBinding[4];
        for (uint index = 0; index < 4; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = bindings,
        };
        Check(
            _vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _descriptorSetLayout),
            "vkCreateDescriptorSetLayout(detile)");

        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = PushConstantBytes,
        };
        var setLayout = _descriptorSetLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        Check(
            _vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout),
            "vkCreatePipelineLayout(detile)");

        ReadOnlySpan<byte> entryPoint = "main\0"u8;
        fixed (byte* entry = entryPoint)
        {
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Layout = _pipelineLayout,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = _shaderModule,
                    PName = entry,
                },
            };
            Check(
                _vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, out _pipeline),
                "vkCreateComputePipelines(detile)");
        }

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(
            _vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool),
            "vkCreateCommandPool(detile)");

        _initialized = true;
    }

    private static uint[] ToElementTerms(int[] byteTerms, int shift)
    {
        var terms = new uint[byteTerms.Length];
        for (var index = 0; index < byteTerms.Length; index++)
        {
            terms[index] = (uint)byteTerms[index] >> shift;
        }

        return terms;
    }

    private VkBuffer CreateHostBuffer(ulong size, BufferUsageFlags usage, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "vkCreateBuffer(detile)");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(
                requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        Check(_vk.AllocateMemory(_device, &allocateInfo, null, out memory), "vkAllocateMemory(detile)");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory(detile)");
        return buffer;
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredFlags)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
        var memoryTypes = &properties.MemoryTypes.Element0;
        for (uint index = 0; index < properties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << (int)index)) != 0 &&
                (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
            {
                return index;
            }
        }

        throw new InvalidOperationException("No compatible Vulkan host-visible memory type for detile.");
    }

    private void UploadBytes(DeviceMemory memory, ReadOnlySpan<byte> data)
    {
        void* mapped;
        Check(_vk.MapMemory(_device, memory, 0, (ulong)data.Length, 0, &mapped), "vkMapMemory(detile)");
        data.CopyTo(new Span<byte>(mapped, data.Length));
        _vk.UnmapMemory(_device, memory);
    }

    private void UploadUInts(DeviceMemory memory, uint[] data)
    {
        void* mapped;
        var byteCount = (ulong)data.Length * sizeof(uint);
        Check(_vk.MapMemory(_device, memory, 0, byteCount, 0, &mapped), "vkMapMemory(detile terms)");
        data.AsSpan().CopyTo(new Span<uint>(mapped, data.Length));
        _vk.UnmapMemory(_device, memory);
    }

    private DescriptorPool CreateDescriptorPool()
    {
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = 4,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Check(
            _vk.CreateDescriptorPool(_device, &poolInfo, null, out var pool),
            "vkCreateDescriptorPool(detile)");
        return pool;
    }

    private DescriptorSet AllocateDescriptorSet(DescriptorPool pool)
    {
        var setLayout = _descriptorSetLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        Check(
            _vk.AllocateDescriptorSets(_device, &allocateInfo, out var descriptorSet),
            "vkAllocateDescriptorSets(detile)");
        return descriptorSet;
    }

    private void WriteDescriptors(
        DescriptorSet descriptorSet,
        (VkBuffer Buffer, ulong Size) binding0,
        (VkBuffer Buffer, ulong Size) binding1,
        (VkBuffer Buffer, ulong Size) binding2,
        (VkBuffer Buffer, ulong Size) binding3)
    {
        var buffers = stackalloc DescriptorBufferInfo[4]
        {
            new DescriptorBufferInfo { Buffer = binding0.Buffer, Offset = 0, Range = binding0.Size },
            new DescriptorBufferInfo { Buffer = binding1.Buffer, Offset = 0, Range = binding1.Size },
            new DescriptorBufferInfo { Buffer = binding2.Buffer, Offset = 0, Range = binding2.Size },
            new DescriptorBufferInfo { Buffer = binding3.Buffer, Offset = 0, Range = binding3.Size },
        };

        var writes = stackalloc WriteDescriptorSet[4];
        for (uint index = 0; index < 4; index++)
        {
            writes[index] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSet,
                DstBinding = index,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &buffers[index],
            };
        }

        _vk.UpdateDescriptorSets(_device, 4, writes, 0, null);
    }

    private CommandBuffer AllocateCommandBuffer()
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(
            _vk.AllocateCommandBuffers(_device, &allocateInfo, out var commandBuffer),
            "vkAllocateCommandBuffers(detile)");
        return commandBuffer;
    }

    private void BeginCommandBuffer(CommandBuffer commandBuffer)
    {
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Check(_vk.BeginCommandBuffer(commandBuffer, &beginInfo), "vkBeginCommandBuffer(detile)");
    }

    private void TransitionImage(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _vk.CmdPipelineBarrier(commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private Fence CreateFence()
    {
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(_vk.CreateFence(_device, &fenceInfo, null, out var fence), "vkCreateFence(detile)");
        return fence;
    }

    private void DestroyBuffer(VkBuffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, buffer, null);
        }

        if (memory.Handle != 0)
        {
            _vk.FreeMemory(_device, memory, null);
        }
    }

    private void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pipeline.Handle != 0)
        {
            _vk.DestroyPipeline(_device, _pipeline, null);
        }

        if (_pipelineLayout.Handle != 0)
        {
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        }

        if (_descriptorSetLayout.Handle != 0)
        {
            _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
        }

        if (_shaderModule.Handle != 0)
        {
            _vk.DestroyShaderModule(_device, _shaderModule, null);
        }

        if (_commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
        }
    }
}

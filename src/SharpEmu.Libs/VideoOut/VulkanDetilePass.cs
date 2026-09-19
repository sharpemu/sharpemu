// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Agc;
using SharpEmu.Logging;
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
    private const uint PushConstantBytes = 11 * sizeof(uint);

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

    private PhysicalDeviceMemoryProperties _memoryProperties;
    private bool _memoryPropertiesLoaded;

    private readonly Dictionary<int[], TermBuffer> _xorTermBuffers = new(ReferenceComparer.Instance);
    private readonly Dictionary<int[], TermBuffer> _blockTermBuffers = new(ReferenceComparer.Instance);
    private TermBuffer _placeholderTermBuffer;

    private readonly Dictionary<(ulong Bucket, bool HostVisible), Stack<Allocation>> _bufferPool = new();
    private readonly List<Allocation> _allAllocations = new();
    private ulong _pooledBytes;

    private readonly Stack<DescriptorSet> _freeDescriptorSets = new();
    private readonly List<DescriptorPool> _descriptorPools = new();
    private const uint DescriptorSetsPerPool = 64;
    private const ulong MinimumBufferBucket = 4096;
    // Busy Demon's Souls loading screens thrash many texture sizes. Without a
    // cap the free lists retain every bucket forever and native device memory
    // climbs with each new size class (#618 / #639).
    private const int MaxBuffersPerBucket = 4;
    private static readonly ulong MaxPooledBufferBytes =
        HostMemoryBudget.IsConstrained
            ? 128UL * 1024 * 1024
            : 256UL * 1024 * 1024;

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

    /// <summary>
    /// The kernel handles the exact-XOR and block-table modes at 4/8/16
    /// bytes-per-element (one, two, or four 32-bit words per element). 1/2 bpp are
    /// sub-word and stay on the CPU.
    /// </summary>
    public static bool Supports(in DetileParams parameters) =>
        (parameters.Equation == DetileEquation.ExactXor ||
         parameters.Equation == DetileEquation.BlockTable) &&
        parameters.BytesPerElement is 4 or 8 or 16;

    /// <summary>Opaque handle to the pooled resources one recorded detile is using.
    /// The caller hands it back to <see cref="Retire"/> once the command buffer they
    /// were recorded into has completed; nothing is destroyed, the buffers and the
    /// descriptor set return to this pass's free lists for the next texture.</summary>
    public sealed class Transients
    {
        internal static readonly Transients Empty = new();

        internal Allocation Tiled;
        internal Allocation Output;
        internal DescriptorSet Set;
        internal bool Rented;
    }

    internal readonly record struct Allocation(
        VkBuffer Buffer,
        DeviceMemory Memory,
        ulong Capacity,
        bool HostVisible);

    private readonly record struct TermBuffer(VkBuffer Buffer, ulong ByteSize);

    private struct DetileResources
    {
        public Allocation Tiled;
        public Allocation Output;
        public DescriptorSet Set;
        public ulong OutputBytes;
        public uint SrcSliceElements;
        public uint EquationValue;
        public uint UintsPerElement;
    }

    private sealed class ReferenceComparer : IEqualityComparer<int[]>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => ReferenceEquals(x, y);

        public int GetHashCode(int[] obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Records the deswizzle of <paramref name="tiled"/> into <paramref name="image"/>
    /// (<paramref name="texelWidth"/> x <paramref name="texelHeight"/> texels x
    /// <paramref name="layers"/> array slices, currently in
    /// <paramref name="currentLayout"/>) onto <paramref name="commandBuffer"/>,
    /// leaving the image <see cref="ImageLayout.ShaderReadOnlyOptimal"/>. The kernel
    /// iterates the element grid from <paramref name="parameters"/> (for
    /// block-compressed formats a 4x4 block is one element, so the element grid is
    /// smaller than the texel grid). The tiled buffer holds the array slices packed
    /// contiguously (each an independently tiled 2D surface). Does not submit; the
    /// caller retires <paramref name="transients"/> with the command buffer's fence.
    /// Returns false (with empty transients) when unsupported.
    /// </summary>
    public bool RecordDetile(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout currentLayout,
        uint texelWidth,
        uint texelHeight,
        uint layers,
        ReadOnlySpan<byte> tiled,
        in DetileParams parameters,
        out Transients transients)
    {
        transients = Transients.Empty;
        if (_disposed || !Supports(parameters) || texelWidth == 0 || texelHeight == 0 || layers == 0 ||
            tiled.IsEmpty || tiled.Length % (int)(layers * (uint)parameters.BytesPerElement) != 0)
        {
            return false;
        }

        EnsurePipeline();

        var resources = default(DetileResources);
        try
        {
            PrepareResources(tiled, parameters, layers, ref resources);
            RecordCommands(commandBuffer, in resources, image, currentLayout, texelWidth, texelHeight, layers, in parameters);
        }
        catch
        {
            ReleaseResources(in resources);
            throw;
        }

        transients = new Transients
        {
            Tiled = resources.Tiled,
            Output = resources.Output,
            Set = resources.Set,
            Rented = true,
        };
        return true;
    }

    public void Retire(Transients transients)
    {
        if (_disposed || transients is null || !transients.Rented)
        {
            return;
        }

        transients.Rented = false;
        ReturnBuffer(transients.Tiled);
        ReturnBuffer(transients.Output);
        _freeDescriptorSets.Push(transients.Set);
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
        uint texelWidth,
        uint texelHeight,
        uint layers,
        ReadOnlySpan<byte> tiled,
        in DetileParams parameters)
    {
        if (_disposed || !Supports(parameters) || texelWidth == 0 || texelHeight == 0 || layers == 0 ||
            tiled.IsEmpty || tiled.Length % (int)(layers * (uint)parameters.BytesPerElement) != 0)
        {
            return false;
        }

        EnsurePipeline();

        var resources = default(DetileResources);
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        try
        {
            PrepareResources(tiled, parameters, layers, ref resources);

            commandBuffer = AllocateCommandBuffer();
            BeginCommandBuffer(commandBuffer);
            RecordCommands(commandBuffer, in resources, image, currentLayout, texelWidth, texelHeight, layers, in parameters);
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

            ReleaseResources(in resources);
        }
    }

    private void PrepareResources(ReadOnlySpan<byte> tiled, in DetileParams parameters, uint layers, ref DetileResources resources)
    {
        // Binding 1 carries the within-block offset table, binding 2 the Y terms.
        // ExactXor: xTerm/yTerm are byte offsets; the kernel indexes a uint[], so it
        // wants element offsets — for a power-of-two element size the low
        // log2(bpp) bits of every term are 0, so the right shift is exact.
        // BlockTable: GetDetileParams' block table is already element offsets; it
        // goes in binding 1 and binding 2 is an unused placeholder.
        TermBuffer xTerm;
        TermBuffer yTerm;
        if (parameters.Equation == DetileEquation.BlockTable)
        {
            xTerm = GetTermBuffer(_blockTermBuffers, parameters.BlockTable, shift: 0);
            yTerm = GetPlaceholderTermBuffer();
            resources.EquationValue = 1;
        }
        else
        {
            var shift = BitOperations.TrailingZeroCount((uint)parameters.BytesPerElement);
            xTerm = GetTermBuffer(_xorTermBuffers, parameters.XByteTerm, shift);
            yTerm = GetTermBuffer(_xorTermBuffers, parameters.YByteTerm, shift);
            resources.EquationValue = 0;
        }

        // The array slices are packed contiguously in the tiled buffer, so each
        // slice's element stride is the whole tiled buffer split evenly by layer.
        // Element sizes are in bytes-per-element; the kernel moves bpp/4 words each.
        var bytesPerElement = (uint)parameters.BytesPerElement;
        resources.UintsPerElement = bytesPerElement / sizeof(uint);
        resources.SrcSliceElements = (uint)((ulong)tiled.Length / bytesPerElement / layers);
        resources.OutputBytes =
            (ulong)parameters.ElementsWide * (ulong)parameters.ElementsHigh * bytesPerElement * layers;

        resources.Tiled = RentBuffer((ulong)tiled.Length, hostVisible: true);
        UploadBytes(resources.Tiled.Memory, tiled);
        resources.Output = RentBuffer(resources.OutputBytes, hostVisible: false);

        resources.Set = RentDescriptorSet();
        WriteDescriptors(
            resources.Set,
            (resources.Tiled.Buffer, (ulong)tiled.Length),
            (xTerm.Buffer, xTerm.ByteSize),
            (yTerm.Buffer, yTerm.ByteSize),
            (resources.Output.Buffer, resources.OutputBytes));
    }

    private TermBuffer GetTermBuffer(Dictionary<int[], TermBuffer> cache, int[] table, int shift)
    {
        if (cache.TryGetValue(table, out var cached))
        {
            return cached;
        }

        var terms = ToElementTerms(table, shift);
        var byteSize = (ulong)terms.Length * sizeof(uint);
        var allocation = CreateBuffer(byteSize, hostVisible: true);
        UploadUInts(allocation.Memory, terms);
        var termBuffer = new TermBuffer(allocation.Buffer, byteSize);
        cache[table] = termBuffer;
        return termBuffer;
    }

    private TermBuffer GetPlaceholderTermBuffer()
    {
        if (_placeholderTermBuffer.Buffer.Handle != 0)
        {
            return _placeholderTermBuffer;
        }

        var allocation = CreateBuffer(sizeof(uint), hostVisible: true);
        UploadUInts(allocation.Memory, [0u]);
        _placeholderTermBuffer = new TermBuffer(allocation.Buffer, sizeof(uint));
        return _placeholderTermBuffer;
    }

    private static ulong BucketFor(ulong size)
    {
        var bucket = MinimumBufferBucket;
        while (bucket < size)
        {
            bucket <<= 1;
        }

        return bucket;
    }

    private Allocation RentBuffer(ulong size, bool hostVisible)
    {
        var bucket = BucketFor(size);
        if (_bufferPool.TryGetValue((bucket, hostVisible), out var free) && free.Count > 0)
        {
            var rented = free.Pop();
            _pooledBytes = rented.Capacity >= _pooledBytes
                ? 0
                : _pooledBytes - rented.Capacity;
            return rented;
        }

        return CreateBuffer(bucket, hostVisible);
    }

    private void ReturnBuffer(Allocation allocation)
    {
        if (allocation.Buffer.Handle == 0)
        {
            return;
        }

        var key = (allocation.Capacity, allocation.HostVisible);
        if (!_bufferPool.TryGetValue(key, out var free))
        {
            free = new Stack<Allocation>();
            _bufferPool[key] = free;
        }

        if (free.Count >= MaxBuffersPerBucket ||
            _pooledBytes + allocation.Capacity > MaxPooledBufferBytes)
        {
            DestroyBuffer(allocation.Buffer, allocation.Memory);
            _allAllocations.Remove(allocation);
            return;
        }

        free.Push(allocation);
        _pooledBytes += allocation.Capacity;
    }

    private DescriptorSet RentDescriptorSet()
    {
        if (_freeDescriptorSets.Count > 0)
        {
            return _freeDescriptorSets.Pop();
        }

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = 4 * DescriptorSetsPerPool,
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = DescriptorSetsPerPool,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        Check(
            _vk.CreateDescriptorPool(_device, &poolInfo, null, out var pool),
            "vkCreateDescriptorPool(detile)");
        _descriptorPools.Add(pool);

        var layouts = stackalloc DescriptorSetLayout[(int)DescriptorSetsPerPool];
        for (var index = 0; index < DescriptorSetsPerPool; index++)
        {
            layouts[index] = _descriptorSetLayout;
        }

        var sets = stackalloc DescriptorSet[(int)DescriptorSetsPerPool];
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = DescriptorSetsPerPool,
            PSetLayouts = layouts,
        };
        Check(
            _vk.AllocateDescriptorSets(_device, &allocateInfo, sets),
            "vkAllocateDescriptorSets(detile)");
        for (var index = 0; index < DescriptorSetsPerPool; index++)
        {
            _freeDescriptorSets.Push(sets[index]);
        }

        return _freeDescriptorSets.Pop();
    }

    private void ReleaseResources(in DetileResources resources)
    {
        ReturnBuffer(resources.Tiled);
        ReturnBuffer(resources.Output);
        if (resources.Set.Handle != 0)
        {
            _freeDescriptorSets.Push(resources.Set);
        }
    }

    private void RecordCommands(
        CommandBuffer commandBuffer,
        in DetileResources resources,
        Image image,
        ImageLayout currentLayout,
        uint texelWidth,
        uint texelHeight,
        uint layers,
        in DetileParams parameters)
    {
        // The kernel iterates the element grid (smaller than the texel grid for
        // block-compressed formats); the image copy below uses the texel grid.
        var elementsWide = (uint)parameters.ElementsWide;
        var elementsHigh = (uint)parameters.ElementsHigh;

        var descriptorSet = resources.Set;
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _pipeline);
        _vk.CmdBindDescriptorSets(
            commandBuffer, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &descriptorSet, 0, null);

        Span<uint> push =
        [
            elementsWide,
            elementsHigh,
            (uint)parameters.BlockWidth,
            (uint)parameters.BlockHeight,
            (uint)parameters.BlockElements,
            (uint)parameters.BlocksPerRow,
            (uint)parameters.XMask,
            (uint)parameters.YMask,
            resources.SrcSliceElements,
            resources.EquationValue,
            resources.UintsPerElement,
        ];
        fixed (uint* pushPointer = push)
        {
            _vk.CmdPushConstants(
                commandBuffer, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, PushConstantBytes, pushPointer);
        }

        // X is widened by uintsPerElement (each thread copies one word); one
        // dispatch-Z layer per array slice.
        _vk.CmdDispatch(
            commandBuffer,
            (elementsWide * resources.UintsPerElement + LocalSize - 1) / LocalSize,
            (elementsHigh + LocalSize - 1) / LocalSize,
            layers);

        // Compute store -> transfer read on the linear output buffer.
        var outputBarrier = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = resources.Output.Buffer,
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
            PipelineStageFlags.TransferBit,
            layers);

        // The output buffer is layer-major, tightly packed (BufferRowLength 0 =>
        // one element-row per texel-row, which for compressed formats is the block
        // row), so a single copy fills every array layer. Extent is in texels.
        var copyRegion = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, layers),
            ImageOffset = default,
            ImageExtent = new Extent3D(texelWidth, texelHeight, 1),
        };
        _vk.CmdCopyBufferToImage(
            commandBuffer, resources.Output.Buffer, image, ImageLayout.TransferDstOptimal, 1, &copyRegion);

        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit,
            layers);
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

    private Allocation CreateBuffer(ulong size, bool hostVisible)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "vkCreateBuffer(detile)");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var required = hostVisible
            ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            : MemoryPropertyFlags.DeviceLocalBit;
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, required, hostVisible),
        };
        Check(_vk.AllocateMemory(_device, &allocateInfo, null, out var memory), "vkAllocateMemory(detile)");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory(detile)");
        var allocation = new Allocation(buffer, memory, size, hostVisible);
        _allAllocations.Add(allocation);
        return allocation;
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredFlags, bool hostVisible)
    {
        if (!_memoryPropertiesLoaded)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out _memoryProperties);
            _memoryPropertiesLoaded = true;
        }

        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            var memoryTypes = &properties->MemoryTypes.Element0;
            for (uint index = 0; index < properties->MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) != 0 &&
                    (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
                {
                    return index;
                }
            }

            if (!hostVisible)
            {
                for (uint index = 0; index < properties->MemoryTypeCount; index++)
                {
                    if ((typeBits & (1u << (int)index)) != 0)
                    {
                        return index;
                    }
                }
            }
        }

        throw new InvalidOperationException("No compatible Vulkan memory type for detile.");
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
        PipelineStageFlags dstStage,
        uint layers)
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
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers),
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

        foreach (var allocation in _allAllocations)
        {
            DestroyBuffer(allocation.Buffer, allocation.Memory);
        }

        _allAllocations.Clear();
        _bufferPool.Clear();
        _pooledBytes = 0;
        _xorTermBuffers.Clear();
        _blockTermBuffers.Clear();
        _placeholderTermBuffer = default;
        _freeDescriptorSets.Clear();
        foreach (var pool in _descriptorPools)
        {
            _vk.DestroyDescriptorPool(_device, pool, null);
        }

        _descriptorPools.Clear();

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

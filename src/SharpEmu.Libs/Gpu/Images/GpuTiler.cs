// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Images;

public readonly record struct TilerBufferSpan(VkBuffer Buffer, ulong Offset, ulong Size);

public enum DepthConversionDirection
{
    Widen,
    Narrow,
}

public enum ColorChannelSwap
{
    None,
    SwapBgra16,
}

public struct DepthConversionLayout
{
    public uint Width;
    public uint Height;
    public uint Layers;
    public ulong SourceRowStride;
    public ulong TargetRowStride;
    public ulong SourceSliceStride;
    public ulong TargetSliceStride;
}

// The 13 dwords every tiler shader reads, as a uniform block or as push constants.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct TileTransferArguments
{
    public uint SourceBase;
    public uint DestinationBase;
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint SurfaceZ;
    public uint PitchBytes;
    public uint SliceBytes;
    public uint BlocksPerRow;
    public uint BlocksPerSlice;
    public uint TailX;
    public uint TailY;
    public uint Tail;

    public const uint Size = 52;
}

// Runs the tiling, detiling, depth conversion and channel swap compute passes.
public sealed unsafe class GpuTiler : IDisposable
{
    private const uint KindCount = 9;
    private const uint ElementSizeCount = 5;
    private const uint DirectionCount = 2;
    private const uint PipelineCount = KindCount * ElementSizeCount * DirectionCount;
    private const uint SetsPerPool = 256;

    private struct TransferDispatch
    {
        public TileTransferArguments Arguments;
        public uint PipelineSlot;
        public VkBuffer ArgumentsBuffer;
        public ulong ArgumentsOffset;
    }

    private readonly record struct StorageBinding(DescriptorBufferInfo Info, uint Base);

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly GpuRingBuffer _stream;
    private readonly TickDescriptorPools _pools;
    private readonly Pipeline[] _pipelines = new Pipeline[PipelineCount];
    private DescriptorSetLayout _descriptorLayout;
    private PipelineLayout _pipelineLayout;
    private Pipeline _d16ToD24;
    private Pipeline _d16ToD32;
    private Pipeline _d24ToD16;
    private Pipeline _d32ToD16;
    private Pipeline _swapBgra16;

    public GpuTiler(GpuDeviceInfo device, SubmissionScheduler scheduler, GpuRingBuffer stream)
    {
        _device = device;
        _scheduler = scheduler;
        _stream = stream;
        var vk = device.Vk;
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint index = 0; index < 2; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding(index, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
        }

        bindings[2] = new DescriptorSetLayoutBinding(2, DescriptorType.UniformBuffer, 1, ShaderStageFlags.ComputeBit);
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        RequireSuccess(vk.CreateDescriptorSetLayout(device.Device, &layoutInfo, null, out _descriptorLayout), "vkCreateDescriptorSetLayout(tiler)");
        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, TileTransferArguments.Size);
        fixed (DescriptorSetLayout* layout = &_descriptorLayout)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = layout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushRange,
            };
            RequireSuccess(vk.CreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout(tiler)");
        }

        _pools = new TickDescriptorPools(device, scheduler,
        [
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 2 * SetsPerPool),
            new DescriptorPoolSize(DescriptorType.UniformBuffer, SetsPerPool),
        ], SetsPerPool);
    }

    public void Dispose()
    {
        var vk = _device.Vk;
        foreach (var pipeline in _pipelines)
        {
            DestroyPipeline(pipeline);
        }

        DestroyPipeline(_d16ToD24);
        DestroyPipeline(_d16ToD32);
        DestroyPipeline(_d24ToD16);
        DestroyPipeline(_d32ToD16);
        DestroyPipeline(_swapBgra16);
        _pools.Dispose();
        lock (_scratchGate)
        {
            _scratchDisposed = true;
            foreach (var pooled in _scratchPool.Values)
            {
                foreach (var buffer in pooled)
                {
                    buffer.Dispose();
                }
            }

            _scratchPool.Clear();
            _scratchPooledBytes = 0;
        }

        vk.DestroyPipelineLayout(_device.Device, _pipelineLayout, null);
        vk.DestroyDescriptorSetLayout(_device.Device, _descriptorLayout, null);
        _pipelineLayout = default;
        _descriptorLayout = default;
    }

    private void DestroyPipeline(Pipeline pipeline)
    {
        if (pipeline.Handle != 0)
        {
            _device.Vk.DestroyPipeline(_device.Device, pipeline, null);
        }
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed: result={result}.");
        }
    }

    private ulong StorageAlignment => Math.Max(_device.MinStorageBufferOffsetAlignment, 4);

    // Image uploads and downloads that tile or swap need a scratch every time, several
    // per frame; creating and allocating a Vulkan buffer each time cost ~4 ms per frame.
    // Scratches are pooled by power-of-two capacity and return to the pool when their tick
    // completes, so the GPU is done with them before anyone else records into them.
    private const int ScratchMinimumShift = 16;
    private const int ScratchBuffersPerSize = 8;
    private const ulong ScratchPoolBudget = 512UL << 20;
    private readonly object _scratchGate = new();
    private readonly Dictionary<int, Stack<GpuBuffer>> _scratchPool = new();
    private ulong _scratchPooledBytes;

    // The scratch stays alive through the current tick; the caller sees exactly `size` bytes.
    private TilerBufferSpan AllocateScratch(ulong size)
    {
        if (size == 0)
        {
            throw SubmissionScheduler.Fatal("The tiler scratch size is zero.");
        }

        var shift = Math.Max(ScratchMinimumShift, 64 - BitOperations.LeadingZeroCount(size - 1));
        GpuBuffer? buffer = null;
        lock (_scratchGate)
        {
            if (_scratchPool.TryGetValue(shift, out var pooled) && pooled.TryPop(out var reused))
            {
                buffer = reused;
                _scratchPooledBytes -= buffer.Size;
            }
        }

        buffer ??= new GpuBuffer(_device, _scheduler, GpuBufferUsage.DeviceLocal, 0,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
            shift >= 63 ? size : 1UL << shift);
        _scheduler.QueueCompletionAction(() => ReturnScratch(shift, buffer));
        return new TilerBufferSpan(buffer.Handle, 0, size);
    }

    private void ReturnScratch(int shift, GpuBuffer buffer)
    {
        lock (_scratchGate)
        {
            if (!_scratchDisposed && _scratchPooledBytes + buffer.Size <= ScratchPoolBudget)
            {
                if (!_scratchPool.TryGetValue(shift, out var pooled))
                {
                    pooled = new Stack<GpuBuffer>();
                    _scratchPool[shift] = pooled;
                }

                if (pooled.Count < ScratchBuffersPerSize)
                {
                    pooled.Push(buffer);
                    _scratchPooledBytes += buffer.Size;
                    return;
                }
            }
        }

        buffer.Dispose();
    }

    private bool _scratchDisposed;

    private Pipeline CreateComputePipeline(byte[] spirv, string operation)
    {
        var vk = _device.Vk;
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };
            RequireSuccess(vk.CreateShaderModule(_device.Device, &moduleInfo, null, out module), $"vkCreateShaderModule({operation})");
        }

        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        Pipeline pipeline;
        Result created;
        try
        {
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entry,
                },
                Layout = _pipelineLayout,
            };
            created = vk.CreateComputePipelines(_device.Device, default, 1, &pipelineInfo, null, out pipeline);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
            vk.DestroyShaderModule(_device.Device, module, null);
        }

        RequireSuccess(created, $"vkCreateComputePipelines({operation})");
        return pipeline;
    }

    private Pipeline GetPipeline(uint slot)
    {
        if (slot >= PipelineCount)
        {
            throw SubmissionScheduler.Fatal($"The tiler pipeline slot is out of range: slot={slot}.");
        }

        if (_pipelines[slot].Handle != 0)
        {
            return _pipelines[slot];
        }

        var elementIndex = slot % ElementSizeCount;
        var directionIndex = slot / (KindCount * ElementSizeCount);
        var kindIndex = (slot / ElementSizeCount) % KindCount;
        var spirv = TilerShaders.CreateBlockCopy((TilerBlockShape)kindIndex, 1u << (int)elementIndex, directionIndex != 0);
        _pipelines[slot] = CreateComputePipeline(spirv, $"tiler kind={kindIndex} bytes={1u << (int)elementIndex} tile={directionIndex}");
        return _pipelines[slot];
    }

    private static bool TryMultiply(ulong left, ulong right, out ulong result)
    {
        result = left * right;
        return left == 0 || right <= ulong.MaxValue / left;
    }

    private static bool TryAdd(ulong left, ulong right, out ulong result)
    {
        result = left + right;
        return right <= ulong.MaxValue - left;
    }

    private static bool IsValidRange(ulong offset, ulong size, ulong capacity) => size != 0 && offset <= capacity && size <= capacity - offset;

    // Checks every transfer and writes its arguments to the stream ring.
    private void Prepare(bool tile, ulong tiledCapacity, ulong linearCapacity, ReadOnlySpan<TileTransfer> transfers, ulong sourceBase, ulong targetBase, List<TransferDispatch> dispatches)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTiling);
        if (transfers.IsEmpty || tiledCapacity == 0 || linearCapacity == 0)
        {
            throw SubmissionScheduler.Fatal($"The tile transfer batch is empty or has no capacity: transfers={transfers.Length} tiledCapacity={tiledCapacity} linearCapacity={linearCapacity}.");
        }

        if (tiledCapacity > uint.MaxValue || linearCapacity > uint.MaxValue)
        {
            throw SubmissionScheduler.Fatal($"The tile transfer capacity exceeds 32 bits: tiledCapacity={tiledCapacity} linearCapacity={linearCapacity}.");
        }

        var groupLimit = _device.MaxComputeWorkGroupCount;
        dispatches.Clear();
        dispatches.EnsureCapacity(transfers.Length);
        foreach (ref readonly var transfer in transfers)
        {
            var tiledWidth = transfer.TiledWidth != 0 ? transfer.TiledWidth : transfer.Pitch;
            var tiledHeight = transfer.TiledHeight != 0 ? transfer.TiledHeight : transfer.Height;
            var groupsX = ((ulong)transfer.Width + 7) / 8;
            var groupsY = ((ulong)transfer.Height + 7) / 8;
            if (!TileGeometry.TryGetBlockLayout(transfer.Kind, transfer.BytesPerElement, out var block) || transfer.Width == 0 ||
                transfer.Height == 0 || transfer.Depth == 0 || transfer.Pitch < transfer.Width ||
                groupsX > groupLimit.X || groupsY > groupLimit.Y || transfer.Depth > groupLimit.Z ||
                (!transfer.Tail && (tiledWidth < transfer.Width || tiledHeight < transfer.Height)) ||
                !IsValidRange(transfer.LinearOffset, transfer.LinearSize, linearCapacity) ||
                !IsValidRange(transfer.TiledOffset, transfer.TiledSize, tiledCapacity) ||
                (block.BlockDepth == 1 && transfer.Depth != 1))
            {
                throw SubmissionScheduler.Fatal(
                    $"The tile transfer is invalid: kind={(int)transfer.Kind} bytes={transfer.BytesPerElement} size={transfer.Width}x{transfer.Height}x{transfer.Depth} " +
                    $"pitch={transfer.Pitch} tiled={tiledWidth}x{tiledHeight} tail={transfer.Tail} linear=0x{transfer.LinearOffset:x}+0x{transfer.LinearSize:x}/0x{linearCapacity:x} " +
                    $"tiledRange=0x{transfer.TiledOffset:x}+0x{transfer.TiledSize:x}/0x{tiledCapacity:x}.");
            }

            if (!TryMultiply(transfer.Pitch, transfer.BytesPerElement, out var pitchBytes) || pitchBytes > uint.MaxValue)
            {
                throw SubmissionScheduler.Fatal($"The tile transfer pitch exceeds 32 bits: pitch={transfer.Pitch} bytes={transfer.BytesPerElement}.");
            }

            var sliceBytes = transfer.LinearSliceStride;
            if (!TryMultiply(pitchBytes, transfer.Height, out var minimumSlice))
            {
                throw SubmissionScheduler.Fatal($"The tile transfer slice size overflows: pitchBytes={pitchBytes} height={transfer.Height}.");
            }

            if (sliceBytes == 0)
            {
                sliceBytes = minimumSlice;
            }

            ulong linearUsed = 0;
            if ((transfer.Depth > 1 && sliceBytes < minimumSlice) ||
                !TryMultiply(transfer.Depth - 1u, sliceBytes, out var bytes) || !TryAdd(linearUsed, bytes, out linearUsed) ||
                !TryMultiply(transfer.Height - 1u, pitchBytes, out bytes) || !TryAdd(linearUsed, bytes, out linearUsed) ||
                !TryMultiply(transfer.Width, transfer.BytesPerElement, out bytes) || !TryAdd(linearUsed, bytes, out linearUsed) ||
                linearUsed > transfer.LinearSize || sliceBytes > uint.MaxValue)
            {
                throw SubmissionScheduler.Fatal(
                    $"The tile transfer linear extent does not fit its range: size={transfer.Width}x{transfer.Height}x{transfer.Depth} pitchBytes={pitchBytes} " +
                    $"sliceBytes={sliceBytes} minimumSlice={minimumSlice} used={linearUsed} linearSize={transfer.LinearSize}.");
            }

            var columns = ((ulong)tiledWidth + block.BlockWidth - 1) / block.BlockWidth;
            var rows = ((ulong)tiledHeight + block.BlockHeight - 1) / block.BlockHeight;
            if (!TryMultiply(columns, rows, out var blocksPerSlice) || columns > uint.MaxValue || blocksPerSlice > uint.MaxValue)
            {
                throw SubmissionScheduler.Fatal($"The tile transfer block grid exceeds 32 bits: columns={columns} rows={rows}.");
            }

            if (transfer.Tail)
            {
                if (transfer.Kind == TileBlockKind.Standard256B || transfer.Depth > block.BlockDepth ||
                    transfer.TailX >= block.BlockWidth || transfer.Width > block.BlockWidth - transfer.TailX ||
                    transfer.TailY >= block.BlockHeight || transfer.Height > block.BlockHeight - transfer.TailY ||
                    transfer.TiledSize < block.BlockSize)
                {
                    throw SubmissionScheduler.Fatal(
                        $"The tail tile transfer does not fit one block: kind={(int)transfer.Kind} tail={transfer.TailX},{transfer.TailY} size={transfer.Width}x{transfer.Height}x{transfer.Depth} " +
                        $"block={block.BlockWidth}x{block.BlockHeight}x{block.BlockDepth} blockSize={block.BlockSize} tiledSize={transfer.TiledSize}.");
                }
            }
            else
            {
                var slices = ((ulong)transfer.Depth + block.BlockDepth - 1) / block.BlockDepth;
                if (!TryMultiply(blocksPerSlice, slices, out var tiledUsed) || !TryMultiply(tiledUsed, block.BlockSize, out tiledUsed) || tiledUsed > transfer.TiledSize)
                {
                    throw SubmissionScheduler.Fatal(
                        $"The tile transfer tiled extent does not fit its range: blocksPerSlice={blocksPerSlice} slices={slices} blockSize={block.BlockSize} tiledSize={transfer.TiledSize}.");
                }
            }

            var alignment = Math.Min(transfer.BytesPerElement, 4u);
            if (((transfer.LinearOffset | transfer.TiledOffset | pitchBytes | sliceBytes) & (alignment - 1u)) != 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The tile transfer offsets are not aligned to the element: alignment={alignment} linear=0x{transfer.LinearOffset:x} tiled=0x{transfer.TiledOffset:x} pitchBytes={pitchBytes} sliceBytes={sliceBytes}.");
            }

            var source = sourceBase + (tile ? transfer.LinearOffset : transfer.TiledOffset);
            var destination = targetBase + (tile ? transfer.TiledOffset : transfer.LinearOffset);
            if (source > uint.MaxValue || destination > uint.MaxValue)
            {
                throw SubmissionScheduler.Fatal($"The tile transfer base exceeds 32 bits: source=0x{source:x} destination=0x{destination:x}.");
            }

            var kindIndex = (uint)transfer.Kind;
            var elementIndex = (uint)System.Numerics.BitOperations.TrailingZeroCount(transfer.BytesPerElement);
            if (kindIndex >= KindCount || elementIndex >= ElementSizeCount)
            {
                throw SubmissionScheduler.Fatal($"The tile transfer has no pipeline: kind={kindIndex} bytes={transfer.BytesPerElement}.");
            }

            dispatches.Add(new TransferDispatch
            {
                PipelineSlot = ((tile ? KindCount : 0u) + kindIndex) * ElementSizeCount + elementIndex,
                Arguments = new TileTransferArguments
                {
                    SourceBase = (uint)source,
                    DestinationBase = (uint)destination,
                    Width = transfer.Width,
                    Height = transfer.Height,
                    Depth = transfer.Depth,
                    SurfaceZ = transfer.SurfaceZ,
                    PitchBytes = (uint)pitchBytes,
                    SliceBytes = (uint)sliceBytes,
                    BlocksPerRow = (uint)columns,
                    BlocksPerSlice = (uint)blocksPerSlice,
                    TailX = transfer.TailX,
                    TailY = transfer.TailY,
                    Tail = transfer.Tail ? 1u : 0u,
                },
            });
        }

        var uniformAlignment = Math.Max(_device.MinUniformBufferOffsetAlignment, 1);
        var stride = (TileTransferArguments.Size + uniformAlignment - 1) & ~(uniformAlignment - 1);
        if ((ulong)dispatches.Count > ulong.MaxValue / stride)
        {
            throw SubmissionScheduler.Fatal($"The tile transfer argument block overflows: dispatches={dispatches.Count} stride={stride}.");
        }

        var total = (ulong)dispatches.Count * stride;
        GpuBuffer argumentBuffer = _stream;
        if (!_stream.TryMap(total, out var offset, uniformAlignment))
        {
            // Retained draw bindings can prevent wrapping; keep their bytes intact.
            argumentBuffer = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Upload, 0,
                BufferUsageFlags.UniformBufferBit, total);
            offset = 0;
        }

        var mapped = argumentBuffer.Mapped;
        for (var index = 0; index < dispatches.Count; index++)
        {
            var dispatch = dispatches[index];
            var target = mapped.Slice((int)(offset + (ulong)index * stride), (int)TileTransferArguments.Size);
            MemoryMarshal.Write(target, in dispatch.Arguments);
            dispatch.ArgumentsBuffer = argumentBuffer.Handle;
            dispatch.ArgumentsOffset = offset + (ulong)index * stride;
            dispatches[index] = dispatch;
        }

        if (ReferenceEquals(argumentBuffer, _stream))
        {
            _stream.Commit();
        }
        else
        {
            argumentBuffer.Flush(0, total);
            _scheduler.QueueCompletionAction(argumentBuffer.Dispose);
        }
    }

    private static BufferMemoryBarrier2 Barrier(VkBuffer buffer, ulong offset, ulong size, AccessFlags source, AccessFlags destination) => new()
    {
        SType = StructureType.BufferMemoryBarrier2,
        SrcAccessMask = VulkanSynchronization.Access(source),
        DstAccessMask = VulkanSynchronization.Access(destination),
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Buffer = buffer,
        Offset = offset,
        Size = size,
    };

    private DescriptorSet WriteDescriptorSet(ReadOnlySpan<DescriptorBufferInfo> infos, int uniformIndex)
    {
        var set = _pools.Allocate(_descriptorLayout);
        var writes = stackalloc WriteDescriptorSet[3];
        fixed (DescriptorBufferInfo* pointer = infos)
        {
            for (var index = 0; index < infos.Length; index++)
            {
                writes[index] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = set,
                    DstBinding = (uint)index,
                    DescriptorCount = 1,
                    DescriptorType = index == uniformIndex ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                    PBufferInfo = pointer + index,
                };
            }

            _device.Vk.UpdateDescriptorSets(_device.Device, (uint)infos.Length, writes, 0, null);
        }

        return set;
    }

    private void BindDescriptorSet(CommandBuffer command, DescriptorSet set) =>
        _device.Vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);

    private void Record(bool tile, VkBuffer source, ulong sourceOffset, ulong sourceCapacity, VkBuffer target, ulong targetOffset, ulong targetCapacity, List<TransferDispatch> dispatches, bool clearTarget)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTiling);
        var vk = _device.Vk;
        var descriptorAlignment = StorageAlignment;
        var sourceDescriptorOffset = sourceOffset & ~(descriptorAlignment - 1);
        var targetDescriptorOffset = targetOffset & ~(descriptorAlignment - 1);
        var sourceBase = sourceOffset - sourceDescriptorOffset;
        var targetBase = targetOffset - targetDescriptorOffset;
        var sourceRange = (sourceBase + sourceCapacity + 3) & ~3UL;
        var targetRange = (targetBase + targetCapacity + 3) & ~3UL;
        if (sourceRange > _device.MaxStorageBufferRange || targetRange > _device.MaxStorageBufferRange || targetOffset % 4 != 0 || targetCapacity % 4 != 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The tile transfer buffers cannot be bound: sourceRange={sourceRange} targetRange={targetRange} limit={_device.MaxStorageBufferRange} targetOffset=0x{targetOffset:x} targetCapacity=0x{targetCapacity:x}.");
        }

        _scheduler.EndRendering();
        var command = new CommandBuffer(_scheduler.Current.Handle);
        var barriers = stackalloc BufferMemoryBarrier2[3];
        barriers[0] = Barrier(source, sourceOffset, sourceCapacity, AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit, AccessFlags.ShaderReadBit);
        barriers[1] = Barrier(target, targetOffset, targetCapacity, AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit,
            clearTarget ? AccessFlags.TransferWriteBit : AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        var first = dispatches[0].ArgumentsOffset;
        var last = dispatches[^1].ArgumentsOffset;
        barriers[2] = Barrier(dispatches[0].ArgumentsBuffer, first, last - first + TileTransferArguments.Size, AccessFlags.HostWriteBit, AccessFlags.UniformReadBit);
        VulkanSynchronization.PipelineBarrier(vk,command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit, 0, 0, null, 3, barriers, 0, null);
        if (clearTarget)
        {
            vk.CmdFillBuffer(command, target, targetOffset, targetCapacity, 0);
            barriers[1].SrcAccessMask = AccessFlags2.TransferWriteBit;
            barriers[1].DstAccessMask = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit;
            VulkanSynchronization.PipelineBarrier(vk,command, PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 1, barriers + 1, 0, null);
        }

        Span<DescriptorBufferInfo> infos = stackalloc DescriptorBufferInfo[3];
        infos[0] = new DescriptorBufferInfo(source, sourceDescriptorOffset, sourceRange);
        infos[1] = new DescriptorBufferInfo(target, targetDescriptorOffset, targetRange);
        foreach (var dispatch in dispatches)
        {
            infos[2] = new DescriptorBufferInfo(dispatch.ArgumentsBuffer, dispatch.ArgumentsOffset, TileTransferArguments.Size);
            BindDescriptorSet(command, WriteDescriptorSet(infos, 2));
            vk.CmdBindPipeline(command, PipelineBindPoint.Compute, GetPipeline(dispatch.PipelineSlot));
            vk.CmdDispatch(command, (dispatch.Arguments.Width + 7) / 8, (dispatch.Arguments.Height + 7) / 8, dispatch.Arguments.Depth);
        }

        barriers[1].SrcAccessMask = AccessFlags2.ShaderWriteBit;
        barriers[1].DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.MemoryReadBit;
        VulkanSynchronization.PipelineBarrier(vk,command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 1, barriers + 1, 0, null);
    }

    // Detiles into a scratch buffer that lives through the current tick.
    public TilerBufferSpan Detile(VkBuffer tiled, ulong tiledOffset, ulong tiledCapacity, ulong linearCapacity, ReadOnlySpan<TileTransfer> transfers)
    {
        var sourceBase = tiledOffset & (StorageAlignment - 1);
        var dispatches = new List<TransferDispatch>();
        Prepare(false, tiledCapacity, linearCapacity, transfers, sourceBase, 0, dispatches);
        var scratch = AllocateScratch((linearCapacity + 3) & ~3UL);
        Record(false, tiled, tiledOffset, tiledCapacity, scratch.Buffer, 0, scratch.Size, dispatches, true);
        return new TilerBufferSpan(scratch.Buffer, 0, linearCapacity);
    }

    public void Tile(VkBuffer linear, ulong linearOffset, ulong linearCapacity, VkBuffer tiled, ulong tiledOffset, ulong tiledCapacity, ReadOnlySpan<TileTransfer> transfers)
    {
        var sourceBase = linearOffset & (StorageAlignment - 1);
        var targetBase = tiledOffset & (StorageAlignment - 1);
        var dispatches = new List<TransferDispatch>();
        Prepare(true, tiledCapacity, linearCapacity, transfers, sourceBase, targetBase, dispatches);
        Record(true, linear, linearOffset, linearCapacity, tiled, tiledOffset, tiledCapacity, dispatches, false);
    }

    // Downloads the image to a scratch, then tiles it; the arguments are reserved before the scratch exists.
    public void TileImage(CachedImage image, ReadOnlySpan<BufferImageCopy> regions, VkBuffer tiled, ulong tiledOffset, ulong tiledCapacity, ulong linearCapacity, ReadOnlySpan<TileTransfer> transfers, ColorChannelSwap swap = ColorChannelSwap.None)
    {
        if (regions.IsEmpty)
        {
            throw SubmissionScheduler.Fatal("The image tiling has no copy regions.");
        }

        var targetBase = tiledOffset & (StorageAlignment - 1);
        var dispatches = new List<TransferDispatch>();
        Prepare(true, tiledCapacity, linearCapacity, transfers, 0, targetBase, dispatches);
        var linear = AllocateScratch((linearCapacity + 3) & ~3UL);
        image.DownloadToBuffer(regions, linear.Buffer, 0, linear.Size);
        var source = linear;
        if (swap == ColorChannelSwap.SwapBgra16)
        {
            source = SwapBgra16(source);
        }

        Record(true, source.Buffer, source.Offset, linearCapacity, tiled, tiledOffset, tiledCapacity, dispatches, false);
    }

    public TilerBufferSpan GetScratchBuffer(ulong size) => AllocateScratch((size + 3) & ~3UL);

    private StorageBinding BindStorage(TilerBufferSpan buffer, ulong size)
    {
        var alignment = StorageAlignment;
        var descriptorOffset = buffer.Offset - buffer.Offset % alignment;
        var baseOffset = buffer.Offset - descriptorOffset;
        if (buffer.Buffer.Handle == 0 || size == 0 || buffer.Size < size || baseOffset > uint.MaxValue || size > ulong.MaxValue - baseOffset || baseOffset + size > ulong.MaxValue - 3)
        {
            throw SubmissionScheduler.Fatal($"The storage binding is invalid: offset=0x{buffer.Offset:x} size={size} bufferSize={buffer.Size}.");
        }

        var range = (baseOffset + size + 3) & ~3UL;
        if (range > _device.MaxStorageBufferRange || range > uint.MaxValue)
        {
            throw SubmissionScheduler.Fatal($"The storage binding range exceeds the device limit: range={range} limit={_device.MaxStorageBufferRange}.");
        }

        return new StorageBinding(new DescriptorBufferInfo(buffer.Buffer, descriptorOffset, range), (uint)baseOffset);
    }

    // Rows one conversion dispatch may cover without exceeding the descriptor range or group limit.
    public static uint ConversionRows(ulong offset, ulong rowStride, ulong active, uint remaining, ulong alignment, ulong maxRange, uint maxGroups)
    {
        if (rowStride == 0 || active == 0 || remaining == 0 || alignment == 0 || maxGroups == 0)
        {
            return 0;
        }

        var prefix = offset % alignment;
        if (prefix >= maxRange || active > maxRange - prefix)
        {
            return 0;
        }

        var descriptorRows = 1 + (maxRange - prefix - active) / rowStride;
        return (uint)Math.Min(Math.Min(remaining, descriptorRows), maxGroups);
    }

    private static ulong RequiredBytes(uint height, uint layers, ulong rowStride, ulong sliceStride, ulong active)
    {
        if (height == 0 || layers == 0 || rowStride < active || height - 1 > (ulong.MaxValue - active) / rowStride)
        {
            throw SubmissionScheduler.Fatal($"The depth conversion layout is invalid: height={height} layers={layers} rowStride={rowStride} active={active}.");
        }

        var slice = (ulong)(height - 1) * rowStride + active;
        if (sliceStride < slice || layers - 1 > (ulong.MaxValue - slice) / sliceStride)
        {
            throw SubmissionScheduler.Fatal($"The depth conversion slice stride is too small: sliceStride={sliceStride} slice={slice} layers={layers}.");
        }

        return (ulong)(layers - 1) * sliceStride + slice;
    }

    private void PushArguments(CommandBuffer command, in TileTransferArguments arguments)
    {
        fixed (TileTransferArguments* pointer = &arguments)
        {
            _device.Vk.CmdPushConstants(command, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, TileTransferArguments.Size, pointer);
        }
    }

    public void ConvertDepth16(TilerBufferSpan source, TilerBufferSpan target, DepthConversionDirection direction, bool useFloat32Depth, in DepthConversionLayout layout)
    {
        ref var pipeline = ref (direction == DepthConversionDirection.Widen ? ref (useFloat32Depth ? ref _d16ToD32 : ref _d16ToD24) : ref (useFloat32Depth ? ref _d32ToD16 : ref _d24ToD16));
        if (pipeline.Handle == 0)
        {
            var spirv = direction == DepthConversionDirection.Widen ? TilerShaders.CreateDepthWiden(useFloat32Depth) : TilerShaders.CreateDepthNarrow(useFloat32Depth);
            pipeline = CreateComputePipeline(spirv, $"depth16 {direction} d32={useFloat32Depth}");
        }

        var sourceElement = direction == DepthConversionDirection.Widen ? 2UL : 4UL;
        var targetElement = direction == DepthConversionDirection.Widen ? 4UL : 2UL;
        var sourceActive = layout.Width * sourceElement;
        var targetActive = layout.Width * targetElement;
        if (layout.Width == 0 || layout.SourceRowStride > uint.MaxValue || layout.TargetRowStride > uint.MaxValue)
        {
            throw SubmissionScheduler.Fatal($"The depth conversion layout is invalid: width={layout.Width} sourceRowStride={layout.SourceRowStride} targetRowStride={layout.TargetRowStride}.");
        }

        var sourceRequired = RequiredBytes(layout.Height, layout.Layers, layout.SourceRowStride, layout.SourceSliceStride, sourceActive);
        var targetRequired = RequiredBytes(layout.Height, layout.Layers, layout.TargetRowStride, layout.TargetSliceStride, targetActive);
        if (sourceRequired > ulong.MaxValue - 3 || targetRequired > ulong.MaxValue - 3)
        {
            throw SubmissionScheduler.Fatal($"The depth conversion size overflows: source={sourceRequired} target={targetRequired}.");
        }

        var sourceBarrierSize = (sourceRequired + 3) & ~3UL;
        var targetBarrierSize = (targetRequired + 3) & ~3UL;
        if (source.Size < sourceBarrierSize || target.Size < targetBarrierSize)
        {
            throw SubmissionScheduler.Fatal($"The depth conversion buffers are too small: source={source.Size}/{sourceBarrierSize} target={target.Size}/{targetBarrierSize}.");
        }

        var vk = _device.Vk;
        _scheduler.EndRendering();
        var command = new CommandBuffer(_scheduler.Current.Handle);
        var barriers = stackalloc BufferMemoryBarrier2[2];
        barriers[0] = Barrier(source.Buffer, source.Offset, sourceBarrierSize,
            AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit | AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit);
        barriers[1] = Barrier(target.Buffer, target.Offset, targetBarrierSize,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit | AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
        VulkanSynchronization.PipelineBarrier(vk,command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 2, barriers, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, pipeline);

        var descriptorAlignment = StorageAlignment;
        var groupLimit = _device.MaxComputeWorkGroupCount;
        var groupsX = ((ulong)layout.Width + 63) / 64;
        if (groupsX == 0 || groupsX > groupLimit.X)
        {
            throw SubmissionScheduler.Fatal($"The depth conversion width exceeds the group limit: width={layout.Width} groups={groupsX} limit={groupLimit.X}.");
        }

        uint RowsFor(TilerBufferSpan buffer, ulong relative, ulong stride, ulong active, uint remaining)
        {
            if (relative > buffer.Size || buffer.Offset > ulong.MaxValue - relative)
            {
                throw SubmissionScheduler.Fatal($"The depth conversion row start is outside the buffer: relative={relative} size={buffer.Size} offset=0x{buffer.Offset:x}.");
            }

            return ConversionRows(buffer.Offset + relative, stride, active, remaining, descriptorAlignment, _device.MaxStorageBufferRange, groupLimit.Y);
        }

        Span<DescriptorBufferInfo> infos = stackalloc DescriptorBufferInfo[2];
        for (uint layer = 0; layer < layout.Layers; layer++)
        {
            for (uint row = 0; row < layout.Height;)
            {
                var sourceRelative = layout.SourceSliceStride * layer + layout.SourceRowStride * row;
                var targetRelative = layout.TargetSliceStride * layer + layout.TargetRowStride * row;
                var remaining = layout.Height - row;
                var rows = Math.Min(
                    RowsFor(source, sourceRelative, layout.SourceRowStride, sourceActive, remaining),
                    RowsFor(target, targetRelative, layout.TargetRowStride, targetActive, remaining));
                if (rows == 0)
                {
                    throw SubmissionScheduler.Fatal($"The depth conversion cannot bind one row: width={layout.Width} row={row} layer={layer}.");
                }

                var sourceSpan = (ulong)(rows - 1) * layout.SourceRowStride + sourceActive;
                var targetSpan = (ulong)(rows - 1) * layout.TargetRowStride + targetActive;
                var sourceBinding = BindStorage(new TilerBufferSpan(source.Buffer, source.Offset + sourceRelative, source.Size - sourceRelative), sourceSpan);
                var targetBinding = BindStorage(new TilerBufferSpan(target.Buffer, target.Offset + targetRelative, target.Size - targetRelative), targetSpan);
                infos[0] = sourceBinding.Info;
                infos[1] = targetBinding.Info;
                BindDescriptorSet(command, WriteDescriptorSet(infos, -1));
                PushArguments(command, new TileTransferArguments
                {
                    SourceBase = sourceBinding.Base,
                    DestinationBase = targetBinding.Base,
                    Width = layout.Width,
                    Height = rows,
                    PitchBytes = (uint)layout.SourceRowStride,
                    SliceBytes = (uint)layout.TargetRowStride,
                });
                vk.CmdDispatch(command, (uint)groupsX, rows, 1);
                row += rows;
            }
        }

        barriers[1].SrcAccessMask = AccessFlags2.ShaderWriteBit;
        barriers[1].DstAccessMask = AccessFlags2.TransferReadBit | AccessFlags2.MemoryReadBit;
        VulkanSynchronization.PipelineBarrier(vk,command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit, 0, 0, null, 1, barriers + 1, 0, null);
    }

    private void SwapBgra16(TilerBufferSpan input, TilerBufferSpan output, uint pixels)
    {
        if (_swapBgra16.Handle == 0)
        {
            _swapBgra16 = CreateComputePipeline(TilerShaders.CreateBgra16Swap(), "bgra16 swap");
        }

        if (pixels == 0)
        {
            throw SubmissionScheduler.Fatal("The BGRA16 swap has no pixels.");
        }

        var bytes = (ulong)pixels * 8;
        var inputBinding = BindStorage(input, bytes);
        var outputBinding = BindStorage(output, bytes);
        Span<DescriptorBufferInfo> infos = stackalloc DescriptorBufferInfo[2];
        infos[0] = inputBinding.Info;
        infos[1] = outputBinding.Info;
        var barriers = stackalloc BufferMemoryBarrier2[2];
        barriers[0] = Barrier(infos[0].Buffer, infos[0].Offset, infos[0].Range, AccessFlags.MemoryWriteBit | AccessFlags.HostWriteBit | AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit);
        barriers[1] = Barrier(infos[1].Buffer, infos[1].Offset, infos[1].Range, AccessFlags.MemoryReadBit, AccessFlags.ShaderWriteBit);
        var vk = _device.Vk;
        _scheduler.EndRendering();
        var command = new CommandBuffer(_scheduler.Current.Handle);
        VulkanSynchronization.PipelineBarrier(vk,command, PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 2, barriers, 0, null);
        vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _swapBgra16);
        BindDescriptorSet(command, WriteDescriptorSet(infos, -1));
        PushArguments(command, new TileTransferArguments { SourceBase = inputBinding.Base, DestinationBase = outputBinding.Base, Width = pixels });
        vk.CmdDispatch(command, (pixels + 63) / 64, 1, 1);
        barriers[1].SrcAccessMask = AccessFlags2.ShaderWriteBit;
        barriers[1].DstAccessMask = AccessFlags2.TransferReadBit;
        VulkanSynchronization.PipelineBarrier(vk,command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.TransferBit, 0, 0, null, 1, barriers + 1, 0, null);
    }

    public TilerBufferSpan SwapBgra16(TilerBufferSpan input)
    {
        if (input.Size == 0 || input.Size % 8 != 0 || input.Size / 8 > uint.MaxValue)
        {
            throw SubmissionScheduler.Fatal($"The BGRA16 swap input size is invalid: size={input.Size}.");
        }

        var result = AllocateScratch(input.Size);
        SwapBgra16(input, result, (uint)(input.Size / 8));
        return result;
    }

    public void SwapBgra16(TilerBufferSpan input, TilerBufferSpan output)
    {
        if (input.Size == 0 || input.Size % 8 != 0 || input.Size / 8 > uint.MaxValue || output.Size < input.Size)
        {
            throw SubmissionScheduler.Fatal($"The BGRA16 swap sizes are invalid: input={input.Size} output={output.Size}.");
        }

        SwapBgra16(input, output, (uint)(input.Size / 8));
    }
}

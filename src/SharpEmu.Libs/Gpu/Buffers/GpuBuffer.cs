// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Buffers;

// One Vulkan buffer, mapped when host-visible. Small buffers share slab chunks (GpuMemorySlabs);
// larger ones get a dedicated allocation.
public unsafe class GpuBuffer : IDisposable
{
    public const BufferUsageFlags ReadFlags =
        BufferUsageFlags.TransferSrcBit | BufferUsageFlags.UniformBufferBit | BufferUsageFlags.IndexBufferBit |
        BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndirectBufferBit;

    public const BufferUsageFlags AllFlags = ReadFlags | BufferUsageFlags.TransferDstBit | BufferUsageFlags.StorageBufferBit;

    private const AccessFlags MemoryAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
    private const AccessFlags HostAccess = AccessFlags.HostReadBit | AccessFlags.HostWriteBit;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly ulong _allocationSize;
    private readonly byte* _mapped;
    private readonly ulong _deviceAddress;
    private VkBuffer _handle;
    private DeviceMemory _memory;
    private readonly ulong _memoryOffset;
    private readonly GpuMemorySlabs.Block? _slab;

    // allowSlab: the caller records on the scheduler's current command buffer, which clears a recycled slab block.
    public GpuBuffer(GpuDeviceInfo device, SubmissionScheduler scheduler, GpuBufferUsage usage, ulong cpuAddress, BufferUsageFlags flags, ulong size,
        bool allowSlab = false)
    {
        if (size == 0)
        {
            throw SubmissionScheduler.Fatal("The buffer size is invalid.");
        }

        _device = device;
        _scheduler = scheduler;
        Usage = usage;
        CpuAddress = cpuAddress;
        Size = size;

        var vk = device.Vk;
        var sharedFamilies = device.SharedQueueFamilies;
        fixed (uint* families = sharedFamilies)
        {
            // Buffers carry no layout, so concurrent sharing lets the readback queue copy
            // them without queue-family ownership transfers at no cost to other queues.
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = flags,
                SharingMode = sharedFamilies is { Length: > 1 } ? SharingMode.Concurrent : SharingMode.Exclusive,
                QueueFamilyIndexCount = sharedFamilies is { Length: > 1 } ? (uint)sharedFamilies.Length : 0,
                PQueueFamilyIndices = sharedFamilies is { Length: > 1 } ? families : null,
            };
            RequireSuccess(vk.CreateBuffer(device.Device, &bufferInfo, null, out _handle), "vkCreateBuffer");
        }

        vk.GetBufferMemoryRequirements(device.Device, _handle, out var requirements);
        var withAddress = (flags & BufferUsageFlags.ShaderDeviceAddressBit) != 0;
        var flagsInfo = new MemoryAllocateFlagsInfo
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit,
        };
        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = withAddress ? &flagsInfo : null,
            AllocationSize = requirements.Size,
        };

        // The best-scored type first; a full heap falls through to the next candidate.
        var result = Result.ErrorOutOfDeviceMemory;
        var candidates = RankMemoryTypes(device, requirements.MemoryTypeBits, usage).ToArray();
        if (allowSlab && GpuMemorySlabs.Fits(requirements))
        {
            foreach (var candidate in candidates)
            {
                if (device.Slabs.TryAllocate(candidate, withAddress, requirements, out var block))
                {
                    _slab = block;
                    _memory = block.Memory;
                    _memoryOffset = block.Offset;
                    _mapped = block.Mapped;
                    allocateInfo.MemoryTypeIndex = candidate;
                    result = Result.Success;
                    break;
                }
            }
        }

        if (_slab is null)
        {
            foreach (var candidate in candidates)
            {
                allocateInfo.MemoryTypeIndex = candidate;
                result = device.AllocateMemory(allocateInfo, out _memory);
                if (result == Result.Success)
                {
                    break;
                }
            }
        }

        RequireSuccess(result, $"vkAllocateMemory({usage}, 0x{size:X} bytes)");
        RequireSuccess(vk.BindBufferMemory(device.Device, _handle, _memory, _memoryOffset), "vkBindBufferMemory");
        _allocationSize = _slab?.Size ?? requirements.Size;
        var properties = device.GetMemoryTypeFlags(allocateInfo.MemoryTypeIndex);
        IsCoherent = (properties & MemoryPropertyFlags.HostCoherentBit) != 0;
        if (_slab is null && (properties & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* pointer;
            RequireSuccess(vk.MapMemory(device.Device, _memory, 0, Vk.WholeSize, 0, &pointer), "vkMapMemory");
            _mapped = (byte*)pointer;
        }

        if (withAddress)
        {
            var addressInfo = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = _handle };
            _deviceAddress = vk.GetBufferDeviceAddress(device.Device, &addressInfo);
            if (_deviceAddress == 0)
            {
                throw SubmissionScheduler.Fatal("The buffer device address is unavailable.");
            }
        }

        // A dedicated allocation arrives zeroed; keep that for a block another buffer used.
        if (_slab is { Recycled: true } && (Size & ~3UL) != 0)
        {
            Fill(0, Size & ~3UL, 0);
        }
    }

    public VkBuffer Handle => _handle;

    public ulong Size { get; }

    public Span<byte> Mapped => _mapped == null ? Span<byte>.Empty : new Span<byte>(_mapped, checked((int)Size));

    public bool IsCoherent { get; }

    public GpuBufferUsage Usage { get; }

    public ulong CpuAddress { get; }

    public ulong DeviceAddress => _deviceAddress != 0 ? _deviceAddress : throw SubmissionScheduler.Fatal("The buffer has no device address.");

    public int StreamScore { get; private set; }

    protected GpuDeviceInfo Device => _device;

    protected SubmissionScheduler Scheduler => _scheduler;

    public ulong Offset(ulong address) => address - CpuAddress;

    public bool IsInBounds(ulong address, ulong size) =>
        address >= CpuAddress && size <= Size && address - CpuAddress <= Size - size;

    public void AddStreamScore(int score) => StreamScore += score;

    // The highest scheduler tick whose command buffer may write this buffer on the GPU.
    // A readback of it only has to wait for that tick, not for all queued work.
    public ulong LastGpuWriteTick { get; private set; }

    // Called by every path that records a GPU write into this buffer.
    public void NoteGpuWrite()
    {
        var tick = _scheduler.CurrentTick;
        if (tick > LastGpuWriteTick)
        {
            LastGpuWriteTick = tick;
        }
    }

    public void Write(ulong offset, ReadOnlySpan<byte> source)
    {
        if (_mapped == null || offset > Size || (ulong)source.Length > Size - offset)
        {
            throw SubmissionScheduler.Fatal("The mapped buffer write range is invalid.");
        }

        source.CopyTo(Mapped[(int)offset..]);
        Flush(offset, (ulong)source.Length);
    }

    public void Flush(ulong offset, ulong size)
    {
        if (_mapped == null || offset > Size || size > Size - offset)
        {
            throw SubmissionScheduler.Fatal("The buffer flush range is invalid.");
        }

        if (!IsCoherent && size != 0)
        {
            var range = GetMappedRange(offset, size);
            RequireSuccess(_device.Vk.FlushMappedMemoryRanges(_device.Device, 1, &range), "vkFlushMappedMemoryRanges");
        }
    }

    public void Invalidate(ulong offset, ulong size)
    {
        if (Usage != GpuBufferUsage.Download || offset > Size || size > Size - offset)
        {
            throw SubmissionScheduler.Fatal("The buffer invalidation range is invalid.");
        }

        if (!IsCoherent && size != 0)
        {
            var range = GetMappedRange(offset, size);
            RequireSuccess(_device.Vk.InvalidateMappedMemoryRanges(_device.Device, 1, &range), "vkInvalidateMappedMemoryRanges");
        }
    }

    public void CopyFrom(
        RecordingBuffer command,
        GpuBuffer source,
        ulong sourceOffset,
        ulong destinationOffset,
        ulong size,
        AccessFlags sourceBefore = AccessFlags.MemoryWriteBit,
        AccessFlags destinationBefore = MemoryAccess,
        AccessFlags sourceAfter = MemoryAccess,
        AccessFlags destinationAfter = MemoryAccess)
    {
        if (size == 0 || sourceOffset > source.Size || size > source.Size - sourceOffset || destinationOffset > Size || size > Size - destinationOffset)
        {
            throw SubmissionScheduler.Fatal("The buffer copy range is invalid.");
        }

        if (source.Handle.Handle == Handle.Handle && sourceOffset < destinationOffset + size && destinationOffset < sourceOffset + size)
        {
            throw SubmissionScheduler.Fatal("Cannot copy overlapping ranges of the same buffer.");
        }

        command.EndRendering();
        NoteGpuWrite();
        var vk = _device.Vk;
        var native = new CommandBuffer(command.Handle);
        var before = stackalloc BufferMemoryBarrier2[2];
        before[0] = source.CreateBarrier(sourceOffset, size, sourceBefore, AccessFlags.TransferReadBit);
        before[1] = CreateBarrier(destinationOffset, size, destinationBefore, AccessFlags.TransferWriteBit);
        VulkanSynchronization.PipelineBarrier(vk,
            native, GetAccessStage(sourceBefore | destinationBefore), PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
            0, null, 2, before, 0, null);
        var copy = new BufferCopy(sourceOffset, destinationOffset, size);
        vk.CmdCopyBuffer(native, source.Handle, Handle, 1, &copy);
        var after = stackalloc BufferMemoryBarrier2[2];
        after[0] = source.CreateBarrier(sourceOffset, size, AccessFlags.TransferReadBit, sourceAfter);
        after[1] = CreateBarrier(destinationOffset, size, AccessFlags.TransferWriteBit, destinationAfter);
        VulkanSynchronization.PipelineBarrier(vk,
            native, PipelineStageFlags.TransferBit, GetAccessStage(sourceAfter | destinationAfter), DependencyFlags.ByRegionBit,
            0, null, 2, after, 0, null);
    }

    public void Fill(ulong offset, ulong size, uint value)
    {
        if (((offset | size) & 3) != 0)
        {
            throw SubmissionScheduler.Fatal("The buffer fill range must be aligned to four bytes.");
        }

        var command = _scheduler.Current;
        command.EndRendering();
        NoteGpuWrite();
        var vk = _device.Vk;
        var native = new CommandBuffer(command.Handle);
        var before = CreateBarrier(offset, size, MemoryAccess, AccessFlags.TransferWriteBit);
        VulkanSynchronization.PipelineBarrier(vk,
            native, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit,
            0, null, 1, &before, 0, null);
        vk.CmdFillBuffer(native, Handle, offset, size, value);
        var after = CreateBarrier(offset, size, AccessFlags.TransferWriteBit, MemoryAccess);
        VulkanSynchronization.PipelineBarrier(vk,
            native, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit,
            0, null, 1, &after, 0, null);
    }

    private int _foreignReads;
    private int _disposeDeferred;

    public void RetainForeignRead() => Interlocked.Increment(ref _foreignReads);

    public void ReleaseForeignRead()
    {
        if (Interlocked.Decrement(ref _foreignReads) == 0 && Interlocked.Exchange(ref _disposeDeferred, 0) != 0)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_handle.Handle == 0)
        {
            return;
        }

        if (Volatile.Read(ref _foreignReads) != 0)
        {
            Volatile.Write(ref _disposeDeferred, 1);
            if (Volatile.Read(ref _foreignReads) != 0 || Interlocked.Exchange(ref _disposeDeferred, 0) == 0)
            {
                return;
            }
        }

        _device.Vk.DestroyBuffer(_device.Device, _handle, null);
        if (_slab is { } slab)
        {
            _device.Slabs.Release(slab);
        }
        else
        {
            _device.FreeMemory(_memory);
        }

        _handle = default;
        _memory = default;
    }

    // Host access in a mask adds the host stage to the barrier.
    private static PipelineStageFlags GetAccessStage(AccessFlags access) =>
        (access & HostAccess) != 0
            ? PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit
            : PipelineStageFlags.AllCommandsBit;

    private static IEnumerable<uint> RankMemoryTypes(GpuDeviceInfo device, uint typeBits, GpuBufferUsage usage)
    {
        var (required, preferred, avoided) = usage switch
        {
            GpuBufferUsage.DeviceLocal => (MemoryPropertyFlags.None, MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None),
            GpuBufferUsage.Upload => (MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.DeviceLocalBit),
            GpuBufferUsage.Download => (MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit, MemoryPropertyFlags.DeviceLocalBit),
            _ => (MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.DeviceLocalBit, MemoryPropertyFlags.None),
        };
        var candidates = new List<(int Score, uint Index)>();
        for (uint index = 0; index < device.MemoryTypeCount; index++)
        {
            var flags = device.GetMemoryTypeFlags(index);
            if ((typeBits & (1u << (int)index)) != 0 && (flags & required) == required)
            {
                var score = BitOperations.PopCount((uint)(flags & preferred)) - BitOperations.PopCount((uint)(flags & avoided));
                candidates.Add((score, index));
            }
        }

        return candidates.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Index).Select(candidate => candidate.Index);
    }

    private MappedMemoryRange GetMappedRange(ulong offset, ulong size)
    {
        var atom = _device.NonCoherentAtomSize;
        var begin = offset / atom * atom;
        var end = Math.Min((offset + size + atom - 1) / atom * atom, _allocationSize);
        return new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = _memory,
            Offset = _memoryOffset + begin,
            Size = end - begin,
        };
    }

    private BufferMemoryBarrier2 CreateBarrier(ulong offset, ulong size, AccessFlags source, AccessFlags destination)
    {
        if (Handle.Handle == 0 || size == 0 || offset > Size || size > Size - offset)
        {
            throw SubmissionScheduler.Fatal(
                $"The DMA barrier range is invalid: handle=0x{Handle.Handle:X} offset=0x{offset:X16} size=0x{size:X16} capacity=0x{Size:X16}");
        }

        return new BufferMemoryBarrier2
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcAccessMask = VulkanSynchronization.Access(source),
            DstAccessMask = VulkanSynchronization.Access(destination),
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = Handle,
            Offset = offset,
            Size = size,
        };
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}

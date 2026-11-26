// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Buffers;

// Keep the entire shader-visible address space, including missing-page faults. Bind physical memory
// only for table entries the cache writes. Existing descriptors always name the same Vulkan buffer.
internal sealed unsafe class SparseAddressTable
{
    private const ulong Size = GuestBufferCache.BdaPageTableSize;
    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly uint _memoryType;
    private readonly ulong _blockSize;
    private readonly DeviceMemory[] _blocks;
    private CommandPool _clearPool;
    private CommandBuffer _clear;
    private VkSemaphore _prior, _bound;
    private Fence _fence;
    private bool _pending, _incompleteSubmission;
    public GpuBuffer Buffer { get; }

    internal static bool CanEnable(Vk vk, PhysicalDevice physical, uint family, PhysicalDeviceFeatures features)
    {
        if (!features.SparseBinding || !features.SparseResidencyBuffer) return false;
        vk.GetPhysicalDeviceProperties(physical, out var properties);
        if (!properties.SparseProperties.ResidencyNonResidentStrict || properties.Limits.SparseAddressSpaceSize < Size) return false;
        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families) vk.GetPhysicalDeviceQueueFamilyProperties(physical, &count, p);
        return family < count && (families[family].QueueFlags & QueueFlags.SparseBindingBit) != 0;
    }

    internal static SparseAddressTable? TryCreate(GpuDeviceInfo device, SubmissionScheduler scheduler)
    {
        if (device.SparseQueue.Handle == 0) return null;
        var create = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = Size, Usage = GpuBuffer.AllFlags,
            Flags = BufferCreateFlags.SparseBindingBit | BufferCreateFlags.SparseResidencyBit, SharingMode = SharingMode.Exclusive };
        var created = device.Vk.CreateBuffer(device.Device, &create, null, out VkBuffer handle);
        if (created != Result.Success) return null;
        device.Vk.GetBufferMemoryRequirements(device.Device, handle, out var req);
        for (uint i = 0; i < device.MemoryTypeCount; i++)
        {
            if ((req.MemoryTypeBits & (1u << (int)i)) == 0 || (device.GetMemoryTypeFlags(i) & MemoryPropertyFlags.DeviceLocalBit) == 0) continue;
            var block = Math.Max(4UL << 20, req.Alignment);
            if (Size % block != 0 || block > int.MaxValue) break;
            return new SparseAddressTable(device, scheduler, handle, i, block);
        }
        device.Vk.DestroyBuffer(device.Device, handle, null);
        return null;
    }

    private SparseAddressTable(GpuDeviceInfo device, SubmissionScheduler scheduler, VkBuffer handle, uint memoryType, ulong blockSize)
    {
        _device = device;
        _scheduler = scheduler;
        _memoryType = memoryType;
        _blockSize = blockSize;
        _blocks = new DeviceMemory[Size / blockSize];
        Buffer = new GpuBuffer(device, scheduler, handle, Size);
    }

    internal void Cover(ulong offset, ulong size)
    {
        int first = (int)(offset / _blockSize), last = (int)((offset + size - 1) / _blockSize);
        if (first == last && _blocks[first].Handle != 0) return;
        Bind(first, last);
    }

    private void Bind(int first, int last)
    {
        Span<SparseMemoryBind> binds = stackalloc SparseMemoryBind[last - first + 1];
        int count = 0;
        var submitted = false;
        try
        {
            for (var i = first; i <= last; i++)
            {
                if (_blocks[i].Handle != 0) continue;
                binds[count] = new SparseMemoryBind { ResourceOffset = (ulong)i * _blockSize, Size = _blockSize,
                    Memory = AllocateBlock() };
                count++;
            }
            if (count == 0) return;
            WaitPrevious();
            PrepareSync();
            RecordClear(binds[..count]);
            fixed (SparseMemoryBind* p = binds)
            {
                var buffer = new SparseBufferMemoryBindInfo { Buffer = Buffer.Handle, BindCount = (uint)count, PBinds = p };
                var prior = _prior;
                var bound = _bound;
                var before = new SubmitInfo { SType = StructureType.SubmitInfo, SignalSemaphoreCount = 1, PSignalSemaphores = &prior };
                var submit = new BindSparseInfo { SType = StructureType.BindSparseInfo, BufferBindCount = 1, PBufferBinds = &buffer,
                    WaitSemaphoreCount = 1, PWaitSemaphores = &prior, SignalSemaphoreCount = 1, PSignalSemaphores = &bound };
                var clear = _clear;
                var all = PipelineStageFlags.AllCommandsBit;
                var after = new SubmitInfo { SType = StructureType.SubmitInfo, WaitSemaphoreCount = 1, PWaitSemaphores = &bound,
                    PWaitDstStageMask = &all, CommandBufferCount = 1, PCommandBuffers = &clear };
                // Sparse bindings have no implicit ordering with command buffers on this queue. Bracket the
                // bind with semaphores so earlier shaders finish first and later work sees initialized entries.
                lock (_scheduler.QueueGate)
                {
                    Check(_device.Vk.QueueSubmit(_device.SparseQueue, 1, &before, default));
                    _incompleteSubmission = true;
                    Check(_device.Vk.QueueBindSparse(_device.SparseQueue, 1, &submit, default));
                    submitted = true;
                    Check(_device.Vk.QueueSubmit(_device.SparseQueue, 1, &after, _fence));
                    _pending = true;
                    _incompleteSubmission = false;
                }
            }
            for (var j = 0; j < count; j++)
            {
                var i = (int)(binds[j].ResourceOffset / _blockSize);
                _blocks[i] = binds[j].Memory;
            }
            count = 0;
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (submitted) _blocks[(int)(binds[i].ResourceOffset / _blockSize)] = binds[i].Memory;
                else _device.FreeMemory(binds[i].Memory);
            }
        }
    }

    private void PrepareSync()
    {
        if (_fence.Handle != 0) return;
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        Check(_device.Vk.CreateSemaphore(_device.Device, &info, null, out _prior));
        Check(_device.Vk.CreateSemaphore(_device.Device, &info, null, out _bound));
        var fence = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(_device.Vk.CreateFence(_device.Device, &fence, null, out _fence));
    }

    private void WaitPrevious()
    {
        if (!_pending) return;
        var fence = _fence;
        Check(_device.Vk.WaitForFences(_device.Device, 1, &fence, true, ulong.MaxValue));
        _pending = false;
        Check(_device.Vk.ResetFences(_device.Device, 1, &fence));
    }

    private DeviceMemory AllocateBlock()
    {
        var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = _blockSize, MemoryTypeIndex = _memoryType };
        Check(_device.AllocateMemory(info, out var memory, GpuResidency.Role.Buffer));
        return memory;
    }

    private void RecordClear(ReadOnlySpan<SparseMemoryBind> binds)
    {
        var vk = _device.Vk;
        if (_clearPool.Handle == 0)
        {
            var info = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.TransientBit, QueueFamilyIndex = _device.SparseQueueFamily };
            Check(vk.CreateCommandPool(_device.Device, &info, null, out _clearPool));
            var alloc = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _clearPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            Check(vk.AllocateCommandBuffers(_device.Device, &alloc, out _clear));
        }
        else Check(vk.ResetCommandPool(_device.Device, _clearPool, 0));
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(vk.BeginCommandBuffer(_clear, &begin));
        foreach (var bind in binds) vk.CmdFillBuffer(_clear, Buffer.Handle, bind.ResourceOffset, bind.Size, 0);
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit };
        vk.CmdPipelineBarrier(_clear, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
        Check(vk.EndCommandBuffer(_clear));
    }

    internal void Dispose()
    {
        if (_incompleteSubmission)
            lock (_scheduler.QueueGate) _device.Vk.QueueWaitIdle(_device.SparseQueue);
        WaitPrevious();
        if (_fence.Handle != 0) _device.Vk.DestroyFence(_device.Device, _fence, null);
        if (_prior.Handle != 0) _device.Vk.DestroySemaphore(_device.Device, _prior, null);
        if (_bound.Handle != 0) _device.Vk.DestroySemaphore(_device.Device, _bound, null);
        Buffer.Dispose();
        if (_clearPool.Handle != 0) _device.Vk.DestroyCommandPool(_device.Device, _clearPool, null);
        foreach (var memory in _blocks) if (memory.Handle != 0) _device.FreeMemory(memory);
    }

    private static void Check(Result result)
    {
        if (result != Result.Success) throw SubmissionScheduler.Fatal($"Vulkan sparse address table operation failed: {result}.");
    }
}

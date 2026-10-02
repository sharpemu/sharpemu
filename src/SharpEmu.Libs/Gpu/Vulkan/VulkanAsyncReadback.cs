// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Gpu.Vulkan;

public readonly record struct ReadbackPiece(GpuBuffer Source, ulong SourceOffset, ulong Size);

// A readback that waits only for the main-queue tick that last wrote its sources.
internal interface IBufferReadback : IDisposable
{
    void Read(ReadOnlySpan<ReadbackPiece> pieces, ulong waitTick, VulkanAsyncReadback.ReadbackConsumer consume);
}

// Copies GPU-written buffer ranges back to the host on a second queue. The copy waits
// on the main queue's timeline for the tick that last wrote the sources only, so a
// guest read of one GPU-produced value no longer waits behind every later draw that
// is still queued on the main queue (a readback there has to go to the queue's tail).
internal sealed unsafe class VulkanAsyncReadback : IBufferReadback
{
    private const ulong Alignment = 16;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly CommandBuffer _command;
    private readonly VkSemaphore _timeline;
    private readonly object _gate = new();
    private ulong _signaled;
    private GpuBuffer? _staging;

    public VulkanAsyncReadback(GpuDeviceInfo device, SubmissionScheduler scheduler, Queue queue, uint queueFamilyIndex)
    {
        _device = device;
        _scheduler = scheduler;
        _queue = queue;
        var vk = device.Vk;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Require(vk.CreateCommandPool(device.Device, &poolInfo, null, out _pool), "vkCreateCommandPool(readback)");
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer command;
        Require(vk.AllocateCommandBuffers(device.Device, &allocateInfo, &command), "vkAllocateCommandBuffers(readback)");
        _command = command;
        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
        };
        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &typeInfo };
        Require(vk.CreateSemaphore(device.Device, &semaphoreInfo, null, out _timeline), "vkCreateSemaphore(readback)");
    }

    // Copies the pieces once the main-queue tick has completed and hands each one's bytes
    // to consume, in order. The caller must have submitted waitTick already.
    public void Read(ReadOnlySpan<ReadbackPiece> pieces, ulong waitTick, ReadbackConsumer consume)
    {
        lock (_gate)
        {
            var total = 0UL;
            foreach (var piece in pieces)
            {
                total = AlignUp(total, Alignment) + piece.Size;
            }

            var staging = EnsureStaging(total);
            var vk = _device.Vk;
            Require(vk.ResetCommandBuffer(_command, 0), "vkResetCommandBuffer(readback)");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Require(vk.BeginCommandBuffer(_command, &beginInfo), "vkBeginCommandBuffer(readback)");
            var offset = 0UL;
            foreach (var piece in pieces)
            {
                offset = AlignUp(offset, Alignment);
                var region = new BufferCopy(piece.SourceOffset, offset, piece.Size);
                vk.CmdCopyBuffer(_command, piece.Source.Handle, staging.Handle, 1, &region);
                offset += piece.Size;
            }

            // Make the copied bytes available to the host once the queue signals.
            var toHost = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.CopyBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.HostBit,
                DstAccessMask = AccessFlags2.HostReadBit,
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &toHost,
            };
            vk.CmdPipelineBarrier2(_command, &dependency);
            Require(vk.EndCommandBuffer(_command), "vkEndCommandBuffer(readback)");

            var signalValue = ++_signaled;
            // The semaphore wait orders the copies after every write the main queue made
            // up to waitTick and makes those writes visible to them.
            var wait = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = new VkSemaphore(_scheduler.Timeline.Handle),
                Value = waitTick,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            var signal = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = _timeline,
                Value = signalValue,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            var commandInfo = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = _command,
                DeviceMask = 1,
            };
            var submit = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = waitTick == 0 ? 0u : 1u,
                PWaitSemaphoreInfos = &wait,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandInfo,
                SignalSemaphoreInfoCount = 1,
                PSignalSemaphoreInfos = &signal,
            };
            Require(vk.QueueSubmit2(_queue, 1, &submit, default), "vkQueueSubmit2(readback)");

            var semaphore = _timeline;
            var waitInfo = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &semaphore,
                PValues = &signalValue,
            };
            using (VideoOut.RenderPhaseProfile.MeasureDetail(VideoOut.RenderPhaseProfile.Phase.GpuCompletionWait))
            {
                Require(vk.WaitSemaphores(_device.Device, &waitInfo, ulong.MaxValue), "vkWaitSemaphores(readback)");
            }

            offset = 0;
            for (var index = 0; index < pieces.Length; index++)
            {
                offset = AlignUp(offset, Alignment);
                var size = pieces[index].Size;
                staging.Invalidate(offset, size);

                consume(index, staging.Mapped.Slice((int)offset, (int)size));
                offset += size;
            }
        }
    }

    public delegate void ReadbackConsumer(int index, ReadOnlySpan<byte> bytes);

    private GpuBuffer EnsureStaging(ulong size)
    {
        if (_staging is { } existing && existing.Size >= size)
        {
            return existing;
        }

        _staging?.Dispose();
        var capacity = Math.Max(size, 1UL << 20);
        _staging = new GpuBuffer(_device, _scheduler, GpuBufferUsage.Download, 0, BufferUsageFlags.TransferDstBit, capacity);
        return _staging;
    }

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    private static void Require(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed: {result}.");
        }
    }

    public void Dispose()
    {
        var vk = _device.Vk;
        lock (_gate)
        {
            if (_signaled != 0)
            {
                var semaphore = _timeline;
                var value = _signaled;
                var waitInfo = new SemaphoreWaitInfo
                {
                    SType = StructureType.SemaphoreWaitInfo,
                    SemaphoreCount = 1,
                    PSemaphores = &semaphore,
                    PValues = &value,
                };
                _ = vk.WaitSemaphores(_device.Device, &waitInfo, ulong.MaxValue);
            }

            _staging?.Dispose();
            _staging = null;
            vk.DestroySemaphore(_device.Device, _timeline, null);
            vk.DestroyCommandPool(_device.Device, _pool, null);
        }
    }
}

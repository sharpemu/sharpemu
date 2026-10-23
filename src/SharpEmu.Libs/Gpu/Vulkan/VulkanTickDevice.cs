// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Gpu.Vulkan;

// One transient command pool and one timeline semaphore on the presenter's queue.
internal sealed unsafe class VulkanTickDevice : IGpuTickDevice
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly VkSemaphore _timeline;

    public VulkanTickDevice(Vk vk, Device device, Queue queue, uint queueFamilyIndex, object queueGate, PhysicalDevice profilePhysicalDevice = default)
    {
        _vk = vk;
        _device = device;
        _queue = queue;
        QueueGate = queueGate;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        RequireSuccess(_vk.CreateCommandPool(_device, &poolInfo, null, out _pool), "vkCreateCommandPool(scheduler)");
        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var createInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &typeInfo,
        };
        RequireSuccess(_vk.CreateSemaphore(_device, &createInfo, null, out _timeline), "vkCreateSemaphore(scheduler timeline)");
        if (profilePhysicalDevice.Handle != 0)
            CommandProfile = new VulkanCommandProfile(vk, profilePhysicalDevice, device, queueFamilyIndex);
    }

    public VulkanCommandProfile? CommandProfile { get; }

    public object QueueGate { get; }

    public ulong TimelineHandle => _timeline.Handle;

    public ulong ReadTimeline()
    {
        ulong value;
        RequireSuccess(_vk.GetSemaphoreCounterValue(_device, _timeline, &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    public bool TryWaitTimeline(ulong tick, out string failure)
    {
        var target = tick;
        if (GpuWaitSpin.TrySpin(() => ReadTimeline() >= target))
        {
            failure = Result.Success.ToString();
            return true;
        }

        var semaphore = _timeline;
        var waitInfo = new SemaphoreWaitInfo
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &tick,
        };
        Result result;
        var waitStart = GpuWaitSiteProfile.Begin();
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GpuCompletionWait))
        {
            result = _vk.WaitSemaphores(_device, &waitInfo, ulong.MaxValue);
        }

        GpuWaitSiteProfile.End(waitStart);
        failure = result.ToString();
        return result == Result.Success;
    }

    public nint[] AllocateBuffers(int count)
    {
        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)count,
        };
        var buffers = new CommandBuffer[count];
        fixed (CommandBuffer* pointer = buffers)
        {
            RequireSuccess(_vk.AllocateCommandBuffers(_device, &allocateInfo, pointer), "vkAllocateCommandBuffers(scheduler)");
        }

        var handles = new nint[count];
        for (var i = 0; i < count; i++)
        {
            handles[i] = buffers[i].Handle;
        }

        return handles;
    }

    public void BeginBuffer(nint buffer)
    {
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        RequireSuccess(_vk.BeginCommandBuffer(new CommandBuffer(buffer), &beginInfo), "vkBeginCommandBuffer(scheduler)");
        CommandProfile?.BeginBuffer(new CommandBuffer(buffer));
    }

    public void EndBuffer(nint buffer)
    {
        CommandProfile?.WriteMarker(new CommandBuffer(buffer), VulkanCommandProfile.IntervalKind.Tail);
        RequireSuccess(_vk.EndCommandBuffer(new CommandBuffer(buffer)), "vkEndCommandBuffer(scheduler)");
    }

    public bool TrySubmit(nint buffer, SubmitBundle bundle, out string failure)
    {
        var commandBuffer = new CommandBuffer(buffer);
        fixed (ulong* waitSemaphores = bundle.WaitSemaphores)
        fixed (ulong* waitTicks = bundle.WaitTicks)
        fixed (uint* waitStages = bundle.WaitStages)
        fixed (ulong* signalSemaphores = bundle.SignalSemaphores)
        fixed (ulong* signalTicks = bundle.SignalTicks)
        {
            var waitInfos = stackalloc SemaphoreSubmitInfo[bundle.WaitCount];
            for (var index = 0; index < bundle.WaitCount; index++)
            {
                waitInfos[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(waitSemaphores[index]),
                    Value = waitTicks[index],
                    StageMask = (PipelineStageFlags2)waitStages[index],
                };
            }

            var signalInfos = stackalloc SemaphoreSubmitInfo[bundle.SignalCount];
            for (var index = 0; index < bundle.SignalCount; index++)
            {
                signalInfos[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = new VkSemaphore(signalSemaphores[index]),
                    Value = signalTicks[index],
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                };
            }

            var commandInfo = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = commandBuffer,
                DeviceMask = 1,
            };
            var submitInfo = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = (uint)bundle.WaitCount,
                PWaitSemaphoreInfos = waitInfos,
                CommandBufferInfoCount = 1,
                PCommandBufferInfos = &commandInfo,
                SignalSemaphoreInfoCount = (uint)bundle.SignalCount,
                PSignalSemaphoreInfos = signalInfos,
            };
            var result = _vk.QueueSubmit2(_queue, 1, &submitInfo, default);
            if (result == Result.Success)
                CommandProfile?.MarkSubmitted(buffer);
            failure = result.ToString();
            return result == Result.Success;
        }
    }

    public void Dispose()
    {
        CommandProfile?.Dispose();
        _vk.DestroySemaphore(_device, _timeline, null);
        _vk.DestroyCommandPool(_device, _pool, null);
    }

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}

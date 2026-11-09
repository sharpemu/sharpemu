// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed unsafe class VulkanCommandPool : IGpuCommandBufferSource, IDisposable
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly CommandPool _pool;
    private readonly VulkanCommandProfile? _profile;

    public VulkanCommandPool(Vk vk, Device device, uint queueFamilyIndex, VulkanCommandProfile? profile)
    {
        _vk = vk;
        _device = device;
        _profile = profile;
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        VulkanTickDevice.RequireSuccess(_vk.CreateCommandPool(_device, &poolInfo, null, out _pool), "vkCreateCommandPool(scheduler)");
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
            VulkanTickDevice.RequireSuccess(_vk.AllocateCommandBuffers(_device, &allocateInfo, pointer), "vkAllocateCommandBuffers(scheduler)");
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
        VulkanTickDevice.RequireSuccess(_vk.BeginCommandBuffer(new CommandBuffer(buffer), &beginInfo), "vkBeginCommandBuffer(scheduler)");
        _profile?.BeginBuffer(new CommandBuffer(buffer));
    }

    public void EndBuffer(nint buffer, bool hostRead)
    {
        var command = new CommandBuffer(buffer);
        _profile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Tail);
        if (hostRead)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.HostReadBit,
            };
            VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.HostBit, 0,
                1, &barrier, 0, null, 0, null, BarrierSource.HostRead);
        }

        VulkanTickDevice.RequireSuccess(_vk.EndCommandBuffer(command), "vkEndCommandBuffer(scheduler)");
    }

    public void Dispose() => _vk.DestroyCommandPool(_device, _pool, null);
}

// One transient command pool and one timeline semaphore on the presenter's queue.
internal sealed unsafe class VulkanTickDevice : IGpuTickDevice
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly uint _queueFamilyIndex;
    private readonly VulkanCommandPool _pool;
    private readonly List<VulkanCommandPool> _pools = new();
    private readonly VkSemaphore _timeline;

    public VulkanTickDevice(Vk vk, Device device, Queue queue, uint queueFamilyIndex, object queueGate, PhysicalDevice profilePhysicalDevice = default)
    {
        _vk = vk;
        _device = device;
        _queue = queue;
        _queueFamilyIndex = queueFamilyIndex;
        QueueGate = queueGate;
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
        _pool = new VulkanCommandPool(vk, device, queueFamilyIndex, CommandProfile);
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

    public nint[] AllocateBuffers(int count) => _pool.AllocateBuffers(count);

    public void BeginBuffer(nint buffer) => _pool.BeginBuffer(buffer);

    public void EndBuffer(nint buffer, bool hostRead) => _pool.EndBuffer(buffer, hostRead);

    public IGpuCommandBufferSource CreateBufferSource()
    {
        var pool = new VulkanCommandPool(_vk, _device, _queueFamilyIndex, null);
        lock (_pools)
        {
            _pools.Add(pool);
        }

        return pool;
    }

    public bool TrySubmit(ReadOnlySpan<nint> buffers, SubmitBundle bundle, out string failure)
    {
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

            var commandInfos = stackalloc CommandBufferSubmitInfo[buffers.Length];
            for (var index = 0; index < buffers.Length; index++)
            {
                commandInfos[index] = new CommandBufferSubmitInfo
                {
                    SType = StructureType.CommandBufferSubmitInfo,
                    CommandBuffer = new CommandBuffer(buffers[index]),
                    DeviceMask = 1,
                };
            }

            var submitInfo = new SubmitInfo2
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = (uint)bundle.WaitCount,
                PWaitSemaphoreInfos = waitInfos,
                CommandBufferInfoCount = (uint)buffers.Length,
                PCommandBufferInfos = commandInfos,
                SignalSemaphoreInfoCount = (uint)bundle.SignalCount,
                PSignalSemaphoreInfos = signalInfos,
            };
            if (Diagnostics.SubmitSiteTally.Enabled)
            {
                Diagnostics.SubmitSiteTally.Note();
            }

            var result = _vk.QueueSubmit2(_queue, 1, &submitInfo, default);
            if (result == Result.Success && CommandProfile is { } profile)
            {
                foreach (var buffer in buffers)
                {
                    profile.MarkSubmitted(buffer);
                }
            }

            failure = result.ToString();
            return result == Result.Success;
        }
    }

    public void Dispose()
    {
        CommandProfile?.Dispose();
        _vk.DestroySemaphore(_device, _timeline, null);
        _pool.Dispose();
        lock (_pools)
        {
            foreach (var pool in _pools)
            {
                pool.Dispose();
            }

            _pools.Clear();
        }
    }

    internal static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"{operation} failed with {result}");
        }
    }
}

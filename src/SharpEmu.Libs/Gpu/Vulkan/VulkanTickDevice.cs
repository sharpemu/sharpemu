// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
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
    private readonly bool _deviceFaultEnabled;
    private readonly delegate* unmanaged<Device, DeviceFaultCountsEXT*, DeviceFaultInfoEXT*, Result> _getDeviceFaultInfo;

    public VulkanTickDevice(
        Vk vk,
        Device device,
        Queue queue,
        uint queueFamilyIndex,
        object queueGate,
        PhysicalDevice profilePhysicalDevice = default,
        bool deviceFaultEnabled = false)
    {
        _vk = vk;
        _device = device;
        _queue = queue;
        QueueGate = queueGate;
        _deviceFaultEnabled = deviceFaultEnabled;
        if (deviceFaultEnabled)
        {
            var getDeviceFaultInfo = _vk.GetDeviceProcAddr(_device, "vkGetDeviceFaultInfoEXT");
            _getDeviceFaultInfo =
                (delegate* unmanaged<Device, DeviceFaultCountsEXT*, DeviceFaultInfoEXT*, Result>)getDeviceFaultInfo.Handle;
            if (_getDeviceFaultInfo == null)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] VK_EXT_device_fault was enabled but vkGetDeviceFaultInfoEXT is unavailable.");
            }
        }

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

    // MoltenVK records Metal command buffers with unretained references and signals the timeline
    // semaphore with a Metal event at the end of each one, which can fire before Metal has finished
    // with the command buffer. Destroying a resource once its tick signals is valid Vulkan but breaks
    // Metal's rule that unretained resources outlive the command buffer. On macOS each submission
    // also signals a fence, which MoltenVK signals from the command buffer's completion handler, and a
    // tick counts as complete to the host only once its fence has signaled too.
    // SHARPEMU_FENCE_RETIREMENT=0 reports the timeline value alone.
    private static readonly bool FenceRetirement = OperatingSystem.IsMacOS() &&
        Environment.GetEnvironmentVariable("SHARPEMU_FENCE_RETIREMENT") != "0";

    private readonly object _fenceGate = new();
    private sealed class InFlightFence(ulong tick, Fence fence)
    {
        public readonly ulong Tick = tick;
        public readonly Fence Fence = fence;
        // Host threads blocked on the fence; it is reset and pooled only once none are.
        public int Waiters;
    }

    private readonly Queue<InFlightFence> _inFlightFences = new();
    private readonly List<InFlightFence> _retiredWithWaiters = new();
    private readonly Stack<Fence> _freeFences = new();

    private Fence TakeFenceLocked()
    {
        if (_freeFences.TryPop(out var fence))
        {
            return fence;
        }

        var info = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        RequireSuccess(_vk.CreateFence(_device, &info, null, out fence), "vkCreateFence(scheduler retirement)");
        return fence;
    }

    private void RecycleLocked(InFlightFence entry)
    {
        var fence = entry.Fence;
        RequireSuccess(_vk.ResetFences(_device, 1, &fence), "vkResetFences(scheduler retirement)");
        _freeFences.Push(fence);
    }

    // Retires the leading submissions whose fences signaled; returns the first tick still in flight.
    private ulong RetireSignaledFencesLocked()
    {
        for (var index = _retiredWithWaiters.Count - 1; index >= 0; index--)
        {
            if (_retiredWithWaiters[index].Waiters == 0)
            {
                RecycleLocked(_retiredWithWaiters[index]);
                _retiredWithWaiters.RemoveAt(index);
            }
        }

        while (_inFlightFences.TryPeek(out var entry))
        {
            if (_vk.GetFenceStatus(_device, entry.Fence) != Result.Success)
            {
                return entry.Tick;
            }

            _inFlightFences.Dequeue();
            if (entry.Waiters == 0)
            {
                RecycleLocked(entry);
            }
            else
            {
                _retiredWithWaiters.Add(entry);
            }
        }

        return ulong.MaxValue;
    }

    // The timeline value the host may treat as complete.
    private ulong RetiredValue(ulong timelineValue)
    {
        if (!FenceRetirement)
        {
            return timelineValue;
        }

        lock (_fenceGate)
        {
            // Below the oldest in-flight tick the timeline value is already retired; poll fences only
            // for ticks the timeline reports done.
            if (!_inFlightFences.TryPeek(out var oldest) || timelineValue < oldest.Tick)
            {
                return timelineValue;
            }

            var firstInFlight = RetireSignaledFencesLocked();
            return firstInFlight == ulong.MaxValue ? timelineValue : Math.Min(timelineValue, firstInFlight - 1);
        }
    }

    // Waits until every submission up to the tick has retired. The waited fence is counted so no
    // other thread resets and reuses it while this one blocks on it.
    private bool WaitRetired(ulong tick)
    {
        if (!FenceRetirement)
        {
            return true;
        }

        InFlightFence? waited = null;
        lock (_fenceGate)
        {
            if (RetireSignaledFencesLocked() > tick)
            {
                return true;
            }

            foreach (var entry in _inFlightFences)
            {
                if (entry.Tick > tick)
                {
                    break;
                }

                waited = entry;
            }

            if (waited is null)
            {
                return true;
            }

            waited.Waiters++;
        }

        var fence = waited.Fence;
        var result = _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);
        lock (_fenceGate)
        {
            waited.Waiters--;
            _ = RetireSignaledFencesLocked();
        }

        return result == Result.Success;
    }

    public object QueueGate { get; }

    public ulong TimelineHandle => _timeline.Handle;

    public ulong ReadTimeline()
    {
        ulong value;
        RequireSuccess(_vk.GetSemaphoreCounterValue(_device, _timeline, &value), "vkGetSemaphoreCounterValue");
        return RetiredValue(value);
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
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GpuCompletionWait))
        {
            result = _vk.WaitSemaphores(_device, &waitInfo, ulong.MaxValue);
        }
        failure = result == Result.ErrorDeviceLost
            ? result + ReadDeviceFaultDetails()
            : result.ToString();
        return result == Result.Success && WaitRetired(tick);
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
            Fence fence = default;
            ulong fenceTick = 0;
            if (FenceRetirement && bundle.SignalCount != 0)
            {
                for (var index = 0; index < bundle.SignalCount; index++)
                {
                    if (signalSemaphores[index] == _timeline.Handle)
                    {
                        fenceTick = Math.Max(fenceTick, signalTicks[index]);
                    }
                }

                if (fenceTick != 0)
                {
                    // Tracked before the submit so no reader sees the tick signal without its fence.
                    lock (_fenceGate)
                    {
                        fence = TakeFenceLocked();
                        _inFlightFences.Enqueue(new InFlightFence(fenceTick, fence));
                    }
                }
            }

            var result = _vk.QueueSubmit2(_queue, 1, &submitInfo, fence);
            if (fence.Handle != 0 && result != Result.Success)
            {
                lock (_fenceGate)
                {
                    // Submissions are serialized by the queue gate, so the failed one is the newest.
                    var kept = _inFlightFences.Where(entry => entry.Fence.Handle != fence.Handle).ToArray();
                    _inFlightFences.Clear();
                    foreach (var entry in kept)
                    {
                        _inFlightFences.Enqueue(entry);
                    }

                    _freeFences.Push(fence);
                }
            }

            if (result == Result.Success)
                CommandProfile?.MarkSubmitted(buffer);
            failure = result == Result.ErrorDeviceLost
                ? result + ReadDeviceFaultDetails()
                : result.ToString();
            return result == Result.Success;
        }
    }

    private string ReadDeviceFaultDetails()
    {
        if (!_deviceFaultEnabled)
        {
            return string.Empty;
        }

        if (_getDeviceFaultInfo == null)
        {
            return "; device_fault=proc_unavailable";
        }

        try
        {
            var counts = new DeviceFaultCountsEXT
            {
                SType = StructureType.DeviceFaultCountsExt,
            };
            var countResult = _getDeviceFaultInfo(_device, &counts, null);
            if (countResult != Result.Success)
            {
                return $"; device_fault_counts={countResult}";
            }

            const uint maxRecords = 256;
            var reportedAddressCount = counts.AddressInfoCount;
            var reportedVendorCount = counts.VendorInfoCount;
            var reportedVendorBinarySize = counts.VendorBinarySize;
            var addresses = new DeviceFaultAddressInfoEXT[checked((int)Math.Min(reportedAddressCount, maxRecords))];
            var vendors = new DeviceFaultVendorInfoEXT[checked((int)Math.Min(reportedVendorCount, maxRecords))];
            fixed (DeviceFaultAddressInfoEXT* addressPointer = addresses)
            fixed (DeviceFaultVendorInfoEXT* vendorPointer = vendors)
            {
                counts.AddressInfoCount = (uint)addresses.Length;
                counts.VendorInfoCount = (uint)vendors.Length;
                counts.VendorBinarySize = 0;
                var info = new DeviceFaultInfoEXT
                {
                    SType = StructureType.DeviceFaultInfoExt,
                    PAddressInfos = addressPointer,
                    PVendorInfos = vendorPointer,
                    PVendorBinaryData = null,
                };
                var infoResult = _getDeviceFaultInfo(_device, &counts, &info);
                var hasDetails = infoResult is Result.Success or Result.Incomplete;
                var addressCount = hasDetails
                    ? (int)Math.Min((uint)addresses.Length, counts.AddressInfoCount)
                    : 0;
                var vendorCount = hasDetails
                    ? (int)Math.Min((uint)vendors.Length, counts.VendorInfoCount)
                    : 0;
                var details = new StringBuilder(192);
                details.Append("; device_fault=").Append(infoResult)
                    .Append(" addresses=").Append(addressCount).Append('/').Append(reportedAddressCount)
                    .Append(" vendors=").Append(vendorCount).Append('/').Append(reportedVendorCount)
                    .Append(" vendor_binary=").Append(reportedVendorBinarySize);
                for (var index = 0; index < addressCount; index++)
                {
                    var address = addresses[index];
                    details.Append(" address[").Append(index).Append("]=")
                        .Append(address.AddressType)
                        .Append("@0x").Append(address.ReportedAddress.ToString("X16"))
                        .Append("+/-0x").Append(address.AddressPrecision.ToString("X"));
                }

                for (var index = 0; index < vendorCount; index++)
                {
                    var vendor = vendors[index];
                    details.Append(" vendor[").Append(index).Append("]=code:0x")
                        .Append(vendor.VendorFaultCode.ToString("X"))
                        .Append(",data:0x").Append(vendor.VendorFaultData.ToString("X"));
                }

                return details.ToString();
            }
        }
        catch (Exception exception)
        {
            return $"; device_fault_query_exception={exception.GetType().Name}";
        }
    }

    public void Dispose()
    {
        CommandProfile?.Dispose();
        lock (_fenceGate)
        {
            foreach (var entry in _inFlightFences.Concat(_retiredWithWaiters))
            {
                var fence = entry.Fence;
                _ = _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);
                _vk.DestroyFence(_device, fence, null);
            }

            _inFlightFences.Clear();
            _retiredWithWaiters.Clear();
            while (_freeFences.TryPop(out var fence))
            {
                _vk.DestroyFence(_device, fence, null);
            }
        }

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

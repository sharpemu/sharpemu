// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    // A same-queue wait is only safe when its signal was submitted earlier on this queue.
    internal static void CheckLabelWaitOrder(ulong waitValue, ulong lastSignalledValue)
    {
        if (waitValue > lastSignalledValue)
        {
            throw SubmissionScheduler.Fatal(
                $"label wait {waitValue} precedes its signal (last signalled {lastSignalledValue})");
        }
    }

    private sealed partial class Presenter : IRenderingState
    {
        // This partial owns the submission scheduler and the GPU worker relay.

        private readonly object _queueGate = new();
        private readonly GpuWorkerRelay _relay = new(WakeRenderThread, WaitForAcceptedGuestWork);
        private readonly SubmissionContext _submissionContext = new();
        private SubmissionScheduler _scheduler = null!;
        private GuestBufferCache _bufferCache = null!;

        bool IRenderingState.IsRendering => _openPassTarget is not null;

        void IRenderingState.EndRendering() => CloseOpenTranslatedRenderPass();

        private static void WakeRenderThread()
        {
            lock (_gate)
            {
                System.Threading.Monitor.PulseAll(_gate);
            }
        }

        private void CreateScheduler() =>
            _scheduler = new SubmissionScheduler(
                new VulkanTickDevice(_vk, _device, _queue, _queueFamilyIndex, _queueGate),
                this,
                PrepareGuestSubmission,
                CompleteGuestSubmission);

        // The store shares the manager's page guard and reads guest memory through its address space.
        private void CreateBufferCache()
        {
            if (GuestGpuMemoryHook.Current is not { AddressSpace: ICpuMemory guest and IGuestBackedSpace backing } memory)
            {
                throw SubmissionScheduler.Fatal("the buffer store needs the guest GPU memory manager over backed virtual memory");
            }

            _bufferCache = new GuestBufferCache(
                new GpuDeviceInfo(_vk, _physicalDevice, _device), _scheduler, _relay, memory.Pages, guest, backing);
            _bufferCache.StreamOffsetAlignment = Math.Max(_bufferCache.StreamOffsetAlignment, GuestStorageBufferOffsetAlignment);
        }

        private void AttachGuestGpuMemory()
        {
            GuestGpuMemoryHook.Current?.AttachStores(_bufferCache, null);
            GuestGpuMemoryHook.Current?.AttachGpuQueue(_relay, _scheduler);
        }

        // Close admission, run accepted commands, drain the store, finish GPU work, shut down, detach.
        private void ShutdownScheduler()
        {
            try
            {
                _relay.StopAcceptingWork();
                _relay.RunPendingCommands();
                try
                {
                    _bufferCache.Shutdown();
                }
                finally
                {
                    GuestGpuMemoryHook.Current?.AttachStores(null, null);
                }

                if (_scheduler.Active)
                {
                    _scheduler.Finish();
                    _scheduler.WaitForAllPriorityOperations();
                }

                _scheduler.Shutdown();
            }
            finally
            {
                // An unmap waiting for the relay to go away must not outlive a failed shutdown.
                GuestGpuMemoryHook.Current?.AttachGpuQueue(null, null);
            }
        }

        private CommandBuffer CurrentRecordingBuffer()
        {
            if (!_scheduler.Active)
            {
                _scheduler.Begin(_submissionContext);
            }

            return new CommandBuffer(_scheduler.Current.Handle);
        }

        private void BindSubmissionContext(VulkanGuestQueueIdentity queue)
        {
            _submissionContext.QueueName = queue.Name;
            _submissionContext.SubmissionId = queue.SubmissionId;
        }

        private ulong SubmitPresentation(
            VkSemaphore imageAvailable,
            PipelineStageFlags waitStage,
            VkSemaphore renderFinished)
        {
            var bundle = new SubmitBundle();
            bundle.AddWait(imageAvailable.Handle, 1, (uint)waitStage);
            bundle.AddSignal(renderFinished.Handle, 1);
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit))
            {
                return _scheduler.Flush(bundle);
            }
        }
    }
}

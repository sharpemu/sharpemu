// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE.GpuMemory;
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
        private readonly GpuWorkerRelay _relay = new(WakeRenderThread);
        private readonly SubmissionContext _submissionContext = new();
        private SubmissionScheduler _scheduler = null!;

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

        private void AttachGuestGpuMemory() =>
            GuestGpuMemoryHook.Current?.AttachGpuQueue(_relay, _scheduler);

        // Close admission, run accepted commands, finish GPU work, shut down, then detach.
        private void ShutdownScheduler()
        {
            _relay.CloseAdmission();
            _relay.RunPending();
            if (_scheduler.Active)
            {
                _scheduler.Finish();
                _scheduler.DrainPriorityOperations();
            }

            _scheduler.Shutdown();
            GuestGpuMemoryHook.Current?.AttachGpuQueue(null, null);
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

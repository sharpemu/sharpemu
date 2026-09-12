// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter : IRenderingState
    {
        // This partial owns the submission scheduler and the GPU worker relay.

        private readonly object _queueGate = new();
        private readonly GpuWorkerRelay _relay = new(WakeRenderThread);

        internal GpuWorkerRelay Relay => _relay;
        private readonly SubmissionContext _submissionContext = new();
        private SubmissionScheduler _scheduler = null!;
        private VulkanCommandProfile? _gpuCommandProfile;
        private GpuDeviceInfo _deviceInfo = null!;
        private GuestBufferCache _bufferCache = null!;
        private GuestImageCache _imageCache = null!;
        private SamplerStore _samplerStore = null!;

        bool IRenderingState.IsRendering => _renderingActive;

        void IRenderingState.EndRendering()
        {
            EndRendering();
        }

        internal static void WakeRenderThread()
        {
            lock (_gate)
            {
                System.Threading.Monitor.PulseAll(_gate);
            }
        }

        private void CreateScheduler()
        {
            var tickDevice = new VulkanTickDevice(_vk, _device, _queue, _queueFamilyIndex, _queueGate,
                RenderPhaseProfile.Enabled ? _physicalDevice : default);
            _gpuCommandProfile = tickDevice.CommandProfile;
            _scheduler = new SubmissionScheduler(
                tickDevice,
                this,
                PrepareGuestSubmission,
                CompleteGuestSubmission,
                _ => WakeRenderThread());
        }

        // Both stores share the manager's page guard and read guest memory through its address space.
        private static (GuestGpuMemory Memory, ICpuMemory Guest, IGuestBackedSpace Backing) RequireGuestMemory(string store)
        {
            if (GuestGpuMemoryHook.Current is not { AddressSpace: ICpuMemory guest and IGuestBackedSpace backing } memory)
            {
                throw SubmissionScheduler.Fatal($"The {store} requires the guest GPU memory manager with backed virtual memory.");
            }

            return (memory, guest, backing);
        }

        private void CreateBufferCache()
        {
            var (memory, guest, backing) = RequireGuestMemory("buffer store");
            _guestBacking = backing;
            _bufferCache = new GuestBufferCache(_deviceInfo, _scheduler, _relay, memory.Pages, guest, backing);
            _bufferCache.StreamOffsetAlignment = Math.Max(_bufferCache.StreamOffsetAlignment, GuestStorageBufferOffsetAlignment);
        }

        // The image store follows the buffer store; readback of linear images stays off.
        private void CreateImageCache()
        {
            var (memory, _, backing) = RequireGuestMemory("image store");
            _imageCache = new GuestImageCache(_deviceInfo, _scheduler, memory.Pages, _bufferCache, backing, readbackLinearImages: false);
            _bufferCache.ImageCache = _imageCache;
            _samplerStore = new SamplerStore(_deviceInfo);
        }

        private void AttachGuestGpuMemory()
        {
            GuestGpuMemoryHook.Current?.AttachStores(_bufferCache, _imageCache);
            GuestGpuMemoryHook.Current?.AttachGpuQueue(_relay, _scheduler);
        }

        // Close admission, run accepted commands, drain the stores (images first), finish GPU work, shut down, detach.
        private void ShutdownScheduler()
        {
            try
            {
                // Drain accepted submissions before closing the relay.
                // Cancel blocked submissions if a full retry cycle makes no progress.
                _commandStream.StopAccepting();
                var outcome = _commandStream.DrainForShutdown(cancelBlockedOnNoProgress: true);
                FlushBatchedGuestCommands();
                Console.Error.WriteLine(
                    $"[LOADER][PERF] command_stream submissions={_commandStream.SubmissionsStarted} " +
                    $"slices={_commandStream.SlicesRun} blocked_retries={_commandStream.BlockedRetries} " +
                    $"outcome={outcome} fatal=0");
                _relay.StopAcceptingWork();
                _relay.RunPendingCommands();
                try
                {
                    _imageCache.Shutdown();
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
                VideoOutExports.CancelOutstandingFlips();
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

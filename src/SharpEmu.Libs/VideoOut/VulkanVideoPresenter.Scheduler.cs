// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter : IRenderingState
    {
        // This partial owns the submission scheduler and the GPU worker relay.

        private readonly object _queueGate = new();
        private readonly GpuWorkerRelay _relay;

        internal GpuWorkerRelay Relay => _relay;
        private readonly SubmissionContext _submissionContext = new();
        internal SubmissionScheduler _scheduler = null!;
        internal VulkanCommandProfile? _gpuCommandProfile;
        internal VulkanOcclusionQueries? _occlusionQueries;
        internal bool _supportsPreciseOcclusion;
        internal int _occlusionQueueId;
        internal GpuDeviceInfo _deviceInfo = null!;
        internal GuestBufferCache _bufferCache = null!;
        internal GuestImageCache _imageCache = null!;
        internal SamplerStore _samplerStore = null!;

        bool IRenderingState.IsRendering => _recording?._renderingActive ?? false;

        void IRenderingState.EndRendering() => _recording?.EndRendering();

        bool IRenderingState.TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers) =>
            _recording?.TryDeferUntilRenderingEnds(sourceStages, destinationStages, barriers) ?? false;

        internal static void WakeRenderThread()
        {
            SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.WakeRequested);
            lock (_gate)
            {
                SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.WakeSignaled);
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
                PrepareSubmission,
                CompleteGuestSubmission,
                _ => WakeRenderThread());
        }

        private void PrepareSubmission(SubmitBundle bundle)
        {
            _bufferCache?.ApplyPendingGpuProtections();
            PrepareGuestSubmission(bundle);
        }

        internal void RecordRequestedMemoryBarrier(nint handle, MemoryBarrierRequest request)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = VulkanSynchronization.Access(request.SourceAccess),
                DstAccessMask = VulkanSynchronization.Access(request.DestinationAccess),
            };
            VulkanSynchronization.PipelineBarrier(_vk, new CommandBuffer(handle), request.SourceStages, request.DestinationStages,
                0, 1, &barrier, 0, null, 0, null, BarrierSource.Requested);
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
            _bufferCache.GpuMemory = memory;
            _bufferCache.CompleteGuestPublications = (address, size, access) => CompleteOverlappingPublications(address, size,
                includeLabels: access == GuestBufferCache.PublicationAccess.Write,
                skipGpuSlots: access == GuestBufferCache.PublicationAccess.OverlaidRead);
            _bufferCache.OverlayGuestPublications = OverlayPendingDumps;
            _bufferCache.PublicationOrdinal = () => _guestPublications.Ordinal;
            _bufferCache.StreamOffsetAlignment = Math.Max(_bufferCache.StreamOffsetAlignment, GuestStorageBufferOffsetAlignment);
            _bufferCache.DefersGpuWriteProtection = true;
            if (_readbackQueueFamilyIndex is { } readbackFamily)
            {
                _bufferCache.AsyncReadback = new Gpu.Vulkan.VulkanAsyncReadback(_deviceInfo, _scheduler, _readbackQueue, readbackFamily);
            }
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
                // The command stream thread ends after its slice; the rest drains here.
                _commandThread?.Stop(() => RunCommandStreamSlices(long.MaxValue));
                if (_translation is not null)
                {
                    _translation.Sink = null;
                }
                var outcome = _commandStream.DrainForShutdown(cancelBlockedOnNoProgress: true);
                FlushBatchedGuestCommands();
                StopRenderWorkers();
                Console.Error.WriteLine(
                    $"[LOADER][PERF] command_stream submissions={_commandStream.SubmissionsStarted} " +
                    $"slices={_commandStream.SlicesRun} blocked_retries={_commandStream.BlockedRetries} " +
                    $"outcome={outcome} fatal=0");
                SharpEmu.Libs.Gpu.Pipelines.ImageDescriptorTrace.WriteHistory();
                Console.Error.WriteLine($"[LOADER][PERF] {ShaderCacheCounters.Summary()}");
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

        internal CommandBuffer CurrentRecordingBuffer()
        {
            if (!_scheduler.Active)
            {
                _scheduler.Begin(_submissionContext);
            }

            return new CommandBuffer(_scheduler.Current.Handle);
        }

        internal RecordingBuffer CurrentRecording()
        {
            if (!_scheduler.Active)
            {
                _scheduler.Begin(_submissionContext);
            }

            return _scheduler.Current;
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

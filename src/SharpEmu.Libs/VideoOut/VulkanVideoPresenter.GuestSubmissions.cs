// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial owns guest GPU submission execution and lifetime.

        private const int MaxInFlightGuestSubmissions = 8;
        // Scheduler ticks: the last submitted tick and the highest tick known retired.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private IReadOnlyList<TranslatedDrawResources>? _batchReferencedResources;
        private readonly Queue<(TranslatedDrawResources Resources, ulong RetireTimeline)>
            _deferredResourceDestroys = new();
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVersionDestroys = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();

        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;

        private sealed record PendingGuestSubmission(
            ulong Tick,
            IReadOnlyList<TranslatedDrawResources> Resources,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers);

        // Translated draws are recorded into the scheduler's current buffer and
        // submitted once per drained work batch (one vkQueueSubmit per batch).
        private bool _batchOpen;
        private int _batchDrawCount;
        private readonly List<TranslatedDrawResources> _batchResources = new();


        private CommandBuffer BeginBatchedGuestCommands()
        {
            var commandBuffer = CurrentRecordingBuffer();
            if (!_batchOpen)
            {
                _batchOpen = true;
                _batchDrawCount = 0;
            }

            return commandBuffer;
        }

        // Submits the current recording buffer with everything the batch lists own.
        private void FlushBatchedGuestCommands(
            IReadOnlyList<TranslatedDrawResources>? referencedResources = null)
        {
            if (!_batchOpen)
            {
                return;
            }

            _batchReferencedResources = referencedResources;
            try
            {
                using var profile = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit);
                _scheduler.Flush();
            }
            finally
            {
                _batchReferencedResources = null;
            }
        }

        private void PrepareGuestSubmission(SubmitBundle bundle)
        {
            _ = bundle;
            EndRendering();
            if (!_batchOpen)
            {
                return;
            }

            _lastSubmitDebugName = _batchResources.Count > 0
                ? $"batch={_batchResources[0].DebugName}"
                : string.Empty;
        }

        private void CompleteGuestSubmission(ulong tick)
        {
            _submitTimeline = tick;
            if (!_batchOpen)
            {
                return;
            }

            _batchOpen = false;
            var resources = _batchResources.ToArray();
            var retireBuffers = _batchRetireBuffers.Count > 0 ? _batchRetireBuffers.ToArray() : [];
            _batchResources.Clear();
            _batchRetireBuffers.Clear();

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    tick,
                    resources,
                    retireBuffers));
        }

        private void EnsureGuestSubmissionCapacity()
        {
            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void WaitForAllGuestSubmissions()
        {
            while (_pendingGuestSubmissions.Count != 0)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void CollectCompletedGuestSubmissions(bool waitForOldest)
        {
            if (waitForOldest && _pendingGuestSubmissions.TryPeek(out var oldest))
            {
                _scheduler.Wait(oldest.Tick);
            }

            while (_pendingGuestSubmissions.TryPeek(out var submission) &&
                   _scheduler.IsTickComplete(submission.Tick))
            {
                _pendingGuestSubmissions.Dequeue();
                RetireGuestSubmission(submission);
            }

            _completedTimeline = Math.Max(_completedTimeline, _scheduler.Timeline.CompletedTick);
            // Deferred tick work (buffer erases, one-shot uploads, fault parses) runs here.
            if (_scheduler.Active)
            {
                _scheduler.RunCompletedOperations();
            }

            _commandStream.NotifyCompletedGpuTick(_scheduler.Timeline.CompletedTick);

            ProcessDeferredTextureDestroys();
        }

        private void RetireGuestSubmission(PendingGuestSubmission submission)
        {
            if (submission.Tick > _completedTimeline)
            {
                _completedTimeline = submission.Tick;
            }

            foreach (var resources in submission.Resources)
            {
                DestroyTranslatedDrawResources(resources);
            }

            foreach (var (buffer, memory) in submission.RetireBuffers)
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _deviceInfo.FreeMemory(memory);
            }
        }

    }
}

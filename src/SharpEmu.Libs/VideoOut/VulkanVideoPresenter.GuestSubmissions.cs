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

        // Batched submissions each carry up to a frame's worth of work, so a cap of
        // 8 made draw preparation wait on the GPU once per frame; at 32 a title that
        // submits a few hundred times a frame still waited about twice a frame, so
        // the cap is 64. SHARPEMU_MAX_INFLIGHT_SUBMISSIONS overrides it.
        private static readonly int MaxInFlightGuestSubmissions =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_MAX_INFLIGHT_SUBMISSIONS"), out var inFlight) && inFlight > 0
                ? inFlight
                : 64;
        // Scheduler ticks: the last submitted tick and the highest tick known retired.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVersionDestroys = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();

        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;

        private sealed record PendingGuestSubmission(
            ulong Tick,
            IReadOnlyList<SubmissionUploadResources> Resources,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers);

        // Translated draws are recorded into the scheduler's current buffer and
        // submitted once per drained work batch (one vkQueueSubmit per batch).
        internal bool _batchOpen;
        internal const int MinimumFlushDraws = 32;
        private static readonly long BatchGraceTicks = System.Diagnostics.Stopwatch.Frequency * 3 / 1000;
        internal int _batchDrawCount;
        internal long _batchOpenedAt;

        private bool ProducerActive => _commandThread is { IsAlive: true } thread && (thread.HasWork || _commandStream.HasPending);

        private bool BatchShouldFlush =>
            _batchDrawCount >= MinimumFlushDraws ||
            _commandThread is null || _window.IsMinimized || _commandStream.BlockedQueueCount != 0 ||
            (_pendingGuestSubmissions.Count == 0 &&
             (!ProducerActive || System.Diagnostics.Stopwatch.GetTimestamp() - _batchOpenedAt >= BatchGraceTicks));
        internal readonly List<SubmissionUploadResources> _batchResources = new();
        private const int MaxSpareSubmissionResources = 4096;
        internal Stack<SubmissionUploadResources>? _spareSubmissionResources;


        internal CommandBuffer BeginBatchedGuestCommands()
        {
            var commandBuffer = CurrentRecordingBuffer();
            OpenGuestBatch();
            return commandBuffer;
        }

        // Opens the batch without asking for the native handle, so a deferred barrier stays deferred
        // until a command is actually recorded.
        internal void EnsureBatchedGuestCommands()
        {
            _ = CurrentRecording();
            OpenGuestBatch();
        }

        internal CommandBuffer BeginBatchedGuestStateCommands()
        {
            var recording = CurrentRecording();
            OpenGuestBatch();
            return new CommandBuffer(recording.StateHandle);
        }

        internal CommandBuffer BeginBatchedGuestTransferCommands()
        {
            var recording = CurrentRecording();
            OpenGuestBatch();
            return new CommandBuffer(recording.TransferHandle);
        }

        internal void RequestGuestMemoryBarrier(in MemoryBarrierRequest request)
        {
            var recording = CurrentRecording();
            OpenGuestBatch();
            _scheduler.MemoryBarrierRecorder ??= RecordRequestedMemoryBarrier;
            recording.RequestMemoryBarrier(request);
        }

        internal void OpenGuestBatch()
        {
            if (!_batchOpen)
            {
                _batchOpen = true;
                _batchDrawCount = 0;
            }
        }

        // Submits the current recording buffer with everything the batch lists own.
        // Returns the timeline tick that contains the commands this call submitted, so a
        // caller that must wait for its own recording does not read the shared
        // _submitTimeline field, which the presenter thread also writes.
        internal ulong FlushBatchedGuestCommands()
        {
            if (!_batchOpen)
            {
                return _submitTimeline;
            }

            using var profile = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit);
            var tick = _scheduler.Flush();
            _bufferCache?.OnBatchSubmitted();
            return tick;
        }

        private void PrepareGuestSubmission(SubmitBundle bundle)
        {
            _ = bundle;
            _recording?.EndRendering();
            _occlusionQueries?.Submit(_scheduler);
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

        // Collecting completed submissions polls the GPU timeline twice and may wait for the priority
        // worker. Every draw used to pay that; completed work only frees resources and runs deferred
        // callbacks, so a collection a few hundred microseconds later changes nothing but how long
        // those resources stay reserved. A full in-flight queue still collects, and waits, at once.
        private static readonly long CollectIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 4000;
        private long _lastCollectTicks;

        internal void EnsureGuestSubmissionCapacity(bool collectNow = false)
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.SubmissionCapacity);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!collectNow && _pendingGuestSubmissions.Count < MaxInFlightGuestSubmissions && now - _lastCollectTicks < CollectIntervalTicks)
            {
                return;
            }

            _lastCollectTicks = now;
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
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CompletedSubmissionCleanup);
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
            PublishLodStatistics();

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
                RecycleSubmissionUploads(resources);
                resources.FeedbackSnapshots = [];
                resources.OverflowBuffers = null;
                var spare = _spareSubmissionResources ??= new Stack<SubmissionUploadResources>();
                if (spare.Count < MaxSpareSubmissionResources)
                {
                    spare.Push(resources);
                }
            }

            foreach (var (buffer, memory) in submission.RetireBuffers)
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _deviceInfo.FreeMemory(memory);
            }
        }

    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial owns guest GPU submission execution and lifetime.

        private const int MaxInFlightGuestSubmissions = 8;
        private bool _gpuLabelTimelineEnabled;
        private VkSemaphore _graphicsGuestTimelineSemaphore;
        private ulong _graphicsGuestTimelineValue;
        private long _gpuLabelTimelineSignalCount;
        private long _gpuLabelTimelineWaitCount;
        private readonly Dictionary<string, GuestGpuLabelDependency>
            _lastSubmittedGpuLabelDependencyByGuestQueue = new(StringComparer.Ordinal);
        private readonly GpuLabelHostPublicationQueue _gpuLabelHostPublications = new();
        // Scheduler ticks: the last submitted tick and the highest tick known retired.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private GuestGpuLabelDependency _batchLabelDependency;
        private IReadOnlyList<TranslatedDrawResources>? _batchReferencedResources;
        private readonly Queue<(TranslatedDrawResources Resources, ulong RetireTimeline)>
            _deferredResourceDestroys = new();
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVersionDestroys = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();

        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        private readonly Dictionary<string, ulong> _lastSubmittedTimelineByGuestQueue =
            new(StringComparer.Ordinal);
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;
        private long _activeGuestWorkSequence;

        private sealed record PendingGuestSubmission(
            ulong Tick,
            IReadOnlyList<TranslatedDrawResources> Resources,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers,
            GuestGpuLabelDependency LabelDependency,
            string DebugName,
            VulkanGuestQueueIdentity Queue,
            long WorkSequence);

        private void PrepareGuestSubmissionCompletion(VulkanOrderedGuestAction work)
        {
            if (!work.CollectionPending)
            {
                return;
            }

            // Collect once at the submission boundary, before its final batch is flushed.
            RunGuestCacheCollection();
            work.CollectionPending = false;
        }

        private bool TryExecuteOrderedGuestAction(VulkanOrderedGuestAction work)
        {
            PrepareGuestSubmissionCompletion(work);
            var visible = TryMakeActiveGuestQueueSubmissionsCpuVisible();
            if (!visible)
            {
                RenderPhaseProfile.RecordOrderedAction(work.DebugName, completed: false);
                return false;
            }

            work.Action();
            RenderPhaseProfile.RecordOrderedAction(work.DebugName, completed: true);
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.ordered_action queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"work_sequence={_activeGuestWorkSequence} name='{work.DebugName}'");
            }

            return true;
        }

        private void ExecuteGuestCacheOperation(VulkanGuestCacheOperation work)
        {
            CloseOpenTranslatedRenderPass();
            var commandBuffer = BeginBatchedGuestCommands();
            var resources = CollectGuestCacheResourceRanges();
            foreach (var operation in work.Operations)
            {
                if (TryRecordResourceGuestCacheBarrier(
                        commandBuffer,
                        operation,
                        resources))
                {
                    continue;
                }

                RecordGlobalGuestCacheBarrier(commandBuffer, operation);
            }

            work.ApplyHostState();
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.guest_cache_operation queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"work_sequence={_activeGuestWorkSequence} " +
                    $"count={work.Operations.Count} " +
                    $"name='{work.DebugName}'");
            }
        }

        private void ExecuteGpuLabelSignal(VulkanGpuLabelSignal work)
        {
            FlushBatchedGuestCommands();
            _lastSubmittedGpuLabelDependencyByGuestQueue.TryGetValue(
                _activeGuestQueue.Name,
                out var dependency);
            _gpuLabelTimelineSignalCount++;
            if (_gpuLabelTimelineSignalCount <= 8 ||
                (_gpuLabelTimelineSignalCount & (_gpuLabelTimelineSignalCount - 1)) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.gpu_label_signal " +
                    $"count={_gpuLabelTimelineSignalCount} " +
                    $"queue={_activeGuestQueue.Name} " +
                    $"dependency=g{dependency.GraphicsTimeline}/" +
                    $"c{dependency.ComputeTimeline}");
            }
            work.PublishGpu(dependency);
            if (work.PublishHost is not null)
            {
                RegisterGpuLabelHostPublication(dependency, work.PublishHost);
            }
        }

        private void RegisterGpuLabelHostPublication(
            GuestGpuLabelDependency dependency,
            Action publish) =>
            _gpuLabelHostPublications.Register(dependency, publish);

        /// <summary>
        /// Returns a skipped draw's pooled data arrays: draws dropped before
        /// resource creation would otherwise strand their rented buffers.
        /// </summary>
        private static void ReturnPooledGuestData(VulkanTranslatedGuestDraw draw)
        {
            var returned = new HashSet<byte[]>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var buffer in draw.GlobalMemoryBuffers)
            {
                if (buffer.Pooled && returned.Add(buffer.Data))
                {
                    GuestDataPool.Shared.Return(buffer.Data);
                }
            }

            foreach (var buffer in draw.VertexBuffers)
            {
                if (buffer.Pooled && returned.Add(buffer.Data))
                {
                    GuestDataPool.Shared.Return(buffer.Data);
                }
            }

            if (draw.IndexBuffer is { Pooled: true } indexBuffer &&
                returned.Add(indexBuffer.Data))
            {
                indexBuffer.TryReturnPooledData();
            }
        }

        private static void ReturnPooledGuestData(VulkanComputeGuestDispatch dispatch)
        {
            var returned = new HashSet<byte[]>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var buffer in dispatch.GlobalMemoryBuffers)
            {
                if (buffer.Pooled && returned.Add(buffer.Data))
                {
                    GuestDataPool.Shared.Return(buffer.Data);
                }
            }
        }

        private string ResolveGuestSubmitContext(
            IReadOnlyList<TranslatedDrawResources> resources)
        {
            var workLabel = !string.IsNullOrEmpty(_activeGuestWorkLabel)
                ? _activeGuestWorkLabel
                : _lastGuestWorkLabel;
            var resourceName = resources.Count > 0
                ? resources[0].DebugName
                : _batchResources.Count > 0
                    ? _batchResources[0].DebugName
                    : string.Empty;
            if (string.IsNullOrEmpty(workLabel))
            {
                return string.IsNullOrEmpty(resourceName)
                    ? string.Empty
                    : $"batch={resourceName}";
            }

            return string.IsNullOrEmpty(resourceName)
                ? workLabel
                : $"{workLabel} batch={resourceName}";
        }

        private static string DescribeGuestWork(
            object work,
            VulkanGuestQueueIdentity queue,
            long sequence)
        {
            var queuePart =
                $"queue={queue.Name} submission={queue.SubmissionId} sequence={sequence}";
            return work switch
            {
                VulkanComputeGuestDispatch compute =>
                    $"compute cs=0x{compute.ShaderAddress:X16} " +
                    $"groups={compute.GroupCountX}x{compute.GroupCountY}x{compute.GroupCountZ} " +
                    $"textures={compute.Textures.Count} " +
                    $"globals={compute.GlobalMemoryBuffers.Count} " +
                    $"writes_global={(compute.WritesGlobalMemory ? 1 : 0)} " +
                    $"indirect={(compute.IsIndirect ? 1 : 0)} " +
                    $"spirv={compute.ComputeSpirv.Length} {queuePart}",
                VulkanOffscreenGuestDraw draw =>
                    $"offscreen vs=0x{draw.ShaderAddress:X16} " +
                    $"mrt={draw.Targets.Count} " +
                    $"textures={draw.Draw.Textures.Count} " +
                    $"vertices={draw.Draw.VertexCount} {queuePart}",
                VulkanOffscreenColorClear clear =>
                    $"offscreen_clear ps=0x{clear.ShaderAddress:X16} " +
                    $"mrt={clear.Targets.Count} " +
                    $"rgba=({clear.Red:0.###},{clear.Green:0.###},{clear.Blue:0.###},{clear.Alpha:0.###}) " +
                    queuePart,
                VulkanGuestImageResolve resolve =>
                    $"image_resolve src=0x{resolve.Source.Address:X16} dst=0x{resolve.Destination.Address:X16} {queuePart}",
                VulkanGuestImageClearFromBuffer imageClear =>
                    $"image_clear addr=0x{imageClear.Address:X16} bytes={imageClear.ByteCount} {queuePart}",
                VulkanOrderedGuestAction action =>
                    $"ordered_action name={action.DebugName} {queuePart}",
                VulkanGuestCacheOperation operation =>
                    $"cache_operation name={operation.DebugName} " +
                    $"count={operation.Operations.Count} {queuePart}",
                VulkanOrderedGuestFlip flip =>
                    $"ordered_flip version={flip.Version} " +
                    $"buf={flip.DisplayBufferIndex} addr=0x{flip.Address:X16} {queuePart}",
                VulkanOrderedGuestFlipWait wait =>
                    $"flip_wait version={wait.Version} " +
                    $"buf={wait.DisplayBufferIndex} {queuePart}",
                _ => $"{work.GetType().Name} {queuePart}",
            };
        }

        // Translated draws are recorded into the scheduler's current buffer and
        // submitted once per drained work batch (one vkQueueSubmit per batch).
        private bool _batchOpen;
        private int _batchDrawCount;
        private readonly List<TranslatedDrawResources> _batchResources = new();

        // The optional reuse path keeps compatible draws in one render pass.
        // The pass closes before transfer, storage, depth, or barrier work.
        private bool _openPassActive;

        private void CloseOpenTranslatedRenderPass()
        {
            if (!_openPassActive)
            {
                return;
            }

            // The store tracks the attachment layouts; the pass only has to end.
            _openPassActive = false;
            _openPassKey = null;
            _vk.CmdEndRenderPass(new CommandBuffer(_scheduler.Current.Handle));
        }

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
            _batchLabelDependency = default;
            if (!_batchOpen)
            {
                return;
            }

            _lastSubmitDebugName = ResolveGuestSubmitContext(_batchResources);

            var queueName = _scheduler.Current.Context.QueueName;
            var dependency = TakeRequiredGpuLabelDependency(queueName);
            if (_gpuLabelTimelineEnabled)
            {
                if (dependency.ComputeTimeline != 0)
                {
                    throw SubmissionScheduler.Fatal(
                        $"label dependency names a compute timeline on a single-queue device: {dependency.ComputeTimeline}");
                }

                if (dependency.GraphicsTimeline != 0)
                {
                    CheckLabelWaitOrder(dependency.GraphicsTimeline, _graphicsGuestTimelineValue);
                    bundle.AddWait(_graphicsGuestTimelineSemaphore.Handle, dependency.GraphicsTimeline);
                    _gpuLabelTimelineWaitCount++;
                }

                var signalValue = _graphicsGuestTimelineValue + 1;
                bundle.AddSignal(_graphicsGuestTimelineSemaphore.Handle, signalValue);
                _batchLabelDependency = new GuestGpuLabelDependency(signalValue, 0);
            }
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
            var queueName = _scheduler.Current.Context.QueueName;
            if (!_batchLabelDependency.IsEmpty)
            {
                _graphicsGuestTimelineValue = _batchLabelDependency.GraphicsTimeline;
                _lastSubmittedGpuLabelDependencyByGuestQueue[queueName] = _batchLabelDependency;
            }

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    tick,
                    resources,
                    retireBuffers,
                    _batchLabelDependency,
                    resources.Length > 0 ? resources[0].DebugName : "batch",
                    _activeGuestQueue,
                    _activeGuestWorkSequence));
            _lastSubmittedTimelineByGuestQueue[queueName] = tick;
        }

        private static GuestGpuLabelDependency TakeRequiredGpuLabelDependency(
            string queueName)
        {
            lock (_gate)
            {
                if (!_requiredGpuLabelDependenciesByGuestQueue.Remove(
                        queueName,
                        out var dependency))
                {
                    return default;
                }

                return dependency;
            }
        }

        private void CreateGuestTimelineSemaphores()
        {
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
            Check(
                _vk.CreateSemaphore(
                    _device,
                    &createInfo,
                    null,
                    out _graphicsGuestTimelineSemaphore),
                "vkCreateSemaphore(graphics guest timeline)");
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

            ProcessDeferredTextureDestroys();
        }

        private void RetireGuestSubmission(PendingGuestSubmission submission)
        {
            if (submission.Tick > _completedTimeline)
            {
                _completedTimeline = submission.Tick;
            }

            _gpuLabelHostPublications.Complete(submission.LabelDependency);
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

        private void WaitForAllGuestSubmissionsForCpuVisibility()
        {
            FlushBatchedGuestCommands();
            WaitForAllGuestSubmissions();
        }

        private bool TryMakeActiveGuestQueueSubmissionsCpuVisible()
        {
            FlushBatchedGuestCommands();
            if (!_lastSubmittedTimelineByGuestQueue.TryGetValue(
                    _activeGuestQueue.Name,
                    out var targetTimeline) ||
                targetTimeline <= _completedTimeline)
            {
                return true;
            }

            // Not retired yet: the work is deferred and requeued, and the loop
            // retries after GPU progress instead of blocking the drain.
            if (!_scheduler.IsTickComplete(targetTimeline))
            {
                _blockedGuestWorkTicks[_activeGuestWorkSequence] = targetTimeline;
                return false;
            }

            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_traceVulkanShaderEnabled)
            {
                TraceVulkanShader(
                    $"vk.queue_visibility queue={_activeGuestQueue.Name} " +
                    $"submission={_activeGuestQueue.SubmissionId} " +
                    $"target_timeline={targetTimeline} completed_timeline={_completedTimeline}");
            }

            return true;
        }
    }
}

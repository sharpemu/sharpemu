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
        private readonly List<VulkanDetilePass.Transients> _batchRetireDetile = new();

        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        private readonly Dictionary<string, ulong> _lastSubmittedTimelineByGuestQueue =
            new(StringComparer.Ordinal);
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;
        private long _activeGuestWorkSequence;

        private sealed record PendingGuestSubmission(
            ulong Tick,
            IReadOnlyList<TranslatedDrawResources> Resources,
            IReadOnlyList<GuestImageResource> TraceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers,
            IReadOnlyList<VulkanDetilePass.Transients> RetireDetile,
            GuestGpuLabelDependency LabelDependency,
            string DebugName,
            VulkanGuestQueueIdentity Queue,
            long WorkSequence);

        private bool TryExecuteOrderedGuestAction(VulkanOrderedGuestAction work)
        {
            var visible = TryMakeActiveGuestQueueSubmissionsCpuVisible();
            if (!visible)
            {
                RenderPhaseProfile.RecordOrderedAction(work.DebugName, completed: false);
                return false;
            }

            WriteBackAllDirtyGuestBuffers(_activeGuestQueue.Name);
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
                foreach (var image in _guestImages.Values)
                {
                    if (!ShouldTraceGuestImageStateForDiagnostics(image) ||
                        (!operation.CoversAllMemory &&
                         !VulkanGuestCacheBarrierPlanner.RangesOverlap(
                             operation.BaseAddress,
                             operation.SizeBytes,
                             image.Address,
                             image.GuestAllocationByteCount)))
                    {
                        continue;
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.guest_cache_image_overlap " +
                        $"addr=0x{image.Address:X16} " +
                        $"operation_base=0x{operation.BaseAddress:X16} " +
                        $"operation_size=0x{operation.SizeBytes:X16} " +
                        $"all={operation.CoversAllMemory} " +
                        $"domains={operation.Domains} actions={operation.Actions} " +
                        $"scope={operation.Scope} order={operation.Order}");
                }

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
                VulkanGuestImageWrite imageWrite =>
                    $"image_write addr=0x{imageWrite.Address:X16} {queuePart}",
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
        private readonly List<GuestImageResource> _batchTraceImages = new();

        // The optional reuse path keeps compatible draws in one render pass.
        // The pass closes before transfer, storage, depth, or barrier work.
        private GuestImageResource? _openPassTarget;

        private void CloseOpenTranslatedRenderPass()
        {
            if (_openPassTarget is not { } target)
            {
                return;
            }

            _openPassTarget = null;
            _openPassKey = null;
            var commandBuffer = new CommandBuffer(_scheduler.Current.Handle);
            _vk.CmdEndRenderPass(commandBuffer);
            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = target.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);
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
            var traceImages = _batchTraceImages.ToArray();
            var retireBuffers = _batchRetireBuffers.Count > 0 ? _batchRetireBuffers.ToArray() : [];
            var retireDetile = _batchRetireDetile.Count > 0 ? _batchRetireDetile.ToArray() : [];
            _batchResources.Clear();
            _batchTraceImages.Clear();
            _batchRetireBuffers.Clear();
            _batchRetireDetile.Clear();
            var queueName = _scheduler.Current.Context.QueueName;
            if (!_batchLabelDependency.IsEmpty)
            {
                _graphicsGuestTimelineValue = _batchLabelDependency.GraphicsTimeline;
                _lastSubmittedGpuLabelDependencyByGuestQueue[queueName] = _batchLabelDependency;
            }

            foreach (var referenced in _batchReferencedResources ?? resources)
            {
                foreach (var globalBuffer in referenced.GlobalMemoryBuffers)
                {
                    if (globalBuffer.Allocation is not { } allocation)
                    {
                        continue;
                    }

                    allocation.LastUseTimeline = Math.Max(allocation.LastUseTimeline, tick);
                    if (globalBuffer.Writable && globalBuffer.WriteBackToGuest)
                    {
                        MarkGuestBufferDirty(
                            allocation,
                            globalBuffer.GuestOffset,
                            globalBuffer.GuestSize,
                            queueName,
                            tick);
                    }
                }
            }

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    tick,
                    resources,
                    traceImages,
                    retireBuffers,
                    retireDetile,
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
            ProcessDeferredTextureDestroys();
        }

        private void RetireGuestSubmission(PendingGuestSubmission submission)
        {
            if (submission.Tick > _completedTimeline)
            {
                _completedTimeline = submission.Tick;
            }

            _gpuLabelHostPublications.Complete(submission.LabelDependency);
            foreach (var image in submission.TraceImages)
            {
                TraceGuestImageContents(image);
            }

            // The tick retired, so the detile dispatch that used these is done
            // reading them; hand them back for the next texture.
            foreach (var transients in submission.RetireDetile)
            {
                _detilePass?.Retire(transients);
            }

            foreach (var resources in submission.Resources)
            {
                DestroyTranslatedDrawResources(resources);
            }

            foreach (var (buffer, memory) in submission.RetireBuffers)
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _vk.FreeMemory(_device, memory, null);
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

        private void WaitForGuestBufferAllocationForCpuVisibility(
            GuestBufferAllocation allocation)
        {
            if (IsGuestBufferAllocationReferencedByOpenBatch(allocation))
            {
                FlushBatchedGuestCommands();
            }

            var targetTimeline = allocation.LastUseTimeline;
            if (targetTimeline <= _completedTimeline)
            {
                return;
            }

            _scheduler.Wait(targetTimeline);
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }
    }
}

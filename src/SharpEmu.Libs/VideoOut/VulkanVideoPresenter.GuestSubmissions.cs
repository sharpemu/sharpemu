// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial owns guest GPU submission lifetime.

        private const int MaxInFlightGuestSubmissions = 8;
        private bool _gpuLabelTimelineEnabled;
        private VkSemaphore _graphicsGuestTimelineSemaphore;
        private VkSemaphore _computeGuestTimelineSemaphore;
        private ulong _graphicsGuestTimelineValue;
        private ulong _computeGuestTimelineValue;
        private long _gpuLabelTimelineSignalCount;
        private long _gpuLabelTimelineCrossQueueWaitCount;
        private readonly Dictionary<string, GuestGpuLabelDependency>
            _lastSubmittedGpuLabelDependencyByGuestQueue = new(StringComparer.Ordinal);
        private readonly GpuLabelHostPublicationQueue _gpuLabelHostPublications = new();
        private long _graphicsQueueSubmitCount;
        private long _computeQueueSubmitCount;
        // Monotonic submission/completion counters across every queue submit
        // (guest batches, compute chunks and presents). Fences on a single
        // queue signal in submission order, so "timeline <= completed" means
        // the GPU is done with everything submitted up to that point; this
        // lets evicted resources be destroyed without a queue drain.
        private ulong _submitTimeline;
        private ulong _completedTimeline;
        private readonly Queue<(TranslatedDrawResources Resources, ulong RetireTimeline)>
            _deferredResourceDestroys = new();
        private readonly Queue<(GuestImageResource Image, ulong RetireTimeline)>
            _deferredGuestImageVersionDestroys = new();
        private readonly Stack<Fence> _recycledGuestFences = new();
        private readonly Stack<CommandBuffer> _recycledGuestCommandBuffers = new();
        private readonly List<(VkBuffer Buffer, DeviceMemory Memory)> _batchRetireBuffers = new();
        private readonly List<VulkanDetilePass.Transients> _batchRetireDetile = new();
        private const int MaxRecycledGuestFences = 32;
        private const int MaxRecycledGuestCommandBuffers = 32;

        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();
        // Submissions whose fence timed out. Keep GPU objects alive until the
        // fence signals (or the device is lost) so a single hung compute
        // dispatch cannot re-block every subsequent capacity wait for the full
        // fence timeout (~3s → ~0.3 FPS).
        private readonly Queue<PendingGuestSubmission> _abandonedGuestSubmissions = new();
        private readonly Dictionary<string, ulong> _lastSubmittedTimelineByGuestQueue =
            new(StringComparer.Ordinal);
        private VulkanGuestQueueIdentity _activeGuestQueue =
            VulkanGuestQueueIdentity.Default;
        private long _activeGuestWorkSequence;

        private sealed record PendingGuestSubmission(
            Fence Fence,
            CommandBuffer CommandBuffer,
            IReadOnlyList<TranslatedDrawResources> Resources,
            IReadOnlyList<GuestImageResource> TraceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)> RetireBuffers,
            IReadOnlyList<VulkanDetilePass.Transients> RetireDetile,
            ulong Timeline,
            GuestGpuLabelDependency LabelDependency,
            string DebugName,
            VulkanGuestQueueIdentity Queue,
            long WorkSequence);

        private CommandBuffer AllocateGuestCommandBuffer()
        {
            // The pool has ResetCommandBufferBit, so vkBeginCommandBuffer
            // implicitly resets recycled buffers.
            if (_recycledGuestCommandBuffers.TryPop(out var recycled))
            {
                return recycled;
            }

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            Check(
                _vk.AllocateCommandBuffers(
                    _device,
                    &allocateInfo,
                    out commandBuffer),
                "vkAllocateCommandBuffers(guest)");
            return commandBuffer;
        }

        private Fence AcquireGuestFence()
        {
            // Recycled fences were reset when they were collected.
            if (_recycledGuestFences.TryPop(out var recycled))
            {
                return recycled;
            }

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Fence fence;
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "vkCreateFence(guest)");
            return fence;
        }

        private void ReleaseGuestCommandBuffer(CommandBuffer commandBuffer)
        {
            if (_recycledGuestCommandBuffers.Count < MaxRecycledGuestCommandBuffers)
            {
                _recycledGuestCommandBuffers.Push(commandBuffer);
                return;
            }

            _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
        }

        private void ReleaseGuestFence(Fence fence, bool needsReset)
        {
            if (_recycledGuestFences.Count < MaxRecycledGuestFences)
            {
                if (needsReset)
                {
                    Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences(guest)");
                }

                _recycledGuestFences.Push(fence);
                return;
            }

            _vk.DestroyFence(_device, fence, null);
        }

        // Translated draws are recorded into a shared command buffer and
        // submitted once per drained work batch: on MoltenVK every
        // vkQueueSubmit is a Metal command-buffer commit (~0.8ms), which used
        // to be paid per draw and dominated the frame time.
        private CommandBuffer _batchCommandBuffer;
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
            _vk.CmdEndRenderPass(_batchCommandBuffer);
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
                _batchCommandBuffer,
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
            if (_batchOpen)
            {
                return _batchCommandBuffer;
            }

            _batchCommandBuffer = AllocateGuestCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(
                _vk.BeginCommandBuffer(_batchCommandBuffer, &beginInfo),
                "vkBeginCommandBuffer(batch)");
            _batchOpen = true;
            _batchDrawCount = 0;
            return _batchCommandBuffer;
        }

        private void FlushBatchedGuestCommands()
        {
            if (!_batchOpen)
            {
                return;
            }

            CloseOpenTranslatedRenderPass();
            _batchOpen = false;
            try
            {
                Check(_vk.EndCommandBuffer(_batchCommandBuffer), "vkEndCommandBuffer(batch)");
                SubmitGuestCommandBuffer(
                    _batchCommandBuffer,
                    _batchResources.ToArray(),
                    _batchTraceImages.ToArray(),
                    _batchRetireBuffers.Count > 0 ? _batchRetireBuffers.ToArray() : [],
                    retireDetile: _batchRetireDetile.Count > 0
                        ? _batchRetireDetile.ToArray()
                        : []);
            }
            catch
            {
                // The batch never reached the queue: release everything it
                // owned here so the stale lists cannot ride into the next
                // batch's submission.
                foreach (var resources in _batchResources)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                foreach (var (buffer, memory) in _batchRetireBuffers)
                {
                    _vk.DestroyBuffer(_device, buffer, null);
                    _vk.FreeMemory(_device, memory, null);
                }

                foreach (var transients in _batchRetireDetile)
                {
                    _detilePass?.Retire(transients);
                }

                ReleaseGuestCommandBuffer(_batchCommandBuffer);
                throw;
            }
            finally
            {
                _batchResources.Clear();
                _batchTraceImages.Clear();
                _batchRetireBuffers.Clear();
                _batchRetireDetile.Clear();
                _batchCommandBuffer = default;
            }
        }

        private void SubmitGuestCommandBuffer(
            CommandBuffer commandBuffer,
            IReadOnlyList<TranslatedDrawResources> resources,
            IReadOnlyList<GuestImageResource> traceImages,
            IReadOnlyList<(VkBuffer Buffer, DeviceMemory Memory)>? retireBuffers = null,
            IReadOnlyList<TranslatedDrawResources>? referencedResources = null,
            IReadOnlyList<VulkanDetilePass.Transients>? retireDetile = null,
            bool useComputeQueue = false)
        {
            var fence = AcquireGuestFence();
            GuestGpuLabelDependency submittedLabelDependency = default;
            try
            {
                var physicalCompute = useComputeQueue && _useDedicatedComputeQueue;
                var submitQueue = physicalCompute ? _computeQueue : _queue;
                var dependency = TakeRequiredGpuLabelDependency(_activeGuestQueue.Name);
                if (_gpuLabelTimelineEnabled &&
                    _lastSubmittedGpuLabelDependencyByGuestQueue.TryGetValue(
                        _activeGuestQueue.Name,
                        out var priorQueueDependency))
                {
                    // A guest queue is serial. Keep its order when host work moves
                    // between the graphics queue and the compute queue.
                    dependency = ResolveGpuLabelSubmissionDependency(
                        dependency,
                        priorQueueDependency);
                }
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                VkSemaphore* waitSemaphores = stackalloc VkSemaphore[2];
                ulong* waitValues = stackalloc ulong[2];
                PipelineStageFlags* waitStages = stackalloc PipelineStageFlags[2];
                var waitCount = 0u;
                if (_gpuLabelTimelineEnabled)
                {
                    if (physicalCompute && dependency.GraphicsTimeline != 0)
                    {
                        waitSemaphores[waitCount] = _graphicsGuestTimelineSemaphore;
                        waitValues[waitCount] = dependency.GraphicsTimeline;
                        waitStages[waitCount++] = PipelineStageFlags.AllCommandsBit;
                    }
                    if (!physicalCompute && dependency.ComputeTimeline != 0)
                    {
                        waitSemaphores[waitCount] = _computeGuestTimelineSemaphore;
                        waitValues[waitCount] = dependency.ComputeTimeline;
                        waitStages[waitCount++] = PipelineStageFlags.AllCommandsBit;
                    }
                    _gpuLabelTimelineCrossQueueWaitCount += waitCount;

                    var signalSemaphore = physicalCompute
                        ? _computeGuestTimelineSemaphore
                        : _graphicsGuestTimelineSemaphore;
                    var signalValue = physicalCompute
                        ? ++_computeGuestTimelineValue
                        : ++_graphicsGuestTimelineValue;
                    var timelineInfo = new TimelineSemaphoreSubmitInfo
                    {
                        SType = StructureType.TimelineSemaphoreSubmitInfo,
                        WaitSemaphoreValueCount = waitCount,
                        PWaitSemaphoreValues = waitCount == 0 ? null : waitValues,
                        SignalSemaphoreValueCount = 1,
                        PSignalSemaphoreValues = &signalValue,
                    };
                    submitInfo.PNext = &timelineInfo;
                    submitInfo.WaitSemaphoreCount = waitCount;
                    submitInfo.PWaitSemaphores = waitCount == 0 ? null : waitSemaphores;
                    submitInfo.PWaitDstStageMask = waitCount == 0 ? null : waitStages;
                    submitInfo.SignalSemaphoreCount = 1;
                    submitInfo.PSignalSemaphores = &signalSemaphore;

                    SubmitGuestQueue(submitQueue, &submitInfo, fence, resources);
                    submittedLabelDependency =
                        physicalCompute
                            ? new GuestGpuLabelDependency(0, signalValue)
                            : new GuestGpuLabelDependency(signalValue, 0);
                    _lastSubmittedGpuLabelDependencyByGuestQueue[_activeGuestQueue.Name] =
                        submittedLabelDependency;
                }
                else
                {
                    SubmitGuestQueue(submitQueue, &submitInfo, fence, resources);
                }

                if (useComputeQueue && _useDedicatedComputeQueue)
                {
                    _computeQueueSubmitCount++;
                }
                else
                {
                    _graphicsQueueSubmitCount++;
                }
            }
            catch
            {
                ReleaseGuestFence(fence, needsReset: false);
                throw;
            }

            _submitTimeline++;
            foreach (var referenced in referencedResources ?? resources)
            {
                foreach (var globalBuffer in referenced.GlobalMemoryBuffers)
                {
                    if (globalBuffer.Allocation is not { } allocation)
                    {
                        continue;
                    }

                    allocation.LastUseTimeline = Math.Max(
                        allocation.LastUseTimeline,
                        _submitTimeline);
                    if (globalBuffer.Writable && globalBuffer.WriteBackToGuest)
                    {
                        MarkGuestBufferDirty(
                            allocation,
                            globalBuffer.GuestOffset,
                            globalBuffer.GuestSize,
                            _activeGuestQueue.Name,
                            _submitTimeline);
                    }
                }
            }

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    fence,
                    commandBuffer,
                    resources,
                    traceImages,
                    retireBuffers ?? [],
                    retireDetile ?? [],
                    _submitTimeline,
                    submittedLabelDependency,
                    resources.Count > 0 ? resources[0].DebugName : "batch",
                    _activeGuestQueue,
                    _activeGuestWorkSequence));
            _lastSubmittedTimelineByGuestQueue[_activeGuestQueue.Name] =
                _submitTimeline;
        }

        private void SubmitGuestQueue(
            Queue submitQueue,
            SubmitInfo* submitInfo,
            Fence fence,
            IReadOnlyList<TranslatedDrawResources> resources)
        {
            var submitContext = ResolveGuestSubmitContext(resources);
            _lastSubmitDebugName = submitContext;
            var submitLabel = string.IsNullOrEmpty(submitContext)
                ? "vkQueueSubmit(guest)"
                : $"vkQueueSubmit(guest) during {submitContext}";
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit))
            {
                Check(_vk.QueueSubmit(submitQueue, 1, submitInfo, fence), submitLabel);
            }
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
            if (_useDedicatedComputeQueue)
            {
                Check(
                    _vk.CreateSemaphore(
                        _device,
                        &createInfo,
                        null,
                        out _computeGuestTimelineSemaphore),
                    "vkCreateSemaphore(compute guest timeline)");
            }
        }


        private void EnsureGuestSubmissionCapacity()
        {
            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
            {
                // Bounded wait so the macOS main thread returns to its event
                // pump promptly under a slow-compute backlog; if the oldest
                // isn't done yet we proceed (soft cap, dynamic pools).
                CollectCompletedGuestSubmissions(
                    waitForOldest: true,
                    maxWaitNs: _submissionCapacityWaitNs == 0
                        ? _guestFenceWaitTimeoutNs
                        : _submissionCapacityWaitNs);
            }
        }

        private void WaitForAllGuestSubmissions()
        {
            while (_pendingGuestSubmissions.Count != 0)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }

            while (_abandonedGuestSubmissions.Count != 0)
            {
                CollectAbandonedGuestSubmissions();
                if (_abandonedGuestSubmissions.Count == 0)
                {
                    break;
                }

                if (!_abandonedGuestSubmissions.TryPeek(out var oldest))
                {
                    break;
                }

                var fence = oldest.Fence;
                var result = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    _guestFenceWaitTimeoutNs);
                if (result == Result.Timeout || result == Result.ErrorDeviceLost)
                {
                    if (result == Result.ErrorDeviceLost)
                    {
                        _deviceLost = true;
                    }

                    while (_abandonedGuestSubmissions.TryDequeue(out var abandoned))
                    {
                        RetireGuestSubmission(abandoned);
                    }

                    break;
                }

                Check(result, $"vkWaitForFences(abandoned: {oldest.DebugName})");
                CollectAbandonedGuestSubmissions();
            }
        }

        private void CollectCompletedGuestSubmissions(bool waitForOldest, ulong maxWaitNs = 0)
        {
            if (waitForOldest && _pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                // maxWaitNs==0 => the full "is this submission hung" timeout,
                // which also emits the one-shot hang warning below. A shorter
                // capacity-probe wait (maxWaitNs>0) must NOT report a hang: the
                // submission is still tracked and will be collected once the GPU
                // finishes it on a later frame.
                var isProbeWait = maxWaitNs != 0 && maxWaitNs < _guestFenceWaitTimeoutNs;
                var waitNs = maxWaitNs != 0 ? maxWaitNs : _guestFenceWaitTimeoutNs;
                var result = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    waitNs);
                if (result == Result.Timeout)
                {
                    // A GPU submission whose fence never signals (typically a
                    // mistranslated compute shader that hangs the Metal queue)
                    // would otherwise block the render thread forever, starving
                    // the swapchain present (black screen). Log the culprit and
                    // continue so at least the last good frame can be shown.
                    if (isProbeWait)
                    {
                        return;
                    }

                    if (_tracedFenceTimeouts.Add(oldest.DebugName))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.fence_wait_timeout submission='{oldest.DebugName}' " +
                            $"— GPU work not completing after {_guestFenceWaitTimeoutNs / 1_000_000}ms; " +
                            "abandoning in-flight tracking so later frames are not re-blocked.");
                    }

                    // Move out of the blocking queue without destroying GPU
                    // objects yet — the work may still be running. Poll and
                    // retire from the abandoned list once the fence signals.
                    _pendingGuestSubmissions.Dequeue();
                    _abandonedGuestSubmissions.Enqueue(oldest);
                }
                else if (result == Result.ErrorDeviceLost)
                {
                    _deviceLost = true;
                    if (!_deviceLostLogged)
                    {
                        _deviceLostLogged = true;
                        var work = !string.IsNullOrEmpty(_lastGuestWorkLabel)
                            ? $"last_work={_lastGuestWorkLabel}"
                            : "work=<none>";
                        Console.Error.WriteLine(
                            "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                            $"{work} last_submit={oldest.DebugName} " +
                            "vkWaitForFences(guest) failed with ErrorDeviceLost.");
                    }
                }
                else
                {
                    Check(result, $"vkWaitForFences(guest: {oldest.DebugName})");
                }
            }

            while (_pendingGuestSubmissions.TryPeek(out var submission))
            {
                var status = _vk.GetFenceStatus(_device, submission.Fence);
                if (status == Result.NotReady && !_deviceLost)
                {
                    break;
                }

                if (status == Result.ErrorDeviceLost)
                {
                    // Pending fences never signal on a lost device; retire the
                    // submission anyway so teardown and back-pressure survive.
                    _deviceLost = true;
                }
                else if (status != Result.NotReady)
                {
                    Check(status, $"vkGetFenceStatus(guest: {submission.DebugName})");
                }

                _pendingGuestSubmissions.Dequeue();
                RetireGuestSubmission(submission);
            }

            CollectAbandonedGuestSubmissions();
            ProcessDeferredTextureDestroys();
        }

        private void CollectAbandonedGuestSubmissions()
        {
            var pending = _abandonedGuestSubmissions.Count;
            for (var i = 0; i < pending; i++)
            {
                if (!_abandonedGuestSubmissions.TryDequeue(out var submission))
                {
                    break;
                }

                var status = _vk.GetFenceStatus(_device, submission.Fence);
                if (status == Result.NotReady && !_deviceLost)
                {
                    _abandonedGuestSubmissions.Enqueue(submission);
                    continue;
                }

                if (status == Result.ErrorDeviceLost)
                {
                    _deviceLost = true;
                }
                else if (status != Result.NotReady && status != Result.Success)
                {
                    Check(status, $"vkGetFenceStatus(abandoned: {submission.DebugName})");
                }

                RetireGuestSubmission(submission);
            }
        }

        private void RetireGuestSubmission(PendingGuestSubmission submission)
        {
            if (!_deviceLost)
            {
                if (submission.Timeline > _completedTimeline)
                {
                    _completedTimeline = submission.Timeline;
                }

                _gpuLabelHostPublications.Complete(submission.LabelDependency);

                foreach (var image in submission.TraceImages)
                {
                    TraceGuestImageContents(image);
                }
            }
            else
            {
                // A lost device cannot complete the label producer. Do not
                // expose its value to CPU-visible guest memory.
                _gpuLabelHostPublications.Cancel();
            }

            // The fence has signalled, so the detile dispatch that used these
            // is done reading them; hand them back for the next texture.
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

            ReleaseGuestCommandBuffer(submission.CommandBuffer);
            ReleaseGuestFence(submission.Fence, needsReset: true);
        }

        private void WaitForAllGuestSubmissionsForCpuVisibility()
        {
            FlushBatchedGuestCommands();
            while (_pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                Check(
                    _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                    $"vkWaitForFences(cpu visibility: {oldest.DebugName})");
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }
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

            PendingGuestSubmission? target = null;
            foreach (var submission in _pendingGuestSubmissions)
            {
                if (submission.Timeline == targetTimeline)
                {
                    target = submission;
                    break;
                }
            }

            if (target is null)
            {
                throw new InvalidOperationException(
                    $"Guest queue '{_activeGuestQueue.Name}' lost pending timeline " +
                    $"{targetTimeline} (completed={_completedTimeline}).");
            }

            var fence = target.Fence;
            var status = _vk.GetFenceStatus(_device, fence);
            if (status == Result.NotReady)
            {
                // Never block the drain for seconds on ordered-action visibility.
                // A multi-second WaitForFences here tanked Dead Cells (~0.4fps)
                // and soft-locked GTA after intro once the GPU was busy. Sync
                // item ceiling is high enough that deferring a tick is safe;
                // take a short probe wait on dedicated render threads only.
                if (OperatingSystem.IsMacOS())
                {
                    return false;
                }

                const ulong orderedVisibilityProbeNs = 2_000_000UL; // 2ms
                var waitResult = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    orderedVisibilityProbeNs);
                if (waitResult == Result.Timeout)
                {
                    return false;
                }

                if (waitResult == Result.ErrorDeviceLost)
                {
                    _deviceLost = true;
                    return true;
                }

                Check(
                    waitResult,
                    $"vkWaitForFences(queue visibility: {_activeGuestQueue.Name})");
                var waitTrace = Interlocked.Increment(ref _orderedActionFenceWaitTraceCount);
                if (waitTrace <= 8 || (waitTrace & (waitTrace - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.ordered_action_fence_wait " +
                        $"count={waitTrace} queue={_activeGuestQueue.Name} " +
                        $"submission='{target.DebugName}'");
                }

                CollectCompletedGuestSubmissions(waitForOldest: false);
                if (_traceVulkanShaderEnabled)
                {
                    TraceVulkanShader(
                        $"vk.queue_visibility queue={_activeGuestQueue.Name} " +
                        $"submission={_activeGuestQueue.SubmissionId} " +
                        $"target_timeline={targetTimeline} " +
                        $"completed_timeline={_completedTimeline} waited=1");
                }

                return true;
            }

            if (status == Result.ErrorDeviceLost)
            {
                _deviceLost = true;
                return true;
            }

            Check(status, $"vkGetFenceStatus(queue visibility: {_activeGuestQueue.Name})");
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

            PendingGuestSubmission? target = null;
            foreach (var submission in _pendingGuestSubmissions)
            {
                if (submission.Timeline == targetTimeline)
                {
                    target = submission;
                    break;
                }
            }

            if (target is null)
            {
                throw new InvalidOperationException(
                    $"Guest buffer 0x{allocation.BaseAddress:X16} lost pending timeline " +
                    $"{targetTimeline} (completed={_completedTimeline}).");
            }

            var fence = target.Fence;
            Check(
                _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                $"vkWaitForFences(buffer visibility: 0x{allocation.BaseAddress:X16})");
            CollectCompletedGuestSubmissions(waitForOldest: false);
        }
    }
}

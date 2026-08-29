// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial drives the Vulkan presenter render loop.

        private void WaitForRenderWork()
        {
            using var profileScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Idle);
            var gpuWorkInFlight = _pendingGuestSubmissions.Count > 0 ||
                Array.Exists(_frameFencePending, static pending => pending);
            lock (_gate)
            {
                if (_closed ||
                    _pendingGuestWorkCount > 0 ||
                    (_latestPresentation is { } latest &&
                     latest.Sequence != _presentedSequence &&
                     latest.RequiredGuestWorkSequence <= _completedGuestWorkSequence))
                {
                    return;
                }

                System.Threading.Monitor.Wait(_gate, gpuWorkInFlight ? 1 : 8);
            }
        }

        private void Render(double _)
        {
            try
            {
                RenderCore();
            }
            catch (Exception exception)
            {
                RenderDocCapture.DiscardFrame();
                // Device loss can strike between any two Vulkan calls in the frame;
                // keep the window loop pumping instead of tearing the presenter down.
                if (!TryMarkDeviceLost(exception))
                {
                    throw;
                }
            }
        }

        private void RenderCore()
        {
            RenderDocCapture.DiscardTimedOutFrame();

            if (Volatile.Read(ref _presenterCloseRequested))
            {
                RenderDocCapture.DiscardFrame();
                Console.Error.WriteLine("[LOADER][WARN] Vulkan VideoOut closing on host shutdown request.");
                _window.Close();
                return;
            }

            if (!_vulkanReady)
            {
                return;
            }

            if (_deviceLost)
            {
                RenderDocCapture.DiscardFrame();
                // Drain queued work so producers aren't back-pressured, then
                // return without any Vulkan call (fences never signal post-loss).
                while (TryTakeGuestWork(out var lostWork))
                {
                    CompleteGuestWork(lostWork);
                }

                return;
            }

            // Reuse of a frame slot waits only on that slot's fence, keeping
            // up to MaxFramesInFlight frames pipelined between CPU and GPU.
            var frameSlot = _currentFrameSlot;
            bool frameSlotReady;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.FrameSlotWait))
            {
                frameSlotReady = TryWaitFrameSlot(frameSlot, _frameSlotWaitBudgetNs);
            }

            if (!frameSlotReady)
            {
                // The GPU is still finishing this slot's previous frame (slow
                // compute backlog). Don't block the macOS main thread — return
                // to the Cocoa event pump so the window keeps handling input
                // (F1 overlay, drag, close) and redrawing. The frame is retried
                // next Render(); the fence signals once the GPU catches up.
                return;
            }

            _presentationCommandBuffer = _frameCommandBuffers[frameSlot];
            _commandBuffer = _presentationCommandBuffer;
            if (!_deviceLost)
            {
                using var collectScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect);
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Evict))
            {
                DrainGuestImageCpuSync();
            }

            var completedWork = 0;
            HashSet<string>? deferredOrderedQueues = null;
            var drainStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            var renderWorkDeadline = _renderWorkBudgetTicks > 0
                ? drainStartTicks + _renderWorkBudgetTicks
                : long.MaxValue;
            var followupDeadline = _guestWorkFollowupBudgetTicks > 0
                ? drainStartTicks + _guestWorkFollowupBudgetTicks
                : long.MinValue;
            var workLimit = _maxGuestWorkPerRender;
            // Prefer ordered sync / flip heads while the queue is elevated so
            // label wakeups are not starved behind fat compute/draw items on
            // sibling logical queues.
            var preferSyncWork =
                _pendingSyncGuestWorkCount >= (_maxPendingGuestWorkItems / 2) ||
                _pendingGuestWorkCount >= (_maxPendingGuestWorkItems / 2);
            while (completedWork < workLimit)
            {
                // Never block the macOS main thread waiting for in-flight GPU
                // work to drain. If submission is at capacity (a slow-compute
                // backlog), stop processing and let the event pump run; the
                // remaining queued work is picked up on later frames as the GPU
                // completions free up capacity (collected non-blockingly here).
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect))
                {
                    CollectCompletedGuestSubmissions(waitForOldest: false);
                }

                if (OperatingSystem.IsMacOS() &&
                    _pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
                {
                    break;
                }

                PendingGuestWork pendingGuestWork;
                bool tookGuestWork;
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.TakeWork))
                {
                    tookGuestWork = TryTakeGuestWork(
                        out pendingGuestWork,
                        deferredOrderedQueues,
                        preferSyncWork);
                }

                if (!tookGuestWork)
                {
                    var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (completedWork == 0 ||
                        _guestWorkFollowupWaitMs <= 0 ||
                        nowTicks >= followupDeadline ||
                        nowTicks >= renderWorkDeadline ||
                        !WaitForFollowupGuestWork(_guestWorkFollowupWaitMs))
                    {
                        break;
                    }

                    continue;
                }

                if (!string.Equals(
                        _activeGuestQueue.Name,
                        pendingGuestWork.Queue.Name,
                        StringComparison.Ordinal))
                {
                    FlushBatchedGuestCommands();
                }

                _activeGuestQueue = pendingGuestWork.Queue;
                _activeGuestWorkSequence = pendingGuestWork.Sequence;
                Volatile.Write(
                    ref _executingGuestWorkSequence,
                    pendingGuestWork.Sequence);
                using var guestQueueScope = EnterGuestQueue(
                    pendingGuestWork.Queue.Name,
                    pendingGuestWork.Queue.SubmissionId);
                _enqueueAsImmediateQueueFollowup = true;
                _immediateFollowupTail = null;
                var work = pendingGuestWork.Work;
                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Describe))
                {
                    _activeGuestWorkLabel = DescribeGuestWork(
                        work,
                        pendingGuestWork.Queue,
                        pendingGuestWork.Sequence);
                }
                _lastGuestWorkLabel = _activeGuestWorkLabel;
                var deferGuestWork = false;
                var deferForFlipCapture = false;

                var traceWork = ShouldTracePresentedGuestImageContentsForDiagnostics();
                var workStart = traceWork ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (traceWork && work is VulkanComputeGuestDispatch or VulkanOffscreenGuestDraw)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.render_work_enter #{completedWork} " +
                        $"sequence={pendingGuestWork.Sequence} " +
                        $"queue={pendingGuestWork.Queue.Name} " +
                        $"submission={pendingGuestWork.Queue.SubmissionId} " +
                        $"queued_ms={(System.Diagnostics.Stopwatch.GetTimestamp() - pendingGuestWork.EnqueuedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} " +
                        work.GetType().Name);
                }

                if (_traceOrderedActionLatency && work is VulkanOrderedGuestAction orderedActionForLatency)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.ordered_action_latency #{completedWork} " +
                        $"name='{orderedActionForLatency.DebugName}' " +
                        $"queue={pendingGuestWork.Queue.Name} " +
                        $"queued_ms={(System.Diagnostics.Stopwatch.GetTimestamp() - pendingGuestWork.EnqueuedTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3} " +
                        $"pending={_pendingGuestWorkCount}");
                }
                try
                {

                    switch (work)
                    {
                        case VulkanOffscreenGuestDraw offscreenDraw:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Draw))
                            {
                                ExecuteOffscreenDraw(offscreenDraw);
                            }

                            break;
                        case VulkanOffscreenColorClear colorClear:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.ColorClear))
                            {
                                ExecuteOffscreenColorClear(colorClear);
                            }

                            break;
                        case VulkanComputeGuestDispatch computeDispatch:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Compute))
                            {
                                ExecuteComputeDispatch(computeDispatch);
                            }

                            break;
                        case VulkanGuestImageWrite guestImageWrite:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.ImageWrite))
                            {
                                ExecuteGuestImageWrite(guestImageWrite);
                            }

                            break;
                        case VulkanOrderedGuestAction orderedAction:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.OrderedAction))
                            {
                                deferGuestWork = !TryExecuteOrderedGuestAction(orderedAction);
                            }

                            break;
                        case VulkanGuestCacheOperation cacheOperation:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.OrderedAction))
                            {
                                ExecuteGuestCacheOperation(cacheOperation);
                            }

                            break;
                        case VulkanGpuLabelSignal gpuLabelSignal:
                            ExecuteGpuLabelSignal(gpuLabelSignal);
                            break;
                        case VulkanOrderedGuestFlip orderedFlip:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Flip))
                            {
                                ExecuteOrderedGuestFlip(orderedFlip);
                            }

                            break;
                        case VulkanOrderedGuestFlipWait flipWait:
                            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Flip))
                            {
                                deferGuestWork = !TryExecuteOrderedGuestFlipWait(flipWait);
                                deferForFlipCapture = deferGuestWork;
                            }

                            break;
                    }
                }
                finally
                {
                    using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CompleteWork))
                    {
                        if (!deferGuestWork || !RequeueGuestWorkFront(pendingGuestWork))
                        {
                            CompleteGuestWork(pendingGuestWork);
                        }
                    }

                    _enqueueAsImmediateQueueFollowup = false;
                    _immediateFollowupTail = null;
                    _activeGuestWorkLabel = string.Empty;
                    Volatile.Write(ref _executingGuestWorkSequence, 0);
                }

                if (deferGuestWork)
                {
                    // A flip can be on a different guest queue. Exclude the
                    // waiting queue so that the capture queue can progress.
                    if (deferForFlipCapture)
                    {
                        deferredOrderedQueues ??= new HashSet<string>(StringComparer.Ordinal);
                        deferredOrderedQueues.Add(pendingGuestWork.Queue.Name);
                        continue;
                    }

                    // macOS: non-blocking defer — exclude this logical queue for
                    // the rest of the tick so sibling queues can still progress.
                    // Windows/Linux already blocked in WaitForFences; excluding
                    // the only busy queue ends the drain immediately and leaves
                    // OrderedGuestAction stacked. Leave the item at the front and
                    // end this Render; the next tick retries after GPU progress.
                    if (OperatingSystem.IsMacOS())
                    {
                        deferredOrderedQueues ??= new HashSet<string>(StringComparer.Ordinal);
                        deferredOrderedQueues.Add(pendingGuestWork.Queue.Name);
                    }
                    else
                    {
                        break;
                    }
                }

                if (workStart != 0)
                {
                    var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - workStart)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsedMs > 250.0)
                    {
                        var desc = work switch
                        {
                            VulkanComputeGuestDispatch c => $"compute cs=0x{c.ShaderAddress:X16} groups={c.GroupCountX}x{c.GroupCountY}x{c.GroupCountZ}",
                            VulkanOffscreenGuestDraw d =>
                                $"draw mrt={d.Targets.Count} " +
                                $"rt=0x{d.Targets[0].Address:X16} " +
                                $"{d.Targets[0].Width}x{d.Targets[0].Height}",
                            _ => work.GetType().Name,
                        };
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.slow_render_work {elapsedMs:F0}ms " +
                            $"queue={pendingGuestWork.Queue.Name} " +
                            $"submission={pendingGuestWork.Queue.SubmissionId} " +
                            $"sequence={pendingGuestWork.Sequence}: {desc}");
                    }
                }

                completedWork++;

                // Return to the main-thread event pump + present once the
                // per-frame budget is spent; remaining guest work is drained
                // on subsequent Render() calls. Without this a compute-heavy
                // backlog freezes the window (macOS "Not Responding").
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= renderWorkDeadline)
                {
                    break;
                }
            }

            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Flush))
            {
                FlushBatchedGuestCommands();
            }

            CollectAbandonedGuestImageVersions();

            Presentation presentation;
            if (_window.IsMinimized)
            {
                return;
            }

            var framebufferSize = GetFramebufferSize();
            var drawableSizeChanged =
                (uint)Math.Max(framebufferSize.X, 1) != _extent.Width ||
                (uint)Math.Max(framebufferSize.Y, 1) != _extent.Height;
            var hdrStateChanged = _window.ConsumeHdrStateChange();
            var guestHdrRequestChanged =
                _videoOptions.HdrMode == HostHdrMode.Auto &&
                _hdrRequestedForSwapchain !=
                    (_window.HdrState.Enabled && VideoOutExports.IsHdrOutputRequested);
            if (_window.ConsumeSurfaceRestore() ||
                drawableSizeChanged ||
                _swapchainRecreateDeferred ||
                hdrStateChanged && _videoOptions.HdrMode != HostHdrMode.Off ||
                guestHdrRequestChanged)
            {
                RecreateSwapchainResources(
                    guestHdrRequestChanged
                        ? "guest HDR output change"
                        : hdrStateChanged
                            ? "SDL HDR state change"
                            : drawableSizeChanged
                                ? "SDL drawable resize"
                            : "restored SDL window",
                    Result.SuboptimalKhr);
                return;
            }

            bool tookPresentation;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.TakePresentation))
            {
                tookPresentation = TryTakePresentation(_presentedSequence, out presentation);
            }

            if (!tookPresentation &&
                TryTakeHostMovieOnlyPresentation(_presentedSequence, out presentation))
            {
                tookPresentation = true;
            }

            if (!tookPresentation)
            {
                // A render-loop tick with no newer flip is normal. Warn only when
                // an actual queued presentation is waiting on unfinished guest work.
                if (SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.IsActive ||
                    ShouldTracePresentedGuestImageContentsForDiagnostics())
                {
                    var hasPendingPresentation =
                        HasPendingGuestPresentation(_presentedSequence);
                    SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TracePresentNotTaken(
                        _presentedSequence,
                        hasPendingPresentation);
                    SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TraceGpuWaitSnapshot();
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        hasPendingPresentation &&
                        _presentNotTakenLoggedSequence != _presentedSequence)
                    {
                        _presentNotTakenLoggedSequence = _presentedSequence;
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] vk.present_not_taken seq={_presentedSequence} " +
                            "— presentation submitted but its required guest work isn't complete; nothing shown.");
                    }
                }

                return;
            }

            SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TracePresentTaken(
                presentation.Sequence,
                presentation.GuestImageAddress,
                presentation.GuestImageVersion);
            if (ShouldTracePresentedGuestImageContentsForDiagnostics())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.present_taken addr=0x{presentation.GuestImageAddress:X16} " +
                    $"version={presentation.GuestImageVersion} " +
                    $"drawKind={presentation.DrawKind} hasPixels={presentation.Pixels is not null} " +
                    $"hasTranslatedDraw={presentation.TranslatedDraw is not null}");
            }

            if (presentation.Pixels is null &&
                presentation.DrawKind != GuestDrawKind.FullscreenBarycentric &&
                presentation.TranslatedDraw is null &&
                presentation.GuestImageAddress == 0)
            {
                _presentedSequence = presentation.Sequence;
                return;
            }

            byte[]? pixels = null;
            if (presentation.Pixels is { } sourcePixels)
            {
                pixels = presentation.Width == _extent.Width && presentation.Height == _extent.Height
                    ? sourcePixels
                    : ScaleBgra(
                        sourcePixels,
                        presentation.Width,
                        presentation.Height,
                        _extent.Width,
                        _extent.Height);
                if ((ulong)pixels.Length > _stagingSize)
                {
                    _presentedSequence = presentation.Sequence;
                    return;
                }

            }

            TranslatedDrawResources? translatedResources = null;
            GuestImageResource? presentedGuestImage = null;
            var ownsPresentedGuestImageVersion = false;
            if (presentation.GuestImageVersion != 0)
            {
                ownsPresentedGuestImageVersion = _guestImageVersions.Remove(
                    presentation.GuestImageVersion,
                    out presentedGuestImage);
            }
            else if (presentation.GuestImageAddress != 0)
            {
                _guestImages.TryGetValue(
                    presentation.GuestImageAddress,
                    out presentedGuestImage);
            }

            if (presentation.GuestImageAddress != 0 &&
                (presentedGuestImage is null || !presentedGuestImage.Initialized))
            {
                if (ShouldTracePresentedGuestImageContentsForDiagnostics())
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.present_dropped addr=0x{presentation.GuestImageAddress:X16} " +
                        $"version={presentation.GuestImageVersion} " +
                        $"found={(presentedGuestImage is not null)} " +
                        $"initialized={(presentedGuestImage?.Initialized ?? false)} " +
                        $"— no swapchain present this frame (black).");
                }

                if (ownsPresentedGuestImageVersion && presentedGuestImage is not null)
                {
                    DestroyGuestImage(presentedGuestImage);
                }

                _presentedSequence = presentation.Sequence;
                return;
            }
            if (ownsPresentedGuestImageVersion)
            {
                System.Diagnostics.Debug.Assert(
                    _frameGuestImageVersions[frameSlot] is null,
                    "A reusable frame slot cannot still own a flip version.");
                _frameGuestImageVersions[frameSlot] = presentedGuestImage;
            }
            if (presentedGuestImage is not null)
            {
                _directPresentationCount++;
                var traceAddressedPresentation =
                    ShouldTraceAddressedPresentedGuestImage(presentedGuestImage);
                if (traceAddressedPresentation ||
                    ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                    (_directPresentationCount is 1 or 30 or 120 ||
                     _directPresentationCount % 600 == 0))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.present_sample frame={_directPresentationCount} " +
                        $"addr=0x{presentedGuestImage.Address:X16}");
                }
            }

            if (presentation.TranslatedDraw is { } translatedDraw)
            {
                try
                {
                    translatedResources = CreateTranslatedDrawResources(
                        translatedDraw,
                        _renderPass,
                        [PresentationTargetFormat],
                        _extent);
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        !_firstGuestDrawPresented &&
                        translatedResources.Textures is
                        [
                        { GuestImage: { } guestImage },
                        ] &&
                        _tracedGuestImageContents.Add(guestImage.Address))
                    {
                        TraceGuestImageContents(guestImage);
                    }
                }
                catch (Exception exception)
                {
                    _presentedSequence = presentation.Sequence;
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan VideoOut translated draw setup failed: {exception.Message}");
                    return;
                }

                FlushBatchedGuestCommands();
            }

            uint imageIndex;
            Result acquireResult;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Acquire))
            {
                acquireResult = _swapchainApi.AcquireNextImage(
                    _device,
                    _swapchain,
                    SwapchainAcquireTimeoutNs,
                    _frameImageAvailable[frameSlot],
                    default,
                    &imageIndex);
            }
            if (acquireResult == Result.Timeout)
            {
                ReleaseUnsubmittedPresentationResources(
                    frameSlot,
                    translatedResources,
                    ownsPresentedGuestImageVersion,
                    presentedGuestImage);
                return;
            }

            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkAcquireNextImageKHR", acquireResult);
                ReleaseUnsubmittedPresentationResources(
                    frameSlot,
                    translatedResources,
                    ownsPresentedGuestImageVersion,
                    presentedGuestImage);

                _presentedSequence = presentation.Sequence;
                return;
            }

            CheckSwapchainResult(acquireResult, "vkAcquireNextImageKHR");
            var recreateAfterPresent = acquireResult == Result.SuboptimalKhr;

            if (pixels is not null)
            {
                var mapped = (void*)_frameUploadMapped[frameSlot];
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }
            }

            using var presentScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Present);
            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &beginInfo), "vkBeginCommandBuffer");

            PipelineStageFlags waitStage;
            if (pixels is not null)
            {
                RecordUpload(imageIndex, frameSlot);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (presentation.DrawKind == GuestDrawKind.FullscreenBarycentric)
            {
                var clearValue = default(ClearValue);
                var renderPassInfo = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _renderPass,
                    Framebuffer = _framebuffers[imageIndex],
                    RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                    ClearValueCount = 1,
                    PClearValues = &clearValue,
                };
                _vk.CmdBeginRenderPass(
                    _commandBuffer,
                    &renderPassInfo,
                    SubpassContents.Inline);
                _vk.CmdBindPipeline(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    _barycentricPipeline);
                _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
                _vk.CmdEndRenderPass(_commandBuffer);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }
            else if (presentedGuestImage is not null)
            {
                RecordGuestImageBlit(imageIndex, presentedGuestImage);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (translatedResources is not null)
            {
                RecordTranslatedDraw(imageIndex, translatedResources);
                waitStage = PipelineStageFlags.AllCommandsBit;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported translated guest draw: {presentation.DrawKind}.");
            }

            if (PerfOverlay.Enabled)
            {
                RecordOverlayBlit(imageIndex, frameSlot);
            }

            if (_hdrOutputActive)
            {
                RecordHdrPresentation(imageIndex, presentation.IsHdr);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }

            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            var imageAvailable = _frameImageAvailable[frameSlot];
            var commandBuffer = _commandBuffer;
            var renderFinished = _renderFinishedPerImage[imageIndex];
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished,
            };
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit))
            {
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, _frameFences[frameSlot]),
                    "vkQueueSubmit");
            }

            _submitTimeline++;
            _frameTimelines[frameSlot] = _submitTimeline;
            _frameFencePending[frameSlot] = true;
            _frameTranslatedResources[frameSlot] = translatedResources;
            if (translatedResources is not null)
            {
                // CPU-side layout bookkeeping only; later command buffers are
                // recorded after this submission, so queue order makes the
                // flags valid before any dependent GPU work runs.
                MarkSampledImagesInitialized(translatedResources);
                MarkStorageImagesInitialized(translatedResources);
            }

            var swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            Result presentResult;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueuePresent))
            {
                presentResult = _swapchainApi.QueuePresent(_queue, &presentInfo);
            }

            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                // The submitted frame still executes; RecreateSwapchainResources
                // drains it (and every frame slot) before destroying anything.
                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                _presentedSequence = presentation.Sequence;
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            recreateAfterPresent |= presentResult == Result.SuboptimalKhr;
            VideoOutExports.ReportPresentedFrame();
            PerfOverlay.RecordPresent();
            RenderPhaseProfile.RecordFrame();
            if (_swapchainReadbackPending || !_pendingAliasImageDumps.IsEmpty)
            {
                // Diagnostics read back GPU memory and need this frame done.
                WaitFrameSlot(frameSlot);
                if (_swapchainReadbackPending)
                {
                    TraceSwapchainReadback();
                }

                while (_pendingAliasImageDumps.TryDequeue(out var aliasImage))
                {
                    TraceGuestImageContents(aliasImage);
                }
            }

            CollectCompletedGuestSubmissions(waitForOldest: false);
            _imageInitialized[imageIndex] = true;
            _currentFrameSlot = (frameSlot + 1) % MaxFramesInFlight;
            _presentedSequence = presentation.Sequence;
            if (presentation.IsSplash && !_splashPresented)
            {
                _splashPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented splash: " +
                    $"{presentation.Width}x{presentation.Height}");
            }
            else if (!presentation.IsSplash && !_firstFramePresented)
            {
                _firstFramePresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented first frame: " +
                    $"{presentation.Width}x{presentation.Height}");
            }

            if (pixels is null && !_firstGuestDrawPresented)
            {
                _firstGuestDrawPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented guest frame: " +
                    (presentedGuestImage is not null
                        ? $"image=0x{presentedGuestImage.Address:X16} " +
                          $"{presentedGuestImage.Width}x{presentedGuestImage.Height}"
                        : presentation.TranslatedDraw is null
                        ? $"{presentation.DrawKind}"
                        : $"shader textures={presentation.TranslatedDraw.Textures.Count}"));
            }

            if (recreateAfterPresent)
            {
                RecreateSwapchainResources("present suboptimal", Result.SuboptimalKhr);
            }
        }
    }
}

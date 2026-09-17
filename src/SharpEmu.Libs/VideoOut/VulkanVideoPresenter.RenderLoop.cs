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
        private void ProcessGuestCacheReadbacks()
        {
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.BufferFaults))
            {
                _bufferCache.ProcessPendingFaultBuffer();
            }

            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.ImageReadback))
            {
                _imageCache.FlushScheduledReadbacks();
            }
        }

        private void RunGuestCacheCollection()
        {
            ProcessGuestCacheReadbacks();

            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.ImageCollect))
            {
                _imageCache.RunGarbageCollector();
            }

            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.BufferCollect))
            {
                _bufferCache.RunGarbageCollector();
            }
        }

        private void WaitForRenderWork()
        {
            using var profileScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Idle);
            var gpuWorkInFlight = _pendingGuestSubmissions.Count > 0 ||
                Array.Exists(_frameInFlight, static pending => pending);
            var blockedRetryWait = BlockedRetryWaitMilliseconds();
            lock (_gate)
            {
                if (_closed ||
                    Volatile.Read(ref _presenterCloseRequested) ||
                    _relay.HasPendingCommands ||
                    (_vulkanReady && _commandStream.HasUnblockedPending) ||
                    blockedRetryWait == 0 ||
                    HasReadyPresentationLocked())
                {
                    return;
                }

                var waitMilliseconds = gpuWorkInFlight ? 1 : 8;
                if (blockedRetryWait is { } retryWait)
                {
                    waitMilliseconds = Math.Min(waitMilliseconds, retryWait);
                }

                var waitPhase = _commandStream.HasPending
                    ? RenderPhaseProfile.Phase.IdleBlockedCommands
                    : _pendingGuestImagePresentations.Count > 0 || _pendingVideoPresentations.Count > 0 ||
                        (_latestPresentation is { } latest && latest.Sequence != _presentedSequence)
                        ? RenderPhaseProfile.Phase.IdlePendingPresentation
                        : RenderPhaseProfile.Phase.IdleNoQueuedWork;
                using var waitProfile = RenderPhaseProfile.MeasureDetail(waitPhase);
                SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.WaitStarted,
                    detail: waitMilliseconds);
                var signaled = System.Threading.Monitor.Wait(_gate, waitMilliseconds);
                // The result describes the monitor wait, not the arrival of runnable guest work.
                SubmissionFlowProfile.Record(signaled ? SubmissionFlowProfile.EventKind.WaitSignaled
                    : SubmissionFlowProfile.EventKind.WaitTimedOut, detail: (int)waitPhase);
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
            using var profileScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Unattributed);
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

            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.QueueRelay))
            {
                _relay.RunPendingCommands();
            }
            if (_deviceLost)
            {
                RenderDocCapture.DiscardFrame();
                _commandStream.DiscardAll();
                return;
            }

            // Reuse of a frame slot waits only on that slot's tick, keeping
            // up to MaxFramesInFlight frames pipelined between CPU and GPU.
            var frameSlot = _currentFrameSlot;
            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.FrameSlotWait))
            {
                WaitFrameSlot(frameSlot);
            }

            if (!_deviceLost)
            {
                using var collectScope = RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Collect);
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }
            var renderWorkDeadline = _renderWorkBudgetTicks > 0
                ? System.Diagnostics.Stopwatch.GetTimestamp() + _renderWorkBudgetTicks
                : long.MaxValue;
            RunCommandStreamSlices(renderWorkDeadline);

            using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Flush))
            {
                FlushBatchedGuestCommands();
            }

            PerfOverlay.SetGuestCacheStatistics(
                _bufferCache.TotalUsedMemory, _imageCache.TotalUsedMemory, _deviceInfo.LiveAllocations, _deviceInfo.PeakAllocations);
            CollectAbandonedGuestImageVersions();

            using var preparationScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PresentationPreparation);
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
                tookPresentation = TryTakePresentation(out presentation);
            }

            if (!tookPresentation &&
                TryTakeHostMovieOnlyPresentation(_presentedSequence, out presentation))
            {
                tookPresentation = true;
            }

            if (!tookPresentation)
            {
                // A render-loop tick with no newer flip is normal.
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
                            "— presentation submitted but its required GPU tick is incomplete; nothing shown.");
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
                    $"drawKind={presentation.DrawKind} hasPixels={presentation.Pixels is not null}");
            }

            if (presentation.Pixels is null &&
                presentation.DrawKind != GuestDrawKind.FullscreenBarycentric &&
                presentation.GuestImageAddress == 0)
            {
                CompletePresentation(in presentation, presented: false);
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
                    CompletePresentation(in presentation, presented: false);
                    return;
                }

            }

            GuestImageResource? presentedGuestImage = null;
            var ownsPresentedGuestImageVersion = false;
            if (presentation.GuestImageVersion != 0)
            {
                ownsPresentedGuestImageVersion = _guestImageVersions.Remove(
                    presentation.GuestImageVersion,
                    out presentedGuestImage);
            }

            if (presentation.GuestImageAddress != 0 && presentedGuestImage is null)
            {
                if (ShouldTracePresentedGuestImageContentsForDiagnostics())
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.present_dropped addr=0x{presentation.GuestImageAddress:X16} " +
                        $"version={presentation.GuestImageVersion} " +
                        "found=False " +
                        $"— no swapchain present this frame (black).");
                }

                CompletePresentation(in presentation, presented: false);
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
                    ownsPresentedGuestImageVersion,
                    presentedGuestImage);
                return;
            }

            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkAcquireNextImageKHR", acquireResult);
                ReleaseUnsubmittedPresentationResources(
                    frameSlot,
                    ownsPresentedGuestImageVersion,
                    presentedGuestImage);

                CompletePresentation(in presentation, presented: false);
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
            FlushBatchedGuestCommands();
            _commandBuffer = CurrentRecordingBuffer();

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
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported translated guest draw: {presentation.DrawKind}.");
            }

            if (PerfOverlay.DrawOnScreen)
            {
                RecordOverlayBlit(imageIndex, frameSlot);
            }

            if (_hdrOutputActive)
            {
                RecordHdrPresentation(imageIndex, presentation.IsHdr);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }

            var renderFinished = _renderFinishedPerImage[imageIndex];
            _submitTimeline = SubmitPresentation(
                _frameImageAvailable[frameSlot],
                waitStage,
                renderFinished);
            _commandBuffer = default;
            _frameTimelines[frameSlot] = _submitTimeline;
            _frameInFlight[frameSlot] = true;

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
                lock (_queueGate)
                {
                    presentResult = _swapchainApi.QueuePresent(_queue, &presentInfo);
                }
            }

            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                // The submitted frame still executes; RecreateSwapchainResources
                // drains it (and every frame slot) before destroying anything.
                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                CompletePresentation(in presentation, presented: false);
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            recreateAfterPresent |= presentResult == Result.SuboptimalKhr;
            VideoOutExports.ReportPresentedFrame();
            PerfOverlay.RecordPresent();
            RenderPhaseProfile.RecordFrame();
            if (_swapchainReadbackPending)
            {
                // Diagnostics read back GPU memory and need this frame done.
                WaitFrameSlot(frameSlot);
                TraceSwapchainReadback();
            }

            CollectCompletedGuestSubmissions(waitForOldest: false);
            _imageInitialized[imageIndex] = true;
            _currentFrameSlot = (frameSlot + 1) % MaxFramesInFlight;
            CompletePresentation(in presentation, presented: true);
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
                        : $"{presentation.DrawKind}"));
            }

            if (recreateAfterPresent)
            {
                RecreateSwapchainResources("present suboptimal", Result.SuboptimalKhr);
            }
        }
    }
}

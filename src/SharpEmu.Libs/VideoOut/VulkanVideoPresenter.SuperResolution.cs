// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.Psml;
using SharpEmu.Libs.VideoOut.Upscaling;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly ShaderImageShape SuperResolutionSampledShape = new(false, false, false, false, TextureNumericClass.Float);
    private static readonly ShaderImageShape SuperResolutionStorageShape = new(false, false, true, false, TextureNumericClass.Float);

    private sealed partial class Presenter
    {
        public void DispatchSuperResolution(SuperResolutionDispatch dispatch)
        {
            if (OnCommandThread)
            {
                var (address, size) = SuperResolutionOutputRange(dispatch);
                _commandThread!.PostRange(() => DispatchSuperResolution(dispatch), address, size, CommandThread.RangeKind.Copy);
                return;
            }

            Recording.RecordSuperResolution(dispatch);
        }

        private void ReleaseIdleSuperResolution()
        {
            if (_superResolution is not { } service || !service.WentIdle())
            {
                return;
            }

            _ = CurrentRecordingBuffer();
            _scheduler.Finish();
            CollectCompletedGuestSubmissions(waitForOldest: false);
            service.ReleaseResources();
        }

        private static (ulong Address, ulong Size) SuperResolutionOutputRange(SuperResolutionDispatch dispatch)
        {
            if (!dispatch.Has(SuperResolutionInput.OutputColor))
            {
                return (0, 0);
            }

            try
            {
                using (ImageRequestBuilders.RejectInvalidDescriptors())
                {
                    var data = ImageRequestBuilders.Texture(dispatch.Texture(SuperResolutionInput.OutputColor), SuperResolutionStorageShape).Request.Description.Data;
                    return (data.Address, data.Size);
                }
            }
            catch (TextureDescriptorRejectedException)
            {
                return (0, 0);
            }
        }

        private Result CreateSwapchainThroughHooks(SwapchainCreateInfoKHR* info, out SwapchainKHR swapchain)
        {
            if (_streamline is { HasSwapchainProxies: true } streamline)
            {
                SwapchainKHR created = default;
                var result = streamline.CreateSwapchainProxy(_device, info, null, &created);
                swapchain = created;
                return result;
            }

            return _swapchainApi.CreateSwapchain(_device, info, null, out swapchain);
        }

        private Result GetSwapchainImagesThroughHooks(SwapchainKHR swapchain, uint* count, Image* images) =>
            _streamline is { HasSwapchainProxies: true } streamline
                ? streamline.GetSwapchainImagesProxy(_device, swapchain, count, images)
                : _swapchainApi.GetSwapchainImages(_device, swapchain, count, images);

        private void DestroySwapchainThroughHooks(SwapchainKHR swapchain)
        {
            if (_streamline is { HasSwapchainProxies: true } streamline)
            {
                streamline.DestroySwapchainProxy(_device, swapchain, null);
                return;
            }

            _swapchainApi.DestroySwapchain(_device, swapchain, null);
        }

        private Result AcquireNextImageThroughHooks(SwapchainKHR swapchain, ulong timeout, VkSemaphore semaphore, uint* imageIndex) =>
            _streamline is { HasSwapchainProxies: true } streamline
                ? streamline.AcquireNextImageProxy(_device, swapchain, timeout, semaphore, default, imageIndex)
                : _swapchainApi.AcquireNextImage(_device, swapchain, timeout, semaphore, default, imageIndex);

        private Result QueuePresentThroughHooks(PresentInfoKHR* presentInfo) =>
            _streamline is { HasSwapchainProxies: true } streamline
                ? streamline.QueuePresentProxy(_queue, presentInfo)
                : _swapchainApi.QueuePresent(_queue, presentInfo);
    }

    private sealed partial class RecordingHost
    {
        private int _superResolutionWarnings;

        internal void RecordSuperResolution(SuperResolutionDispatch dispatch)
        {
            if (_presenter._deviceLost || _presenter._superResolution is not { } service)
            {
                return;
            }

            if (!dispatch.Has(SuperResolutionInput.OutputColor) || !dispatch.Has(SuperResolutionInput.Color))
            {
                WarnSuperResolution($"the dispatch has no output or input color (inputs=0x{dispatch.TextureMask:X2})");
                return;
            }

            var passThrough = (dispatch.Flags & SuperResolutionFlags.PassThrough) != 0;
            var fsrRuntime = passThrough ? null : service.Fsr;
            var xessRuntime = service.Xess;
            if (fsrRuntime is not null)
            {
                Debug.Assert(!_worker);
                _scheduler.Timeline.RefreshCompletedTick();
                var waitTick = fsrRuntime.WaitTick(_scheduler.CompletedTick);
                if (waitTick != 0)
                {
                    if (waitTick == _scheduler.CurrentTick)
                    {
                        FlushBatchedGuestCommands();
                    }

                    _scheduler.Wait(waitTick);
                    fsrRuntime.WaitTick(_scheduler.CompletedTick);
                }
            }

            var inputs = new List<(SuperResolutionInput Input, bool Storage)>
            {
                (SuperResolutionInput.OutputColor, true),
                (SuperResolutionInput.Color, false),
            };
            foreach (var optional in (ReadOnlySpan<SuperResolutionInput>)[SuperResolutionInput.Depth, SuperResolutionInput.MotionVectors, SuperResolutionInput.Exposure])
            {
                if (dispatch.Has(optional))
                {
                    inputs.Add((optional, false));
                }
            }

            if ((fsrRuntime is not null || xessRuntime is not null && !passThrough) && dispatch.Has(SuperResolutionInput.ReactiveMask))
            {
                inputs.Add((SuperResolutionInput.ReactiveMask, false));
            }

            EnsureBatchedGuestCommands();
            EndRendering();
            var bindings = new SuperResolutionBinding[inputs.Count];
            try
            {
                using (ImageRequestBuilders.RejectInvalidDescriptors())
                {
                    for (var index = 0; index < inputs.Count; index++)
                    {
                        bindings[index] = ResolveSuperResolutionImage(dispatch.Texture(inputs[index].Input), inputs[index].Storage);
                    }
                }
            }
            catch (TextureDescriptorRejectedException exception)
            {
                ResetImageBindings();
                WarnSuperResolution($"an input descriptor was rejected: {exception.Message}");
                return;
            }

            ResetImageBindings();
            var transitions = BeginBatchedGuestCommands();
            for (var index = 0; index < bindings.Length; index++)
            {
                TransitionImage(bindings[index].Image,
                    inputs[index].Storage ? AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit : AccessFlags.ShaderReadBit,
                    null,
                    transitions);
            }

            SuperResolutionImage ImageOf(SuperResolutionInput input)
            {
                var index = inputs.FindIndex(entry => entry.Input == input);
                if (index < 0 || bindings[index].View.Handle == 0)
                {
                    return default;
                }

                var image = bindings[index].Image;
                var depth = image.Description.IsDepth;
                var view = bindings[index].Description;
                var backing = image.Backing;
                var singleLayerView = backing.ImageType == ImageType.Type2D && backing.Layers == 1 && backing.Samples == 1 &&
                    view.Type == ImageViewType.Type2D && view.BaseLevel == 0 && view.BaseLayer == 0 && view.MinLod == 0;
                var fsrView = fsrRuntime is not null && singleLayerView &&
                    UsesNativeFsrChannels(input, view.Mapping) &&
                    (depth
                        ? view.Aspect == ImageAspectFlags.DepthBit && (_presenter.FsrSeparateDepthStencilLayouts ||
                            (ViewFormatRules.FullAspects(backing.Format) & ImageAspectFlags.StencilBit) == 0)
                        : view.Format == backing.Format);
                var storage = inputs[index].Storage;
                var xessView = xessRuntime is not null && singleLayerView && view.LevelCount == 1 && view.Format == backing.Format &&
                    view.Aspect == (depth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit) &&
                    !ViewFormatRules.IsIntegerFormat(view.Format) &&
                    (backing.Usage & (storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit)) != 0 &&
                    (!storage || (view.Usage & ImageUsageFlags.StorageBit) != 0);
                return new SuperResolutionImage(
                    backing.Handle.Handle,
                    bindings[index].View.Handle,
                    (uint)(depth ? backing.Format : fsrRuntime is not null || xessRuntime is not null ? view.Format : bindings[index].RequestedFormat),
                    backing.Extent.Width,
                    backing.Extent.Height,
                    backing.MipLevels,
                    backing.Layers,
                    (uint)backing.Usage,
                    (uint)backing.Flags,
                    depth,
                    fsrView)
                {
                    XessCompatibleView = xessView,
                };
            }

            var output = ImageOf(SuperResolutionInput.OutputColor);
            var color = ImageOf(SuperResolutionInput.Color);
            if (!output.IsValid || !color.IsValid)
            {
                WarnSuperResolution("the output or input color has no image");
                return;
            }

            var renderX = Math.Min(dispatch.RenderOffsetX, color.Width - 1);
            var renderY = Math.Min(dispatch.RenderOffsetY, color.Height - 1);
            var renderWidth = dispatch.RenderWidth is > 0 and var width ? Math.Min(width, color.Width - renderX) : color.Width - renderX;
            var renderHeight = dispatch.RenderHeight is > 0 and var height ? Math.Min(height, color.Height - renderY) : color.Height - renderY;
            var outputWidth = dispatch.OutputWidth is > 0 and var contextWidth ? Math.Min(contextWidth, output.Width) : output.Width;
            var outputHeight = dispatch.OutputHeight is > 0 and var contextHeight ? Math.Min(contextHeight, output.Height) : output.Height;
            var frame = new SuperResolutionFrame
            {
                Dispatch = dispatch,
                Output = output,
                Color = color,
                Depth = ImageOf(SuperResolutionInput.Depth),
                MotionVectors = ImageOf(SuperResolutionInput.MotionVectors),
                Exposure = ImageOf(SuperResolutionInput.Exposure),
                Reactive = ImageOf(SuperResolutionInput.ReactiveMask),
                RenderX = renderX,
                RenderY = renderY,
                RenderWidth = renderWidth,
                RenderHeight = renderHeight,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
            };

            var outputImage = bindings[0].Image;
            var capture = PsmlExports.BeginCaptureFrame(dispatch.ContextId);
            if (capture is { } captureFrame)
            {
                Debug.Assert(!_worker);
                for (var index = 1; index < bindings.Length; index++)
                {
                    if (bindings[index].View.Handle != 0)
                    {
                        _imageCache.SaveDiagnosticImage(bindings[index].Image, $"{captureFrame.Path}-{inputs[index].Input}", pssrCapture: true);
                    }
                }
            }

            var dlss = !passThrough && frame.Depth.IsValid && frame.MotionVectors.IsValid &&
                service.TryDlss(BeginBatchedGuestCommands(), frame);
            var fsr = fsrRuntime is not null &&
                service.TryFsr(BeginBatchedGuestCommands(), frame, _scheduler.CurrentTick, _scheduler.CompletedTick);
            var xess = false;
            if (xessRuntime is not null)
            {
                Debug.Assert(!_worker);
                _scheduler.Timeline.RefreshCompletedTick();
                xess = service.TryXess(BeginBatchedGuestCommands(), frame, _scheduler.CurrentTick, _scheduler.CompletedTick, _scheduler.QueueGate);
            }
            var jitterCorrected = !dlss && !fsr && !xess && service.TryJitterCompensatedBasic(
                BeginBatchedGuestCommands(), frame, bindings[1].Description, bindings[0].Description);
            if (dlss || fsrRuntime is not null || xessRuntime is not null || jitterCorrected)
            {
                ForgetNativeShadow();
            }

            var recorded = dlss || fsr || xess || jitterCorrected;
            if (!recorded)
            {
                var command = BeginBatchedGuestCommands();
                TransitionImage(bindings[1].Image, AccessFlags.TransferReadBit, null, command);
                TransitionImage(outputImage, AccessFlags.TransferWriteBit, null, command);
                recorded = service.TryBasic(BeginBatchedGuestCommands(), frame);
            }

            if (recorded)
            {
                _imageCache.MarkGpuWritten(bindings[0].Identifier);
            }

            if (capture is { } finishedCapture)
            {
                if (recorded)
                {
                    _imageCache.SaveDiagnosticImage(outputImage, $"{finishedCapture.Path}-OutputColor", pssrCapture: true);
                }

                // The image callbacks for this dispatch are already ahead of this completion.
                _scheduler.QueueCompletionAction(finishedCapture.Complete);
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[PSSR][INFO] Capture context={dispatch.ContextId} backend={(dlss ? "DLSS" : fsr ? "FSR" : xess ? "XeSS" : recorded ? "Basic" : "none")} render={renderWidth}x{renderHeight}+{renderX},{renderY} output={outputWidth}x{outputHeight} jitter={dispatch.JitterX},{dispatch.JitterY} path={finishedCapture.Path}"));
            }
        }

        private static bool UsesNativeFsrChannels(SuperResolutionInput input, ComponentMapping mapping)
        {
            // FSR rebuilds the image view and reads only the channels required by this input.
            return (mapping.R is ComponentSwizzle.Identity or ComponentSwizzle.R) && (input switch
            {
                SuperResolutionInput.Color => (mapping.G is ComponentSwizzle.Identity or ComponentSwizzle.G) &&
                    (mapping.B is ComponentSwizzle.Identity or ComponentSwizzle.B),
                SuperResolutionInput.MotionVectors => mapping.G is ComponentSwizzle.Identity or ComponentSwizzle.G,
                _ => true,
            });
        }

        private readonly record struct SuperResolutionBinding(
            ResourceSlotIdentifier Identifier, CachedImage Image, ImageView View, Format RequestedFormat, ImageViewDescription Description);

        private SuperResolutionBinding ResolveSuperResolutionImage(ReadOnlySpan<uint> words, bool storage)
        {
            var resolution = ImageRequestBuilders.Texture(words, storage ? SuperResolutionStorageShape : SuperResolutionSampledShape);
            CompleteOverlappingPublications(resolution.Request.Description.Data.Address, resolution.Request.Description.Data.Size, includeLabels: false);
            SynchronizeColorMetadata(resolution.Request);
            EnsureBatchedGuestCommands();
            var request = resolution.Request;
            var identifier = LookUpImage(ref request, resolution.ExactFormat);
            resolution = resolution with { Request = request };
            identifier = ImageRequestBuilders.ValidateTextureOwner(_imageCache, identifier, resolution);
            ApplyNativeColorClear(identifier, resolution.Request);
            BindImage(identifier, storage);
            request = resolution.Request;
            if (storage)
            {
                request = request with { View = request.View with { LevelCount = 1 } };
            }

            var view = _imageCache.AcquireTextureView(identifier, request, out var description);
            var image = Image(identifier);
            image.Uses.Storage |= storage;
            image.Uses.Texture |= !storage;
            return new SuperResolutionBinding(identifier, image, view, request.View.Format, description);
        }

        private void ForgetNativeShadow() => _shadowCommand = 0;

        private void WarnSuperResolution(string reason)
        {
            if (Interlocked.Increment(ref _superResolutionWarnings) <= 8)
            {
                Console.Error.WriteLine($"[PSSR][WARN] A super resolution dispatch was skipped: {reason}.");
            }
        }
    }
}

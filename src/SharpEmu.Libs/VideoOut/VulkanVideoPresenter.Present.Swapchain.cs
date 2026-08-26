// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Media;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial owns the Vulkan swapchain lifecycle.

        private SwapchainKHR _swapchain;
        private Image[] _swapchainImages = [];
        private ImageView[] _swapchainImageViews = [];
        private Framebuffer[] _framebuffers = [];
        private bool[] _imageInitialized = [];
        private Format _swapchainFormat;
        private ColorSpaceKHR _swapchainColorSpace;
        private bool _hdrOutputActive;
        private bool _hdrRequestedForSwapchain;
        private float _hdrSdrWhiteLevel = 1f;
        private float _hdrHeadroom = 1f;
        private Image[] _presentationImages = [];
        private DeviceMemory[] _presentationImageMemory = [];
        private ImageView[] _presentationImageViews = [];
        private ImageView[] _presentationSampleViews = [];
        private RenderPass _hdrRenderPass;
        private Framebuffer[] _hdrFramebuffers = [];
        private DescriptorSetLayout _hdrDescriptorSetLayout;
        private DescriptorPool _hdrDescriptorPool;
        private DescriptorSet[] _hdrDescriptorSets = [];
        private DescriptorSet[] _hdrPqDescriptorSets = [];
        private PipelineLayout _hdrPipelineLayout;
        private Pipeline _hdrPipeline;
        private Pipeline _hdrPqPipeline;
        private Sampler _hdrSampler;
        private Extent2D _extent;
        private RenderPass _renderPass;
        private PipelineLayout _pipelineLayout;
        private Pipeline _barycentricPipeline;

        // Presentation runs with multiple frames in flight: each frame slot
        // owns a command buffer, an acquire semaphore and a fence, so the CPU
        // can record frame N+1 while the GPU still executes frame N. The
        // per-present vkQueueWaitIdle this replaces made CPU and GPU costs
        // strictly additive. Render-finished semaphores are per swapchain
        // image because the presentation engine may still wait on them after
        // the frame fence has signaled.
        private const int MaxFramesInFlight = 2;
        private CommandBuffer[] _frameCommandBuffers = [];
        private VkSemaphore[] _frameImageAvailable = [];
        private VkSemaphore[] _renderFinishedPerImage = [];
        private Fence[] _frameFences = [];
        private bool[] _frameFencePending = [];
        private ulong[] _frameTimelines = [];
        private TranslatedDrawResources?[] _frameTranslatedResources = [];
        private GuestImageResource?[] _frameGuestImageVersions = [];
        private int _currentFrameSlot;

        private bool _swapchainRecreateDeferred;

        private void CreateSwapchain()
        {
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

            uint formatCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatPointer = formats)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatPointer),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR");
            }

            var hdrState = _window.HdrState;
            var guestHdrRequested = VideoOutExports.IsHdrOutputRequested;
            var requestHdr = _videoOptions.HdrMode switch
            {
                HostHdrMode.On => true,
                HostHdrMode.Auto => hdrState.Enabled && guestHdrRequested,
                _ => false,
            };
            _hdrRequestedForSwapchain = requestHdr;
            var surfaceFormat = ChooseSurfaceFormat(formats, requestHdr, out _hdrOutputActive);
            _hdrSdrWhiteLevel = _hdrOutputActive ? Math.Max(1f, hdrState.SdrWhiteLevel) : 1f;
            _hdrHeadroom = _hdrOutputActive ? Math.Max(1f, hdrState.Headroom) : 1f;
            _swapchainFormat = surfaceFormat.Format;
            _swapchainColorSpace = surfaceFormat.ColorSpace;
            _extent = ChooseExtent(capabilities);
            HostMovieBridge.SetPresentationSize(_extent.Width, _extent.Height);
            var presentMode = ChoosePresentMode();
            var imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            }

            var compositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha);
            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage =
                    ImageUsageFlags.TransferDstBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = presentMode,
                Clipped = true,
            };

            Check(_swapchainApi.CreateSwapchain(_device, &createInfo, null, out _swapchain), "vkCreateSwapchainKHR");

            uint swapchainImageCount = 0;
            Check(
                _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, null),
                "vkGetSwapchainImagesKHR");
            _swapchainImages = new Image[swapchainImageCount];
            fixed (Image* imagePointer = _swapchainImages)
            {
                Check(
                    _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, imagePointer),
                    "vkGetSwapchainImagesKHR");
            }

            _imageInitialized = new bool[swapchainImageCount];
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan output color: requested={_videoOptions.HdrMode} " +
                $"display_hdr={hdrState.Enabled} guest_hdr={guestHdrRequested} active={_hdrOutputActive} " +
                $"format={_swapchainFormat} colorspace={_swapchainColorSpace} " +
                $"sdr_white={_hdrSdrWhiteLevel:F3} headroom={_hdrHeadroom:F3}");
            if (requestHdr && !_hdrOutputActive)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] HDR output requested but the Vulkan surface exposes no scRGB format; using SDR.");
            }
        }

        private void CreateGuestDrawResources()
        {
            var presentationFormat = PresentationTargetFormat;
            var colorAttachment = new AttachmentDescription
            {
                Format = presentationFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = PresentationTargetFinalLayout,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(
                _vk.CreateRenderPass(
                    _device,
                    &renderPassInfo,
                    null,
                    out var swapchainRenderPass),
                "vkCreateRenderPass");
            if (swapchainRenderPass.Handle == 0)
            {
                throw new InvalidOperationException(
                    "vkCreateRenderPass returned a null swapchain render pass");
            }

            _renderPass = swapchainRenderPass;

            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            _framebuffers = new Framebuffer[_swapchainImages.Length];
            if (_hdrOutputActive)
            {
                _presentationImages = new Image[_swapchainImages.Length];
                _presentationImageMemory = new DeviceMemory[_swapchainImages.Length];
                _presentationImageViews = new ImageView[_swapchainImages.Length];
                _presentationSampleViews = new ImageView[_swapchainImages.Length];
            }
            for (var index = 0; index < _swapchainImages.Length; index++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _swapchainImages[index],
                    ViewType = ImageViewType.Type2D,
                    Format = _swapchainFormat,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[index]),
                    "vkCreateImageView");

                var imageView = _swapchainImageViews[index];
                if (_hdrOutputActive)
                {
                    CreatePresentationImage(index);
                    imageView = _presentationImageViews[index];
                }
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = swapchainRenderPass,
                    AttachmentCount = 1,
                    PAttachments = &imageView,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[index]),
                    "vkCreateFramebuffer");
            }

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            Check(
                _vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout),
                "vkCreatePipelineLayout");
            CreateBarycentricPipeline();
            if (_hdrOutputActive)
            {
                CreateHdrPresentationResources();
            }
        }

        private PresentModeKHR ChoosePresentMode()
        {
            // MAILBOX never blocks vkQueuePresentKHR on vblank, so a slow
            // frame does not quantize the frame rate down to 30/20 fps the
            // way FIFO does; the guest side is already paced by PaceFlip.
            // FIFO is the only mode guaranteed by the spec and remains the
            // fallback (MoltenVK typically exposes FIFO + IMMEDIATE only).
            uint modeCount = 0;
            if (_surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &modeCount,
                    null) != Result.Success ||
                modeCount == 0)
            {
                return PresentModeKHR.FifoKhr;
            }

            var modes = stackalloc PresentModeKHR[(int)modeCount];
            if (_surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &modeCount,
                    modes) != Result.Success)
            {
                return PresentModeKHR.FifoKhr;
            }

            if (!_videoOptions.VSync)
            {
                for (var index = 0u; index < modeCount; index++)
                {
                    if (modes[index] == PresentModeKHR.ImmediateKhr)
                    {
                        return PresentModeKHR.ImmediateKhr;
                    }
                }
            }

            for (var index = 0u; index < modeCount; index++)
            {
                if (modes[index] == PresentModeKHR.MailboxKhr)
                {
                    return PresentModeKHR.MailboxKhr;
                }
            }

            return PresentModeKHR.FifoKhr;
        }

        private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                var fallbackWidth = _extent.Width != 0
                    ? _extent.Width
                    : (uint)_videoOptions.Width;
                var fallbackHeight = _extent.Height != 0
                    ? _extent.Height
                    : (uint)_videoOptions.Height;
                return new Extent2D(
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Width,
                        fallbackWidth,
                        capabilities.MinImageExtent.Width,
                        capabilities.MaxImageExtent.Width),
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Height,
                        fallbackHeight,
                        capabilities.MinImageExtent.Height,
                        capabilities.MaxImageExtent.Height));
            }

            var size = GetFramebufferSize();
            return new Extent2D(
                ClampSurfaceExtent(
                    (uint)Math.Max(size.X, 1),
                    (uint)_videoOptions.Width,
                    capabilities.MinImageExtent.Width,
                    capabilities.MaxImageExtent.Width),
                ClampSurfaceExtent(
                    (uint)Math.Max(size.Y, 1),
                    (uint)_videoOptions.Height,
                    capabilities.MinImageExtent.Height,
                    capabilities.MaxImageExtent.Height));
        }

        private (int X, int Y) GetFramebufferSize()
        {
            var size = _window.PixelSize;
            return (size.Width, size.Height);
        }

        private static uint ClampSurfaceExtent(
            uint value,
            uint fallback,
            uint minimum,
            uint maximum)
        {
            value = value <= 1 && fallback > 1 ? fallback : value;
            minimum = Math.Max(minimum, 1u);
            // Minimized / unmapped Win32 surfaces often report MaxImageExtent=0.
            // Clamping the fallback into [1,1] would create a useless 1x1
            // swapchain; keep the last/default size instead.
            if (maximum < minimum)
            {
                maximum = Math.Max(fallback, minimum);
            }

            return Math.Clamp(value, minimum, maximum);
        }

        private static SurfaceFormatKHR ChooseSurfaceFormat(
            IReadOnlyList<SurfaceFormatKHR> formats,
            bool requestHdr,
            out bool hdrActive)
        {
            if (requestHdr)
            {
                foreach (var format in formats)
                {
                    if (format.Format == Format.R16G16B16A16Sfloat &&
                        format.ColorSpace == ColorSpaceKHR.SpaceExtendedSrgbLinearExt)
                    {
                        hdrActive = true;
                        return format;
                    }
                }
            }

            hdrActive = false;
            foreach (var format in formats)
            {
                if (format.Format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm &&
                    format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }

            return formats.Count > 0
                ? formats[0]
                : throw new InvalidOperationException("The Vulkan surface exposes no pixel formats.");
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            foreach (var candidate in new[]
                     {
                         CompositeAlphaFlagsKHR.OpaqueBitKhr,
                         CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.InheritBitKhr,
                     })
            {
                if ((supported & candidate) != 0)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("The Vulkan surface exposes no composite alpha mode.");
        }

        private void RecreateSwapchainResources(string operation, Result result)
        {
            if (_device.Handle == 0)
            {
                return;
            }

            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    _physicalDevice,
                    _surface,
                    out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            var framebufferSize = GetFramebufferSize();
            var hasFixedExtent = capabilities.CurrentExtent.Width != uint.MaxValue;
            var surfaceWidth = hasFixedExtent
                ? capabilities.CurrentExtent.Width
                : (uint)Math.Max(framebufferSize.X, 0);
            var surfaceHeight = hasFixedExtent
                ? capabilities.CurrentExtent.Height
                : (uint)Math.Max(framebufferSize.Y, 0);
            if (surfaceWidth <= 1 || surfaceHeight <= 1)
            {
                // Minimized / unmapped window reports 0x0. Deferring forever
                // leaves an OutOfDate swapchain with no presents. ChooseExtent
                // already clamps 0 to last/default size — recreate with that.
                if (!_swapchainRecreateDeferred)
                {
                    _swapchainRecreateDeferred = true;
                    Console.Error.WriteLine(
                        "[LOADER][INFO] Vulkan VideoOut swapchain recreate " +
                        $"using fallback extent (window reported {surfaceWidth}x{surfaceHeight})");
                }
            }

            _swapchainRecreateDeferred = false;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreating swapchain after {operation}: {result}");
            _vk.DeviceWaitIdle(_device);
            DrainFrameSlots();
            CollectCompletedGuestSubmissions(waitForOldest: false);
            DestroySwapchainResources();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            QueueSplashReplayAfterSwapchainRecreate();
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreated swapchain: " +
                $"{_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private void QueueSplashReplayAfterSwapchainRecreate()
        {
            lock (_gate)
            {
                if (_latestPresentation is not
                    {
                        IsSplash: true,
                        Pixels: not null,
                    } splash ||
                    splash.Sequence > _presentedSequence)
                {
                    return;
                }

                _latestPresentation = splash with
                {
                    Sequence = _presentedSequence + 1,
                };
            }
        }

        private void DestroySwapchainResources()
        {
            DestroyHostMovieImage();
            DestroyPresentEncodeImage();
            for (var slot = 0; slot < _frameUploadBuffers.Length; slot++)
            {
                if (_frameUploadMapped.Length > slot && _frameUploadMapped[slot] != 0)
                {
                    _vk.UnmapMemory(_device, _frameUploadMemory[slot]);
                }
                if (_frameUploadBuffers[slot].Handle != 0)
                {
                    _vk.DestroyBuffer(_device, _frameUploadBuffers[slot], null);
                }
                if (_frameUploadMemory[slot].Handle != 0)
                {
                    _vk.FreeMemory(_device, _frameUploadMemory[slot], null);
                }
            }
            _frameUploadBuffers = [];
            _frameUploadMemory = [];
            _frameUploadMapped = [];
            if (_stagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, _stagingBuffer, null);
                _stagingBuffer = default;
            }
            if (_stagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _stagingMemory, null);
                _stagingMemory = default;
                _stagingSize = 0;
            }
            foreach (var semaphore in _frameImageAvailable)
            {
                if (semaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(_device, semaphore, null);
                }
            }
            _frameImageAvailable = [];
            foreach (var semaphore in _renderFinishedPerImage)
            {
                if (semaphore.Handle != 0)
                {
                    _vk.DestroySemaphore(_device, semaphore, null);
                }
            }
            _renderFinishedPerImage = [];
            if (_overlayImage.Handle != 0)
            {
                _vk.DestroyImage(_device, _overlayImage, null);
                _overlayImage = default;
            }
            if (_overlayImageMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _overlayImageMemory, null);
                _overlayImageMemory = default;
            }
            for (var slot = 0; slot < _overlayStagingBuffers.Length; slot++)
            {
                if (_overlayStagingBuffers[slot].Handle != 0)
                {
                    _vk.DestroyBuffer(_device, _overlayStagingBuffers[slot], null);
                }
                if (_overlayStagingMemory[slot].Handle != 0)
                {
                    _vk.FreeMemory(_device, _overlayStagingMemory[slot], null);
                }
            }
            _overlayStagingBuffers = [];
            _overlayStagingMemory = [];
            _overlayStagingMapped = [];
            _overlayImageInitialized = false;
            foreach (var fence in _frameFences)
            {
                if (fence.Handle != 0)
                {
                    _vk.DestroyFence(_device, fence, null);
                }
            }
            _frameFences = [];
            _frameFencePending = [];
            _frameTimelines = [];
            _frameTranslatedResources = [];
            _frameGuestImageVersions = [];
            while (_recycledGuestFences.TryPop(out var recycledFence))
            {
                _vk.DestroyFence(_device, recycledFence, null);
            }
            if (_hdrPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _hdrPipeline, null);
                _hdrPipeline = default;
            }
            if (_hdrPqPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _hdrPqPipeline, null);
                _hdrPqPipeline = default;
            }
            if (_hdrPipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _hdrPipelineLayout, null);
                _hdrPipelineLayout = default;
            }
            if (_hdrDescriptorPool.Handle != 0)
            {
                _vk.DestroyDescriptorPool(_device, _hdrDescriptorPool, null);
                _hdrDescriptorPool = default;
            }
            _hdrDescriptorSets = [];
            _hdrPqDescriptorSets = [];
            if (_hdrDescriptorSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(_device, _hdrDescriptorSetLayout, null);
                _hdrDescriptorSetLayout = default;
            }
            if (_hdrSampler.Handle != 0)
            {
                _vk.DestroySampler(_device, _hdrSampler, null);
                _hdrSampler = default;
            }
            foreach (var framebuffer in _hdrFramebuffers)
            {
                if (framebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, framebuffer, null);
                }
            }
            _hdrFramebuffers = [];
            if (_hdrRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, _hdrRenderPass, null);
                _hdrRenderPass = default;
            }
            if (_barycentricPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _barycentricPipeline, null);
                _barycentricPipeline = default;
            }
            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
                _pipelineLayout = default;
            }
            foreach (var framebuffer in _framebuffers)
            {
                if (framebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, framebuffer, null);
                }
            }
            if (_renderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, _renderPass, null);
                _renderPass = default;
            }
            foreach (var imageView in _swapchainImageViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            foreach (var imageView in _presentationSampleViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            _presentationSampleViews = [];
            foreach (var imageView in _presentationImageViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            _presentationImageViews = [];
            foreach (var image in _presentationImages)
            {
                if (image.Handle != 0)
                {
                    _vk.DestroyImage(_device, image, null);
                }
            }
            _presentationImages = [];
            foreach (var memory in _presentationImageMemory)
            {
                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                }
            }
            _presentationImageMemory = [];
            if (_commandPool.Handle != 0)
            {
                // Destroying the pool frees every command buffer allocated
                // from it, including recycled and per-frame ones.
                _recycledGuestCommandBuffers.Clear();
                _frameCommandBuffers = [];
                _vk.DestroyCommandPool(_device, _commandPool, null);
                _commandPool = default;
                _commandBuffer = default;
                _presentationCommandBuffer = default;
            }
            if (_swapchain.Handle != 0)
            {
                _swapchainApi.DestroySwapchain(_device, _swapchain, null);
                _swapchain = default;
            }

            _swapchainImages = [];
            _swapchainImageViews = [];
            _framebuffers = [];
            _imageInitialized = [];
            _hdrOutputActive = false;
            _hdrRequestedForSwapchain = false;
            _hdrSdrWhiteLevel = 1f;
            _hdrHeadroom = 1f;
        }

        private static void CheckSwapchainResult(Result result, string operation)
        {
            if (result is Result.Success or Result.SuboptimalKhr)
            {
                return;
            }

            if (result == Result.ErrorDeviceLost)
            {
                throw new VulkanDeviceLostException(operation);
            }

            throw new InvalidOperationException($"{operation} failed with {result}.");
        }
    }
}

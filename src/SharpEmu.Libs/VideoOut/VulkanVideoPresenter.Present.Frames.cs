// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    internal static PipelineStageFlags GetPresentationUploadSourceStage(bool isHdrOutput, bool isImageInitialized) =>
        !isHdrOutput ? PipelineStageFlags.TransferBit : isImageInitialized
            ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit;

    private sealed partial class Presenter
    {
        // This partial records presentation frame transfers.

        private VkBuffer _stagingBuffer;
        private DeviceMemory _stagingMemory;
        private ulong _stagingSize;
        private VkBuffer[] _frameUploadBuffers = [];
        private DeviceMemory[] _frameUploadMemory = [];
        private nint[] _frameUploadMapped = [];

        private bool _tracedPresentedSwapchain;
        private bool _swapchainReadbackPending;
        private long _presentedSwapchainCount;

        private void CreateStagingBuffer(ulong size)
        {
            _stagingBuffer = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _stagingMemory);
            _stagingSize = size;
        }

        private void CreateFrameUploadBuffers(ulong size)
        {
            _frameUploadBuffers = new VkBuffer[MaxFramesInFlight];
            _frameUploadMemory = new DeviceMemory[MaxFramesInFlight];
            _frameUploadMapped = new nint[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                _frameUploadBuffers[slot] = CreateBuffer(
                    size,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out _frameUploadMemory[slot]);
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _frameUploadMemory[slot], 0, size, 0, &mapped),
                    "vkMapMemory(frame upload)");
                _frameUploadMapped[slot] = (nint)mapped;
            }
        }

        private void RecordUpload(uint imageIndex, int frameSlot)
        {
            var presentationTarget = PresentationTargetImage(imageIndex);
            var oldLayout = _imageInitialized[imageIndex]
                ? PresentationTargetFinalLayout
                : ImageLayout.Undefined;
            var toTransfer = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = _hdrOutputActive && _imageInitialized[imageIndex]
                    ? AccessFlags2.ShaderReadBit : 0,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                GetPresentationUploadSourceStage(_hdrOutputActive, _imageInitialized[imageIndex]),
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _frameUploadBuffers[frameSlot],
                presentationTarget,
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toFinalLayout = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = _hdrOutputActive
                    ? AccessFlags2.ShaderReadBit
                    : AccessFlags2.MemoryReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = PresentationTargetFinalLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                _hdrOutputActive
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toFinalLayout);
        }

        // PS5 float VideoOut buffers (A16B16G16R16F flips) hold linear scRGB
        // light where 1.0 is SDR white; hardware scan-out applies the display
        // transfer. vkCmdBlitImage converts numerically only, so presenting a
        // linear-float guest frame into a UNORM swapchain shows near-black
        // for any dim scene. Encode linear->sRGB by blitting through an sRGB
        // intermediate (sRGB stores encode), then raw-copying the encoded
        // bytes into the same-class UNORM swapchain image.
        // One intermediate per swapchain image: every frame discards the encode
        // image's contents (Undefined -> TransferDst) before blitting into it,
        // so a single shared image lets the next frame in flight overwrite the
        // one the previous frame has not copied out yet, which presents as a
        // fully zeroed (black) swapchain image.
        private Image[] _presentEncodeImages = [];
        private DeviceMemory[] _presentEncodeMemories = [];
        // The encoded bytes in the swapchain's own format. Copying the sRGB image straight into a
        // swapchain image makes MoltenVK keep a reinterpreting view of that presentable image; its
        // drawable changes every frame and destroying the swapchain then dereferences a freed device.
        private Image[] _presentBounceImages = [];
        private DeviceMemory[] _presentBounceMemories = [];
        private Extent2D _presentEncodeExtent;

        private bool TryGetPresentEncodeImage(uint imageIndex, out Image encodeImage, out Image bounceImage)
        {
            encodeImage = default;
            bounceImage = default;
            var encodeFormat = GetSrgbCounterpart(_swapchainFormat);
            if (encodeFormat == Format.Undefined)
            {
                return false;
            }

            if (_presentEncodeImages.Length != 0 &&
                (_presentEncodeExtent.Width != _extent.Width ||
                 _presentEncodeExtent.Height != _extent.Height))
            {
                DestroyPresentEncodeImage();
            }

            if (_presentEncodeImages.Length <= imageIndex)
            {
                var grown = new Image[_swapchainImages.Length];
                var grownMemory = new DeviceMemory[_swapchainImages.Length];
                _presentEncodeImages.CopyTo(grown, 0);
                _presentEncodeMemories.CopyTo(grownMemory, 0);
                _presentEncodeImages = grown;
                _presentEncodeMemories = grownMemory;
                var grownBounce = new Image[_swapchainImages.Length];
                var grownBounceMemory = new DeviceMemory[_swapchainImages.Length];
                _presentBounceImages.CopyTo(grownBounce, 0);
                _presentBounceMemories.CopyTo(grownBounceMemory, 0);
                _presentBounceImages = grownBounce;
                _presentBounceMemories = grownBounceMemory;
            }

            if (imageIndex >= _presentEncodeImages.Length)
            {
                return false;
            }

            if (_presentEncodeImages[imageIndex].Handle == 0)
            {
                CreatePresentTransferImage(encodeFormat, $"SharpEmu present sRGB-encode image {imageIndex}",
                    out _presentEncodeImages[imageIndex], out _presentEncodeMemories[imageIndex]);
                CreatePresentTransferImage(_swapchainFormat, $"SharpEmu present encoded-bytes image {imageIndex}",
                    out _presentBounceImages[imageIndex], out _presentBounceMemories[imageIndex]);
                _presentEncodeExtent = new Extent2D(_extent.Width, _extent.Height);
            }

            encodeImage = _presentEncodeImages[imageIndex];
            bounceImage = _presentBounceImages[imageIndex];
            return true;
        }

        private void CreatePresentTransferImage(Format format, string name, out Image image, out DeviceMemory memory)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(_extent.Width, _extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out image), "vkCreateImage(present transfer)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_deviceInfo.AllocateMemory(allocationInfo, out memory), "vkAllocateMemory(present transfer)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(present transfer)");
            SetDebugName(ObjectType.Image, image.Handle, name);
        }

        private void DestroyPresentEncodeImage()
        {
            for (var index = 0; index < _presentEncodeImages.Length; index++)
            {
                if (_presentEncodeImages[index].Handle != 0)
                {
                    _vk.DestroyImage(_device, _presentEncodeImages[index], null);
                    _presentEncodeImages[index] = default;
                }

                if (_presentEncodeMemories[index].Handle != 0)
                {
                    _deviceInfo.FreeMemory(_presentEncodeMemories[index]);
                    _presentEncodeMemories[index] = default;
                }
            }

            _presentEncodeImages = [];
            _presentEncodeMemories = [];
            for (var index = 0; index < _presentBounceImages.Length; index++)
            {
                if (_presentBounceImages[index].Handle != 0)
                {
                    _vk.DestroyImage(_device, _presentBounceImages[index], null);
                }

                if (_presentBounceMemories[index].Handle != 0)
                {
                    _deviceInfo.FreeMemory(_presentBounceMemories[index]);
                }
            }

            _presentBounceImages = [];
            _presentBounceMemories = [];

            _presentEncodeExtent = default;
        }

        private void RecordGuestImageBlit(
            uint imageIndex,
            GuestImageResource source)
        {
            var presentationTarget = PresentationTargetImage(imageIndex);
            var presentedCount = Interlocked.Increment(ref _presentedSwapchainCount);
            var periodicDumpInterval = SwapchainDumpInterval();
            var traceDestination =
                ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                (!_tracedPresentedSwapchain ||
                 periodicDumpInterval > 0 && presentedCount % periodicDumpInterval == 0);
            _tracedPresentedSwapchain |= traceDestination;
            BeginDebugLabel(
                _commandBuffer,
                $"SharpEmu present image 0x{source.Address:X16}");

            var sourceToTransfer = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                // An offscreen target is last written as a color attachment,
                // then put in ShaderReadOnlyOptimal for later sampling.  A
                // layout-only handoff to ShaderRead does not make that write
                // visible to this transfer when no shader sample occurs in
                // between.  NVIDIA's Linux driver exposed the resulting stale
                // (usually black) image while Windows drivers happened to
                // tolerate it.  Include all preceding writes before blitting
                // the image into the swapchain.
                SrcAccessMask = AccessFlags2.MemoryWriteBit | AccessFlags2.ShaderReadBit,
                DstAccessMask = AccessFlags2.TransferReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToTransfer = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = _imageInitialized[imageIndex]
                    ? _hdrOutputActive
                        ? AccessFlags2.ShaderReadBit
                        : AccessFlags2.MemoryReadBit
                    : 0,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = _imageInitialized[imageIndex]
                    ? PresentationTargetFinalLayout
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            // Encode linear floating-point colors before presentation to a UNORM target.
            var encodeForPresent = false;
            Image encodeImage = default;
            Image bounceImage = default;
            if (IsLinearFloatPresentSource(source.Format) &&
                GetSrgbCounterpart(PresentationTargetFormat) != Format.Undefined)
            {
                encodeForPresent = TryGetPresentEncodeImage(imageIndex, out encodeImage, out bounceImage);
            }

            var encodeToTransferDst = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferReadBit,
                DstAccessMask = AccessFlags2.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = encodeImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            var barriers = stackalloc ImageMemoryBarrier2[3];
            barriers[0] = sourceToTransfer;
            barriers[1] = destinationToTransfer;
            barriers[2] = encodeToTransferDst;
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                encodeForPresent ? 3u : 2u,
                barriers);

            var sourceX = 0u;
            var sourceY = 0u;
            var sourceWidth = source.Width;
            var sourceHeight = source.Height;
            var destinationX = 0u;
            var destinationY = 0u;
            var destinationWidth = _extent.Width;
            var destinationHeight = _extent.Height;
            var scalingMode = _videoOptions.ScalingMode;
            if (scalingMode == HostScalingMode.Cover)
            {
                var sourceIsWider = (ulong)sourceWidth * _extent.Height >
                                     (ulong)_extent.Width * sourceHeight;
                if (sourceIsWider)
                {
                    sourceWidth = Math.Max(1u, (uint)((ulong)sourceHeight * _extent.Width / _extent.Height));
                    sourceX = (source.Width - sourceWidth) / 2;
                }
                else
                {
                    sourceHeight = Math.Max(1u, (uint)((ulong)sourceWidth * _extent.Height / _extent.Width));
                    sourceY = (source.Height - sourceHeight) / 2;
                }
            }
            else if (scalingMode is HostScalingMode.Fit or HostScalingMode.Integer)
            {
                var useIntegerScale = scalingMode == HostScalingMode.Integer &&
                                      sourceWidth <= _extent.Width && sourceHeight <= _extent.Height;
                if (useIntegerScale)
                {
                    var scale = Math.Max(1u, Math.Min(_extent.Width / sourceWidth, _extent.Height / sourceHeight));
                    destinationWidth = sourceWidth * scale;
                    destinationHeight = sourceHeight * scale;
                }
                else if ((ulong)sourceWidth * _extent.Height > (ulong)_extent.Width * sourceHeight)
                {
                    destinationWidth = _extent.Width;
                    destinationHeight = Math.Max(1u, (uint)((ulong)_extent.Width * sourceHeight / sourceWidth));
                }
                else
                {
                    destinationHeight = _extent.Height;
                    destinationWidth = Math.Max(1u, (uint)((ulong)_extent.Height * sourceWidth / sourceHeight));
                }

                destinationX = (_extent.Width - destinationWidth) / 2;
                destinationY = (_extent.Height - destinationHeight) / 2;
            }

            if (destinationX != 0 || destinationY != 0 ||
                destinationWidth != _extent.Width || destinationHeight != _extent.Height)
            {
                var clearColor = new ClearColorValue(0f, 0f, 0f, 1f);
                var clearRange = ColorSubresourceRange();
                _vk.CmdClearColorImage(
                    _commandBuffer,
                    encodeForPresent ? encodeImage : presentationTarget,
                    ImageLayout.TransferDstOptimal,
                    &clearColor,
                    1,
                    &clearRange);
                var clearComplete = new MemoryBarrier2
                {
                    SType = StructureType.MemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.TransferWriteBit,
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    _commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                    0, 1, &clearComplete, 0, null, 0, null);
            }

            var sourceOffsets = new ImageBlit.SrcOffsetsBuffer
            {
                Element0 = new Offset3D(checked((int)sourceX), checked((int)sourceY), 0),
                Element1 = new Offset3D(
                    checked((int)(sourceX + sourceWidth)),
                    checked((int)(sourceY + sourceHeight)),
                    1),
            };
            var destinationOffsets = new ImageBlit.DstOffsetsBuffer
            {
                Element0 = new Offset3D(checked((int)destinationX), checked((int)destinationY), 0),
                Element1 = new Offset3D(
                    checked((int)(destinationX + destinationWidth)),
                    checked((int)(destinationY + destinationHeight)),
                    1),
            };
            var region = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                SrcOffsets = sourceOffsets,
                DstSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                DstOffsets = destinationOffsets,
            };
            // Nearest keeps integer upscales pixel-crisp, but any fractional
            // scale (e.g. a 3840x2160 guest frame into a 2560x1440 swapchain)
            // must blend neighbours or it silently drops every Nth source
            // row/column, which shreds 1-2px features in the guest frame.
            var isIntegerUpscale =
                sourceWidth != 0 && sourceHeight != 0 &&
                destinationWidth >= sourceWidth && destinationHeight >= sourceHeight &&
                destinationWidth % sourceWidth == 0 && destinationHeight % sourceHeight == 0;
            var preserveEncodedSrgb =
                !encodeForPresent &&
                CanCopyEncodedSrgbPresentSource(source.Format, PresentationTargetFormat) &&
                sourceWidth == destinationWidth &&
                sourceHeight == destinationHeight;
            if (preserveEncodedSrgb)
            {
                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    SrcOffset = new Offset3D(
                        checked((int)sourceX),
                        checked((int)sourceY),
                        0),
                    DstSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstOffset = new Offset3D(
                        checked((int)destinationX),
                        checked((int)destinationY),
                        0),
                    Extent = new Extent3D(sourceWidth, sourceHeight, 1),
                };
                _vk.CmdCopyImage(
                    _commandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    presentationTarget,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copy);
            }
            else
            {
                _vk.CmdBlitImage(
                    _commandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    encodeForPresent ? encodeImage : presentationTarget,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &region,
                    isIntegerUpscale ? Filter.Nearest : Filter.Linear);
            }

            if (encodeForPresent)
            {
                var encodeToTransferSrc = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = encodeImage,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &encodeToTransferSrc);

                // Raw same-class copy keeps the sRGB-encoded bytes unchanged. The format changes on an
                // image the presenter owns; the swapchain image only receives a same-format copy.
                var encodedCopy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(_extent.Width, _extent.Height, 1),
                };
                var bounceToTransferDst = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferReadBit,
                    DstAccessMask = AccessFlags2.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = bounceImage,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    _commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &bounceToTransferDst);
                _vk.CmdCopyImage(
                    _commandBuffer,
                    encodeImage,
                    ImageLayout.TransferSrcOptimal,
                    bounceImage,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &encodedCopy);
                var bounceToTransferSrc = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = bounceImage,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    _commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &bounceToTransferSrc);
                _vk.CmdCopyImage(
                    _commandBuffer,
                    bounceImage,
                    ImageLayout.TransferSrcOptimal,
                    presentationTarget,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &encodedCopy);
            }

            if (traceDestination)
            {
                var destinationToReadback = new ImageMemoryBarrier2
                {
                    SType = StructureType.ImageMemoryBarrier2,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstAccessMask = AccessFlags2.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = presentationTarget,
                    SubresourceRange = ColorSubresourceRange(),
                };
                VulkanSynchronization.PipelineBarrier(_vk,
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToReadback);

                var copyRegion = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    presentationTarget,
                    ImageLayout.TransferSrcOptimal,
                    _stagingBuffer,
                    1,
                    &copyRegion);
                _swapchainReadbackPending = true;
            }

            var sourceToShaderRead = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferReadBit,
                DstAccessMask = AccessFlags2.ShaderReadBit,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToFinalLayout = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcAccessMask = traceDestination
                    ? AccessFlags2.TransferReadBit
                    : AccessFlags2.TransferWriteBit,
                DstAccessMask = _hdrOutputActive
                    ? AccessFlags2.ShaderReadBit
                    : AccessFlags2.MemoryReadBit,
                OldLayout = traceDestination
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.TransferDstOptimal,
                NewLayout = PresentationTargetFinalLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            barriers[0] = sourceToShaderRead;
            barriers[1] = destinationToFinalLayout;
            VulkanSynchronization.PipelineBarrier(_vk,
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                _hdrOutputActive
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                2,
                barriers);
            EndDebugLabel(_commandBuffer);
        }

        private void TraceSwapchainReadback()
        {
            _swapchainReadbackPending = false;
            var byteCount = checked((ulong)_extent.Width * _extent.Height * 4);
            void* mapped;
            Check(
                _vk.MapMemory(_device, _stagingMemory, 0, byteCount, 0, &mapped),
                "vkMapMemory(swapchain readback)");
            try
            {
                var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                var nonzeroBytes = 0L;
                var nonblackPixels = 0L;
                ulong hash = 14695981039346656037UL;
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    var b0 = bytes[offset];
                    var b1 = bytes[offset + 1];
                    var b2 = bytes[offset + 2];
                    var b3 = bytes[offset + 3];
                    nonzeroBytes += b0 == 0 ? 0 : 1;
                    nonzeroBytes += b1 == 0 ? 0 : 1;
                    nonzeroBytes += b2 == 0 ? 0 : 1;
                    nonzeroBytes += b3 == 0 ? 0 : 1;
                    nonblackPixels += b0 != 0 || b1 != 0 || b2 != 0 ? 1 : 0;
                    hash = (hash ^ b0) * 1099511628211UL;
                    hash = (hash ^ b1) * 1099511628211UL;
                    hash = (hash ^ b2) * 1099511628211UL;
                    hash = (hash ^ b3) * 1099511628211UL;
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.swapchain_image size={_extent.Width}x{_extent.Height} " +
                    $"format={_swapchainFormat} nonzero_bytes={nonzeroBytes}/{byteCount} " +
                    $"nonblack_pixels={nonblackPixels}/{(ulong)_extent.Width * _extent.Height} " +
                    $"hash=0x{hash:X16}");

                var dumpDir = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
                if (!string.IsNullOrWhiteSpace(dumpDir))
                {
                    Directory.CreateDirectory(dumpDir);
                    var seq = Interlocked.Increment(ref _guestImageDumpSequence);
                    var path = Path.Combine(
                        dumpDir,
                        $"present-{seq:D4}-{_extent.Width}x{_extent.Height}-{_swapchainFormat}.bgra");
                    File.WriteAllBytes(path, bytes.ToArray());
                    Console.Error.WriteLine($"[LOADER][TRACE] vk.swapchain_dump path={path}");
					// Continuous readback is intentionally opt-in: each 1080p frame
					// is several megabytes and synchronously waits for the GPU.
					if (string.Equals(
							Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_CONTINUOUS"),
							"1",
							StringComparison.Ordinal))
					{
						_tracedPresentedSwapchain = false;
					}
                }
            }
            finally
            {
                _vk.UnmapMemory(_device, _stagingMemory);
            }
        }

        private static readonly long _swapchainDumpInterval = ParseSwapchainDumpInterval();

        private static long SwapchainDumpInterval() => _swapchainDumpInterval;

        private static long ParseSwapchainDumpInterval()
        {
            var raw = Environment.GetEnvironmentVariable("SHARPEMU_SWAPCHAIN_DUMP_EVERY");
            return long.TryParse(raw, out var interval) && interval > 0 ? interval : 0;
        }

        private static byte[] ScaleBgra(byte[] source, uint sourceWidth, uint sourceHeight, uint width, uint height)
        {
            var destination = new byte[checked((int)(width * height * 4))];
            for (uint y = 0; y < height; y++)
            {
                var sourceY = (uint)(((ulong)y * sourceHeight) / height);
                for (uint x = 0; x < width; x++)
                {
                    var sourceX = (uint)(((ulong)x * sourceWidth) / width);
                    var sourceOffset = checked((int)(((ulong)sourceY * sourceWidth + sourceX) * 4));
                    var destinationOffset = checked((int)(((ulong)y * width + x) * 4));
                    source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
                }
            }

            return destination;
        }
        // Captures the display surface through the store in queue order; presentation reads the copy.
        // A flip snapshot is created and retired for every flip; on macOS that is a
        // vkCreateImage plus a vkAllocateMemory (and a Metal texture) about twenty
        // times a second for the same 1920x1080 target. Keep the retired ones and
        // hand them back: a snapshot is fully overwritten by the copy that follows
        // (its barrier takes it from Undefined), so its old contents and layout do
        // not matter.
        // The command-stream thread takes snapshots and the render thread retires
        // them, so the pool is guarded.
        private readonly Dictionary<(Format Format, uint Width, uint Height), Stack<GuestImageResource>>
            _flipSnapshotPool = new();
        private readonly object _flipSnapshotPoolGate = new();
        private int _pooledFlipSnapshots;
        // SHARPEMU_FLIP_SNAPSHOT_POOL=0 turns the pool off; it is on otherwise.
        private bool _flipSnapshotPoolOpen =
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_FLIP_SNAPSHOT_POOL"),
                "0",
                StringComparison.Ordinal);
        private const int MaxPooledFlipSnapshots = 8;

        private bool TryTakePooledFlipSnapshot(
            Format format,
            uint width,
            uint height,
            ulong address,
            long version,
            out GuestImageResource snapshot)
        {
            lock (_flipSnapshotPoolGate)
            {
                snapshot = null!;
                if (!_flipSnapshotPoolOpen ||
                    !_flipSnapshotPool.TryGetValue((format, width, height), out var available) ||
                    available.Count == 0)
                {
                    return false;
                }

                snapshot = available.Pop();
                _pooledFlipSnapshots--;
            }

            snapshot.Address = address;
            snapshot.FlipVersion = version;
            return true;
        }

        private bool TryPoolFlipSnapshot(GuestImageResource resource)
        {
            lock (_flipSnapshotPoolGate)
            {
                if (!_flipSnapshotPoolOpen || resource.Image.Handle == 0 || resource.Memory.Handle == 0 ||
                    _pooledFlipSnapshots >= MaxPooledFlipSnapshots)
                {
                    return false;
                }

                var key = (resource.Format, resource.Width, resource.Height);
                if (!_flipSnapshotPool.TryGetValue(key, out var available))
                {
                    available = new Stack<GuestImageResource>();
                    _flipSnapshotPool[key] = available;
                }

                resource.Address = 0;
                resource.FlipVersion = 0;
                available.Push(resource);
                _pooledFlipSnapshots++;
                return true;
            }
        }

        // Closes the pool and hands every kept snapshot back for a real destroy.
        private void DrainFlipSnapshotPool()
        {
            List<GuestImageResource> kept = [];
            lock (_flipSnapshotPoolGate)
            {
                _flipSnapshotPoolOpen = false;
                foreach (var available in _flipSnapshotPool.Values)
                {
                    kept.AddRange(available);
                }

                _flipSnapshotPool.Clear();
                _pooledFlipSnapshots = 0;
            }

            foreach (var resource in kept)
            {
                DestroyGuestImage(resource);
            }
        }

        private GuestImageResource CreateGuestFlipSnapshot(Format format, uint width, uint height, ulong address, long version)
        {
            if (TryTakePooledFlipSnapshot(format, width, height, address, version, out var pooled))
            {
                SetDebugName(ObjectType.Image, pooled.Image.Handle, $"guest flip v{version} source 0x{address:X16}");
                return pooled;
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(flip snapshot)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory memory = default;
            try
            {
                Check(_deviceInfo.AllocateMemory(allocationInfo, out memory), "vkAllocateMemory(flip snapshot)");
                Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(flip snapshot)");
            }
            catch
            {
                _deviceInfo.FreeMemory(memory);
                _vk.DestroyImage(_device, image, null);
                throw;
            }

            SetDebugName(ObjectType.Image, image.Handle, $"guest flip v{version} source 0x{address:X16}");
            return new GuestImageResource
            {
                Address = address,
                FlipVersion = version,
                Width = width,
                Height = height,
                Format = format,
                Image = image,
                Memory = memory,
            };
        }

        // Wait for the GPU to complete the slot's last frame before reuse.
        private void WaitFrameSlot(int slot)
        {
            if (_frameInFlight.Length <= slot || !_frameInFlight[slot])
            {
                if (_frameGuestImageVersions.Length > slot &&
                    _frameGuestImageVersions[slot] is { } unsubmittedVersion)
                {
                    _frameGuestImageVersions[slot] = null;
                    DestroyGuestImage(unsubmittedVersion);
                    FlipProgressTracker.RecordFlip(unsubmittedVersion.FlipVersion);
                    TraceVulkanShader(
                        $"vk.flip_retired version={unsubmittedVersion.FlipVersion} " +
                        $"frame_slot={slot} reason=frame-not-submitted");
                }
                return;
            }

            _scheduler.Wait(_frameTimelines[slot]);
            _frameInFlight[slot] = false;
            if (_frameTimelines[slot] > _completedTimeline)
            {
                _completedTimeline = _frameTimelines[slot];
            }

            if (_frameGuestImageVersions[slot] is { } guestImageVersion)
            {
                _frameGuestImageVersions[slot] = null;
                DestroyGuestImage(guestImageVersion);
                FlipProgressTracker.RecordFlip(guestImageVersion.FlipVersion);
                TraceVulkanShader(
                    $"vk.flip_retired version={guestImageVersion.FlipVersion} " +
                    $"frame_slot={slot} timeline={_frameTimelines[slot]}");
            }

            ProcessDeferredTextureDestroys();
        }

        private void WaitAllFrameSlots()
        {
            for (var slot = 0; slot < _frameInFlight.Length; slot++)
            {
                WaitFrameSlot(slot);
            }
        }

        private void CollectAbandonedGuestImageVersions()
        {
            using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageVersions);
            if (_guestImageVersions.Count == 0)
            {
                return;
            }

            HashSet<long> referencedVersions;
            lock (_gate)
            {
                referencedVersions = _pendingGuestImagePresentations
                    .Select(static presentation => presentation.GuestImageVersion)
                    .Where(static version => version != 0)
                    .ToHashSet();
                if (_latestPresentation is { GuestImageVersion: not 0 } latest)
                {
                    referencedVersions.Add(latest.GuestImageVersion);
                }
            }

            foreach (var entry in _guestImageVersions.ToArray())
            {
                if (referencedVersions.Contains(entry.Key))
                {
                    continue;
                }

                _guestImageVersions.Remove(entry.Key);
                _deferredGuestImageVersionDestroys.Enqueue(
                    (entry.Value, _submitTimeline));
                TraceVulkanShader(
                    $"vk.flip_retire_deferred version={entry.Key} " +
                    $"timeline={_submitTimeline} reason=presentation-dropped");
            }
        }

        private void DrainFrameSlots()
        {
            WaitAllFrameSlots();
            _scheduler.Wait(_submitTimeline);
            _completedTimeline = _submitTimeline;
            ProcessDeferredTextureDestroys();
        }

    }
}

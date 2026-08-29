// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
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
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex]
                    ? _hdrOutputActive
                        ? AccessFlags.ShaderReadBit
                        : AccessFlags.MemoryReadBit
                    : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                _imageInitialized[imageIndex]
                    ? _hdrOutputActive
                        ? PipelineStageFlags.FragmentShaderBit
                        : PipelineStageFlags.BottomOfPipeBit
                    : PipelineStageFlags.TopOfPipeBit,
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

            var toFinalLayout = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = _hdrOutputActive
                    ? AccessFlags.ShaderReadBit
                    : AccessFlags.MemoryReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = PresentationTargetFinalLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
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
        private Image _presentEncodeImage;
        private DeviceMemory _presentEncodeMemory;
        private Extent2D _presentEncodeExtent;

        private bool TryGetPresentEncodeImage(out Image encodeImage)
        {
            encodeImage = default;
            var encodeFormat = GetSrgbCounterpart(_swapchainFormat);
            if (encodeFormat == Format.Undefined)
            {
                return false;
            }

            if (_presentEncodeImage.Handle != 0 &&
                (_presentEncodeExtent.Width != _extent.Width ||
                 _presentEncodeExtent.Height != _extent.Height))
            {
                DestroyPresentEncodeImage();
            }

            if (_presentEncodeImage.Handle == 0)
            {
                var imageInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = encodeFormat,
                    Extent = new Extent3D(_extent.Width, _extent.Height, 1),
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.TransferDstBit |
                            ImageUsageFlags.TransferSrcBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined,
                };
                Check(
                    _vk.CreateImage(_device, &imageInfo, null, out _presentEncodeImage),
                    "vkCreateImage(present encode)");
                _vk.GetImageMemoryRequirements(
                    _device,
                    _presentEncodeImage,
                    out var requirements);
                var allocationInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = FindMemoryType(
                        requirements.MemoryTypeBits,
                        MemoryPropertyFlags.DeviceLocalBit),
                };
                Check(
                    _vk.AllocateMemory(_device, &allocationInfo, null, out _presentEncodeMemory),
                    "vkAllocateMemory(present encode)");
                Check(
                    _vk.BindImageMemory(_device, _presentEncodeImage, _presentEncodeMemory, 0),
                    "vkBindImageMemory(present encode)");
                _presentEncodeExtent = new Extent2D(_extent.Width, _extent.Height);
                SetDebugName(
                    ObjectType.Image,
                    _presentEncodeImage.Handle,
                    "SharpEmu present sRGB-encode image");
            }

            encodeImage = _presentEncodeImage;
            return true;
        }

        private void DestroyPresentEncodeImage()
        {
            if (_presentEncodeImage.Handle != 0)
            {
                _vk.DestroyImage(_device, _presentEncodeImage, null);
                _presentEncodeImage = default;
            }

            if (_presentEncodeMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _presentEncodeMemory, null);
                _presentEncodeMemory = default;
            }

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

            var sourceToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                // An offscreen target is last written as a color attachment,
                // then put in ShaderReadOnlyOptimal for later sampling.  A
                // layout-only handoff to ShaderRead does not make that write
                // visible to this transfer when no shader sample occurs in
                // between.  NVIDIA's Linux driver exposed the resulting stale
                // (usually black) image while Windows drivers happened to
                // tolerate it.  Include all preceding writes before blitting
                // the image into the swapchain.
                SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex]
                    ? _hdrOutputActive
                        ? AccessFlags.ShaderReadBit
                        : AccessFlags.MemoryReadBit
                    : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _imageInitialized[imageIndex]
                    ? PresentationTargetFinalLayout
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = presentationTarget,
                SubresourceRange = ColorSubresourceRange(),
            };
            // Linear-float flips need a linear->sRGB encode on the way to a
            // UNORM swapchain; sRGB (or unknown-counterpart) swapchains keep
            // the direct blit.
            var encodeForPresent = false;
            Image encodeImage = default;
            if (IsLinearFloatPresentSource(source.Format) &&
                GetSrgbCounterpart(_swapchainFormat) != Format.Undefined)
            {
                encodeForPresent = TryGetPresentEncodeImage(out encodeImage);
            }

            var encodeToTransferDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _presentEncodeImage,
                SubresourceRange = ColorSubresourceRange(),
            };
            var barriers = stackalloc ImageMemoryBarrier[3];
            barriers[0] = sourceToTransfer;
            barriers[1] = destinationToTransfer;
            barriers[2] = encodeToTransferDst;
            _vk.CmdPipelineBarrier(
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
                    presentationTarget,
                    ImageLayout.TransferDstOptimal,
                    &clearColor,
                    1,
                    &clearRange);
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
                CanCopyEncodedSrgbPresentSource(source.Format, _swapchainFormat) &&
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
                var encodeToTransferSrc = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = encodeImage,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
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

                // Raw same-class copy keeps the sRGB-encoded bytes unchanged
                // while landing them in the UNORM swapchain image.
                var encodedCopy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(_extent.Width, _extent.Height, 1),
                };
                _vk.CmdCopyImage(
                    _commandBuffer,
                    encodeImage,
                    ImageLayout.TransferSrcOptimal,
                    _swapchainImages[imageIndex],
                    ImageLayout.TransferDstOptimal,
                    1,
                    &encodedCopy);
            }

            if (traceDestination)
            {
                var destinationToReadback = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = presentationTarget,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
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

            var sourceToShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToFinalLayout = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = traceDestination
                    ? AccessFlags.TransferReadBit
                    : AccessFlags.TransferWriteBit,
                DstAccessMask = _hdrOutputActive
                    ? AccessFlags.ShaderReadBit
                    : AccessFlags.MemoryReadBit,
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
            _vk.CmdPipelineBarrier(
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
        private void ExecuteOrderedGuestFlip(VulkanOrderedGuestFlip work)
        {
            Agc.AgcExports.MarkAllSurfacesCleared();
            FlushBatchedGuestCommands();
            _guestImages.TryGetValue(work.Address, out var source);
            if (_deviceLost ||
                source is null ||
                !source.Initialized)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.flip_capture_failed version={work.Version} " +
                    $"queue={_activeGuestQueue.Name} addr=0x{work.Address:X16} " +
                    $"found={(source is not null)} initialized={(source?.Initialized ?? false)}");
                MarkGuestFlipVersionSafe(work);
                return;
            }

            EnsureGuestSubmissionCapacity();
            var snapshot = CreateGuestFlipSnapshot(source, work.Version);
            var commandBuffer = AllocateGuestCommandBuffer();
            var submitted = false;
            try
            {
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(flip capture)");

                var barriers = stackalloc ImageMemoryBarrier[2];
                barriers[0] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderReadBit |
                                    AccessFlags.ShaderWriteBit |
                                    AccessFlags.ColorAttachmentWriteBit |
                                    AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = source.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                barriers[1] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    2,
                    barriers);

                var copy = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(
                        ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(source.Width, source.Height, 1),
                };
                _vk.CmdCopyImage(
                    commandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    snapshot.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copy);

                barriers[0] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderReadBit |
                                    AccessFlags.ShaderWriteBit |
                                    AccessFlags.ColorAttachmentWriteBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = source.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                barriers[1] = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = snapshot.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.AllCommandsBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    2,
                    barriers);

                Check(
                    _vk.EndCommandBuffer(commandBuffer),
                    "vkEndCommandBuffer(flip capture)");
                SubmitGuestCommandBuffer(commandBuffer, [], []);
                submitted = true;
                snapshot.Initialized = true;
                _guestImageVersions.Add(work.Version, snapshot);
                MarkGuestFlipVersionSafe(work);

                lock (_gate)
                {
                    var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
                    var isHdr =
                        VideoOutExports.TryGetDisplayBufferInfo(
                            work.VideoOutHandle,
                            work.DisplayBufferIndex,
                            out var displayBuffer) &&
                        VideoOutExports.IsHdrPixelFormat(displayBuffer.PixelFormat);
                    var presentation = new Presentation(
                        null,
                        work.Width,
                        work.Height,
                        sequence,
                        GuestDrawKind.None,
                        TranslatedDraw: null,
                        RequiredGuestWorkSequence: _activeGuestWorkSequence,
                        IsSplash: false,
                        GuestImageAddress: work.Address,
                        GuestImageVersion: work.Version,
                        IsHdr: isHdr);
                    _latestPresentation = presentation;
                    _pendingGuestImagePresentations.Enqueue(presentation);
                    while (_pendingGuestImagePresentations.Count > MaxPendingGuestFlipVersions)
                    {
                        _pendingGuestImagePresentations.Dequeue();
                    }
                }

                CollectAbandonedGuestImageVersions();

                var effectivePitch = work.PitchInPixel == 0
                    ? work.Width
                    : work.PitchInPixel;
                TraceVulkanShader(
                    $"vk.flip_capture version={work.Version} " +
                    $"queue={_activeGuestQueue.Name} submission={_activeGuestQueue.SubmissionId} " +
                    $"work_sequence={_activeGuestWorkSequence} addr=0x{work.Address:X16} " +
                    $"size={work.Width}x{work.Height} pitch={effectivePitch}");
                RenderDocCapture.OnGuestFlipBoundary(work.Version);
            }
            finally
            {
                if (!submitted)
                {
                    MarkGuestFlipVersionSafe(work);
                    ReleaseGuestCommandBuffer(commandBuffer);
                    DestroyGuestImage(snapshot);
                }
            }
        }

        private bool TryExecuteOrderedGuestFlipWait(VulkanOrderedGuestFlipWait work)
        {
            var safe = _guestFlipCompletion.IsSafe(
                work.VideoOutHandle,
                work.DisplayBufferIndex,
                work.Version);
            TraceVulkanShader(
                $"vk.flip_wait_safe version={work.Version} " +
                $"queue={_activeGuestQueue.Name} submission={_activeGuestQueue.SubmissionId} " +
                $"handle={work.VideoOutHandle} index={work.DisplayBufferIndex} " +
                $"capture_complete={(safe ? 1 : 0)}");
            if (!safe && !_loggedFlipWaitOrderViolation)
            {
                _loggedFlipWaitOrderViolation = true;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] vk.flip_wait_order version={work.Version} " +
                    $"handle={work.VideoOutHandle} index={work.DisplayBufferIndex} " +
                    "executed before its flip capture; deferring.");
            }

            return safe;
        }

        private void MarkGuestFlipVersionSafe(VulkanOrderedGuestFlip work)
        {
            _guestFlipCompletion.MarkSafe(
                work.VideoOutHandle,
                work.DisplayBufferIndex,
                work.Version);
        }

        private bool _loggedFlipWaitOrderViolation;

        private GuestImageResource CreateGuestFlipSnapshot(
            GuestImageResource source,
            long version)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = source.Format,
                Extent = new Extent3D(source.Width, source.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferSrcBit |
                        ImageUsageFlags.TransferDstBit |
                        ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(flip snapshot)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory memory = default;
            try
            {
                Check(
                    _vk.AllocateMemory(_device, &allocationInfo, null, out memory),
                    "vkAllocateMemory(flip snapshot)");
                Check(
                    _vk.BindImageMemory(_device, image, memory, 0),
                    "vkBindImageMemory(flip snapshot)");
            }
            catch
            {
                if (memory.Handle != 0)
                {
                    _vk.FreeMemory(_device, memory, null);
                }
                _vk.DestroyImage(_device, image, null);
                throw;
            }

            SetDebugName(
                ObjectType.Image,
                image.Handle,
                $"guest flip v{version} source 0x{source.Address:X16}");
            return new GuestImageResource
            {
                Address = source.Address,
                FlipVersion = version,
                Width = source.Width,
                Height = source.Height,
                LogicalWidth = source.LogicalWidth,
                LogicalHeight = source.LogicalHeight,
                MipLevels = 1,
                TileMode = source.TileMode,
                GuestFormat = source.GuestFormat,
                Format = source.Format,
                Image = image,
                Memory = memory,
            };
        }

        private void WaitFrameSlot(int slot) => TryWaitFrameSlot(slot, ulong.MaxValue);

        // Returns false when the slot's fence is still unsignaled after
        // timeoutNs (the GPU is behind, e.g. a slow-compute backlog). Callers
        // on the macOS main thread must NOT wait forever here or the Cocoa
        // event pump stalls and the window goes "Not Responding" (F1 overlay /
        // close stop working). A bounded wait lets Render() skip the frame and
        // return to the pump; the fence still signals later and the frame is
        // retried.
        private bool TryWaitFrameSlot(int slot, ulong timeoutNs)
        {
            if (_frameFencePending.Length <= slot || !_frameFencePending[slot])
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
                return true;
            }

            var fence = _frameFences[slot];
            var waitResult = _vk.WaitForFences(_device, 1, &fence, true, timeoutNs);
            if (waitResult == Result.Timeout)
            {
                return false;
            }

            Check(waitResult, "vkWaitForFences(frame)");
            Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences(frame)");
            _frameFencePending[slot] = false;
            if (_frameTimelines[slot] > _completedTimeline)
            {
                _completedTimeline = _frameTimelines[slot];
            }

            if (_frameTranslatedResources[slot] is { } translated)
            {
                _frameTranslatedResources[slot] = null;
                DestroyTranslatedDrawResources(translated);
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
            return true;
        }

        private void WaitAllFrameSlots()
        {
            for (var slot = 0; slot < _frameFencePending.Length; slot++)
            {
                WaitFrameSlot(slot);
            }
        }

        private void CollectAbandonedGuestImageVersions()
        {
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

        // Used on teardown paths that already drained the queue (device
        // wait-idle): releases per-slot state without touching fences that
        // were never submitted.
        private void DrainFrameSlots()
        {
            WaitAllFrameSlots();
            _completedTimeline = _submitTimeline;
            ProcessDeferredTextureDestroys();
        }

    }
}

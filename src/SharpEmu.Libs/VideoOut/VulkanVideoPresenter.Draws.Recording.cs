// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial records translated guest draw commands.

        private void RecordTranslatedDraw(uint imageIndex, TranslatedDrawResources resources)
        {
            BeginDebugLabel(_commandBuffer, "SharpEmu swapchain draw");
            RecordGlobalBufferVisibilityBarrier(
                _commandBuffer,
                resources,
                PipelineStageFlags.VertexShaderBit |
                PipelineStageFlags.FragmentShaderBit);
            RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
            RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);
            RecordTranslatedGraphicsPass(
                resources,
                _renderPass,
                _framebuffers[imageIndex],
                _extent);
            MarkGlobalBufferShaderWrites(resources);
            RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
            EndDebugLabel(_commandBuffer);
        }

        private void RecordGuestImageForSampling(
            GuestImageResource guestImage,
            PipelineStageFlags shaderStage)
        {
            if (guestImage.Initialized || guestImage.InitialUploadPending)
            {
                return;
            }

            var range = ColorSubresourceRange(0, Math.Max(guestImage.MipLevels, 1));
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = guestImage.Image,
                SubresourceRange = range,
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var clearValue = new ClearColorValue(0f, 0f, 0f, 0f);
            _vk.CmdClearColorImage(
                _commandBuffer,
                guestImage.Image,
                ImageLayout.TransferDstOptimal,
                &clearValue,
                1,
                &range);

            var toShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = guestImage.Image,
                SubresourceRange = range,
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                shaderStage,
                0,
                0,
                null,
                0,
                null,
                1,
                &toShaderRead);

            guestImage.Initialized = true;
        }

        private void RecordRenderTargetFeedbackSnapshots(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (texture.FeedbackSource is not { } source)
                {
                    continue;
                }

                // Initialize every destination mip to a deterministic zero.
                // Render-target writes currently populate mip 0; leaving the
                // remaining sampled mips undefined turns guest LOD selection
                // into driver-dependent colored garbage.
                var destinationToTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToTransfer);

                var clearValue = new ClearColorValue(0f, 0f, 0f, 0f);
                // Avoid overlapping a clear and copy on mip 0: without an
                // intervening dependency two transfer writes to one
                // subresource are not ordered merely because they were
                // recorded in that order. Initialized sources overwrite mip
                // 0 directly and clear only the otherwise undefined tail.
                var clearBaseMip = source.Initialized ? 1u : 0u;
                if (clearBaseMip < source.MipLevels)
                {
                    var destinationRange = ColorSubresourceRange(
                        clearBaseMip,
                        source.MipLevels - clearBaseMip);
                    _vk.CmdClearColorImage(
                        _commandBuffer,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        &clearValue,
                        1,
                        &destinationRange);
                }

                if (source.Initialized)
                {
                    var sourceToTransfer = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.ShaderReadBit,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                        NewLayout = ImageLayout.TransferSrcOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = source.Image,
                        // Only mip 0 has defined render-target contents.
                        SubresourceRange = ColorSubresourceRange(),
                    };
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        shaderStage |
                        PipelineStageFlags.ColorAttachmentOutputBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToTransfer);

                    var copy = new ImageCopy
                    {
                        SrcSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.ColorBit,
                            0,
                            0,
                            1),
                        DstSubresource = new ImageSubresourceLayers(
                            ImageAspectFlags.ColorBit,
                            0,
                            0,
                            1),
                        Extent = new Extent3D(source.Width, source.Height, 1),
                    };
                    _vk.CmdCopyImage(
                        _commandBuffer,
                        source.Image,
                        ImageLayout.TransferSrcOptimal,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copy);
                    RecordFeedbackSnapshotCopy(
                        source.Format,
                        source.Width,
                        source.Height);
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
                    _vk.CmdPipelineBarrier(
                        _commandBuffer,
                        PipelineStageFlags.TransferBit,
                        shaderStage,
                        0,
                        0,
                        null,
                        0,
                        null,
                        1,
                        &sourceToShaderRead);
                }

                var destinationToShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(0, source.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToShaderRead);

                TraceVulkanShader(
                    $"vk.feedback_snapshot_copy addr=0x{source.Address:X16} " +
                    $"size={source.Width}x{source.Height} initialized={source.Initialized}");
            }
        }

        private static void MarkSampledImagesInitialized(
            TranslatedDrawResources resources)
        {
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.NeedsUpload ||
                        texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                    if (texture.UpdatesCpuContent)
                    {
                        guestImage.CpuContentFingerprint = texture.CpuContentFingerprint;
                        if (texture.WriteGeneration >= 0)
                        {
                            _cpuBackedUploadGenerations[guestImage.Address] =
                                texture.WriteGeneration;
                        }
                    }
                }
            }
        }

        private bool ShouldTraceGuestImageContents(
            GuestImageResource image,
            ulong shaderAddress = 0)
        {
            if (image.Address == 0)
            {
                return false;
            }

            if (_traceGuestImageShaderFilterEnabled &&
                !AddressListContains(
                    "SHARPEMU_TRACE_GUEST_IMAGE_SHADER_ADDRS",
                    shaderAddress))
            {
                return false;
            }

            if ((_traceGuestImageWidth > 0 && image.Width != _traceGuestImageWidth) ||
                (_traceGuestImageHeight > 0 && image.Height != _traceGuestImageHeight))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_traceGuestImageFormat) &&
                (!Enum.TryParse<Format>(
                        _traceGuestImageFormat,
                        ignoreCase: true,
                        out var expectedFormat) ||
                 image.Format != expectedFormat))
            {
                return false;
            }

            var addressMatched = ShouldTraceGuestImageAddressForDiagnostics(image.Address);
            if (addressMatched && _traceGuestImageOccurrence > 0)
            {
                var count = _guestImageTraceCounts.TryGetValue(image.Address, out var previous)
                    ? previous + 1
                    : 1;
                _guestImageTraceCounts[image.Address] = count;
                return count == _traceGuestImageOccurrence;
            }

            var broadTrace =
                ShouldTraceGuestImageContentsForDiagnostics() &&
                image.Width >= 1280 &&
                image.Height >= 720;
            if (GuestImageTraceInterval() is { } interval)
            {
                var addressFilter = Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS");
                if (!string.IsNullOrWhiteSpace(addressFilter) && !addressMatched)
                {
                    return false;
                }

                if (image.Width < 1280 || image.Height < 720)
                {
                    return false;
                }

                _globalGuestImageDrawCount++;
                if (_globalGuestImageDrawCount < GuestImageTraceStartAfter() ||
                    _intervalReadbackCount > 3000)
                {
                    return false;
                }

                var count = _guestImageTraceCounts.TryGetValue(image.Address, out var previous)
                    ? previous + 1
                    : 1;
                _guestImageTraceCounts[image.Address] = count;
                return count % interval == 0;
            }

            return (addressMatched || broadTrace) &&
                   _tracedGuestImageContents.Add(image.Address);
        }

        private readonly Dictionary<ulong, long> _guestImageTraceCounts = new();
        private long _globalGuestImageDrawCount;
        private long _intervalReadbackCount;

        private static long? _cachedGuestImageTraceInterval = long.MinValue;
        private static long _cachedGuestImageTraceStartAfter;

        // SHARPEMU_TRACE_GUEST_IMAGES=every:N[@M] — read back 1280x720+ guest
        // images every Nth draw into each, starting after M total such draws.
        private static long? GuestImageTraceInterval()
        {
            if (_cachedGuestImageTraceInterval == long.MinValue)
            {
                var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
                long? interval = null;
                if (mode is not null && mode.StartsWith("every:", StringComparison.Ordinal))
                {
                    var spec = mode["every:".Length..];
                    var at = spec.IndexOf('@');
                    var intervalText = at < 0 ? spec : spec[..at];
                    if (long.TryParse(intervalText, out var parsed) && parsed > 0)
                    {
                        interval = parsed;
                    }

                    if (at >= 0 && long.TryParse(spec[(at + 1)..], out var after) && after > 0)
                    {
                        _cachedGuestImageTraceStartAfter = after;
                    }
                }

                _cachedGuestImageTraceInterval = interval;
            }

            return _cachedGuestImageTraceInterval;
        }

        private static long GuestImageTraceStartAfter()
        {
            _ = GuestImageTraceInterval();
            return _cachedGuestImageTraceStartAfter;
        }

        // Diagnostics toggles are read once: these run per draw / per cached
        // texture hit, and env lookups plus string parsing are far too
        // expensive there (and non-trivially so under Rosetta 2).
        private static readonly bool _vulkanValidationEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_VK_VALIDATION"),
                "1",
                StringComparison.Ordinal);
        // Object names and command labels are useful in RenderDoc and validation
        // captures, but formatting them and calling the debug-utils driver hooks
        // for every draw is measurable overhead in normal gameplay.
        private static readonly bool _vulkanDebugUtilsEnabled =
            _vulkanValidationEnabled ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_VK_DEBUG_LABELS"),
                "1",
                StringComparison.Ordinal);
        private static readonly string? _traceGuestImagesMode =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        private static readonly bool _traceGuestImagesEnabled =
            string.Equals(_traceGuestImagesMode, "1", StringComparison.Ordinal);
        private static readonly bool _tracePresentedGuestImagesEnabled =
            _traceGuestImagesEnabled ||
            string.Equals(_traceGuestImagesMode, "present", StringComparison.OrdinalIgnoreCase);
        private static readonly bool _traceVulkanResourcesEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_VK_RESOURCES"),
                "1",
                StringComparison.Ordinal);
        private static readonly bool _traceVulkanShaderEnabled =
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
                "1",
                StringComparison.Ordinal) ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
                "1",
                StringComparison.Ordinal);
        private static readonly long _traceGuestImageOccurrence =
            long.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_OCCURRENCE"),
                out var traceGuestImageOccurrence) &&
            traceGuestImageOccurrence > 0
                ? traceGuestImageOccurrence
                : 0;
        private static readonly uint _traceGuestImageWidth =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_WIDTH"),
                out var traceGuestImageWidth)
                ? traceGuestImageWidth
                : 0;
        private static readonly uint _traceGuestImageHeight =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_HEIGHT"),
                out var traceGuestImageHeight)
                ? traceGuestImageHeight
                : 0;
        private static readonly string? _traceGuestImageFormat =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_FORMAT");
        private static readonly long _tracePresentedGuestImageOccurrence =
            long.TryParse(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_OCCURRENCE"),
                out var tracePresentedGuestImageOccurrence) &&
            tracePresentedGuestImageOccurrence > 0
                ? tracePresentedGuestImageOccurrence
                : 0;
        private static readonly bool _traceGuestImageShaderFilterEnabled =
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_GUEST_IMAGE_SHADER_ADDRS"));
        private static readonly bool _traceGuestImageAddressFilterEnabled =
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS"));
        private static readonly double _renderResolutionScale =
            double.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_RENDER_SCALE"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var renderResolutionScale) &&
            renderResolutionScale > 0 &&
            renderResolutionScale <= 2.0
                ? renderResolutionScale
                : 1.0;

        private static uint ScaleGuestDimension(uint value) =>
            _renderResolutionScale == 1.0
                ? value
                : Math.Max(1u, (uint)Math.Round(value * _renderResolutionScale));

        private static readonly bool _forceFullscreenPipeline =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_FULLSCREEN_PIPELINE") == "1";
        private static readonly bool _forceFullscreenVertex =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_FULLSCREEN_VERTEX") == "1";
        private static readonly bool _forceTitleFullscreenVertex =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_FULLSCREEN_VERTEX") == "1";
        private static readonly bool _forceDefaultRasterState =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_DEFAULT_RASTER_STATE") == "1";
        private static readonly bool _forceTitleDefaultRasterState =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DEFAULT_RASTER_STATE") == "1";
        private static readonly bool _forceTitleSolidFragment =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_SOLID_FRAGMENT") == "1";
        private static readonly bool _forceSolidFragment =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_SOLID_FRAGMENT") == "1";
        private static readonly uint? _forceAttributeFragmentLocation =
            uint.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_FORCE_ATTRIBUTE_FRAGMENT"),
                out var forceAttributeFragmentLocation)
                    ? forceAttributeFragmentLocation
                    : null;
        private static readonly string? _fixedFragmentDumpPath =
            Environment.GetEnvironmentVariable("SHARPEMU_DUMP_FIXED_SOLID_FRAGMENT");
        private static readonly bool _chunkedDrawsEnabled =
            Environment.GetEnvironmentVariable("SHARPEMU_ENABLE_CHUNKED_DRAWS") == "1";
        private static readonly string? _traceGuestWritesMode =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_WRITES");
        private static readonly long _traceGuestWriteOrdinal =
            long.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_WRITE_ORDINAL"),
                out var traceGuestWriteOrdinal)
                    ? traceGuestWriteOrdinal
                    : 0;
        private static readonly long _traceLargeGuestWriteOrdinal =
            ParseTraceLargeGuestWriteOrdinal(_traceGuestWritesMode);
        private static readonly int _tracePixelSpirvBytes =
            int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SPIRV_BYTES"),
                out var tracePixelSpirvBytes)
                    ? tracePixelSpirvBytes
                    : 0;
        private static readonly int _tracePixelSpirvOccurrence =
            int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SPIRV_OCCURRENCE"),
                out var tracePixelSpirvOccurrence)
                    ? Math.Max(tracePixelSpirvOccurrence, 1)
                    : 1;
        private static readonly bool _traceTitleDrawEnabled =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_DRAW") == "1";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            (bool Wildcard, ulong[] Addresses)> _cachedAddressLists = new();

        private static long ParseTraceLargeGuestWriteOrdinal(string? mode)
        {
            return mode is not null &&
                mode.StartsWith("large@", StringComparison.Ordinal) &&
                long.TryParse(mode.AsSpan("large@".Length), out var ordinal) &&
                ordinal > 0
                    ? ordinal
                    : 0;
        }

        private static bool ShouldTraceGuestImageContentsForDiagnostics() =>
            _traceGuestImagesEnabled;

        private static bool ShouldTraceGuestImageAddressForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS",
                address);
        }

        private static bool ShouldTraceGuestImageStateForDiagnostics(
            GuestImageResource image)
        {
            if (_traceGuestImageAddressFilterEnabled)
            {
                return ShouldTraceGuestImageAddressForDiagnostics(image.Address);
            }

            var hasShapeFilter = _traceGuestImageWidth != 0 ||
                _traceGuestImageHeight != 0 ||
                !string.IsNullOrWhiteSpace(_traceGuestImageFormat);
            if (!hasShapeFilter ||
                (_traceGuestImageWidth != 0 && image.Width != _traceGuestImageWidth) ||
                (_traceGuestImageHeight != 0 && image.Height != _traceGuestImageHeight))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(_traceGuestImageFormat) ||
                (Enum.TryParse<Format>(
                    _traceGuestImageFormat,
                    ignoreCase: true,
                    out var expectedFormat) &&
                 image.Format == expectedFormat);
        }

        private static bool ShouldTraceGuestImageWriteForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_WRITES",
                address);
        }

        private static bool AddressListContains(
            string environmentVariable,
            ulong address)
        {
            var (wildcard, addresses) = _cachedAddressLists.GetOrAdd(
                environmentVariable,
                static name => ParseAddressList(Environment.GetEnvironmentVariable(name)));
            return wildcard || Array.IndexOf(addresses, address) >= 0;
        }

        private static (bool Wildcard, ulong[] Addresses) ParseAddressList(string? addresses)
        {
            if (string.IsNullOrWhiteSpace(addresses))
            {
                return (false, []);
            }

            var parsedAddresses = new List<ulong>();
            foreach (var token in addresses.Split(
                         [',', ';', ' ', '\t'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token == "*")
                {
                    return (true, []);
                }

                var span = token.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(
                        span,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    parsedAddresses.Add(parsed);
                }
            }

            return (false, parsedAddresses.ToArray());
        }

        private static bool ShouldTracePresentedGuestImageContentsForDiagnostics() =>
            _tracePresentedGuestImagesEnabled;

        private bool ShouldTraceAddressedPresentedGuestImage(GuestImageResource image)
        {
            if (!AddressListContains(
                    "SHARPEMU_TRACE_PRESENTED_GUEST_IMAGE_ADDRS",
                    image.Address))
            {
                return false;
            }

            var count = _presentedGuestImageTraceCounts.TryGetValue(
                image.Address,
                out var previous)
                ? previous + 1
                : 1;
            _presentedGuestImageTraceCounts[image.Address] = count;
            return _tracePresentedGuestImageOccurrence == 0
                ? count == 1
                : count == _tracePresentedGuestImageOccurrence;
        }

        private static bool ShouldTraceVulkanResources() =>
            _traceVulkanResourcesEnabled;

        private void RecordTranslatedGraphicsPass(
            TranslatedDrawResources resources,
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent)
        {
            BeginTranslatedRenderPass(renderPass, framebuffer, extent);
            RecordTranslatedDrawInPass(resources, extent);
            _vk.CmdEndRenderPass(_commandBuffer);
        }

        private void BeginTranslatedRenderPass(
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent,
            int colorAttachmentCount = 1,
            bool hasDepthAttachment = false,
            float clearDepth = 1f,
            byte clearStencil = 0,
            ClearColorValue[]? colorClearValues = null)
        {
            colorAttachmentCount = Math.Max(colorAttachmentCount, 1);
            var clearValueCount = colorAttachmentCount + (hasDepthAttachment ? 1 : 0);
            var clearValues = stackalloc ClearValue[clearValueCount];
            for (var index = 0; index < colorAttachmentCount; index++)
            {
                clearValues[index] = colorClearValues is not null &&
                    index < colorClearValues.Length
                        ? new ClearValue { Color = colorClearValues[index] }
                        : default;
            }
            // Reverse-Z is not assumed; clear depth to 1.0 (far) so a standard
            // LessOrEqual/Less test keeps the nearest fragment.
            if (hasDepthAttachment)
            {
                clearValues[colorAttachmentCount] = new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue(
                        clearDepth,
                        clearStencil),
                };
            }
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), extent),
                ClearValueCount = (uint)clearValueCount,
                PClearValues = clearValues,
            };
            _vk.CmdBeginRenderPass(
                _commandBuffer,
                &renderPassInfo,
                SubpassContents.Inline);
        }

        private void RecordTranslatedDrawInPass(
            TranslatedDrawResources resources,
            Extent2D extent)
        {
            _vk.CmdBindPipeline(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                resources.Pipeline);
            if (resources.DescriptorSet.Handle != 0)
            {
                var descriptorSet = resources.DescriptorSet;
                _vk.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    resources.PipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);
            }

            var drawScissor = ClampScissor(resources.Scissor, extent);
            if (drawScissor.Width == 0 || drawScissor.Height == 0)
            {
                return;
            }

            var drawViewport = ClampViewport(resources.Viewport, extent);
            if (ViewportDebugEpsilon != 0f)
            {
                drawViewport.X += ViewportDebugEpsilon;
                drawViewport.Y += ViewportDebugEpsilon;
            }
            _vk.CmdSetViewport(_commandBuffer, 0, 1, &drawViewport);
            // CB_BLEND_RED..ALPHA feed the CONSTANT_COLOR/CONSTANT_ALPHA factors.
            var blendConstants = stackalloc float[4]
            {
                resources.BlendConstant.Red,
                resources.BlendConstant.Green,
                resources.BlendConstant.Blue,
                resources.BlendConstant.Alpha,
            };
            _vk.CmdSetBlendConstants(_commandBuffer, blendConstants);
            if (resources.Depth.StencilTestEnable)
            {
                var front = resources.Depth.StencilFront;
                var back = resources.Depth.StencilBack;
                _vk.CmdSetStencilCompareMask(
                    _commandBuffer,
                    StencilFaceFlags.FaceFrontBit,
                    front.CompareMask);
                _vk.CmdSetStencilCompareMask(
                    _commandBuffer,
                    StencilFaceFlags.FaceBackBit,
                    back.CompareMask);
                _vk.CmdSetStencilWriteMask(
                    _commandBuffer,
                    StencilFaceFlags.FaceFrontBit,
                    front.WriteMask);
                _vk.CmdSetStencilWriteMask(
                    _commandBuffer,
                    StencilFaceFlags.FaceBackBit,
                    back.WriteMask);
                _vk.CmdSetStencilReference(
                    _commandBuffer,
                    StencilFaceFlags.FaceFrontBit,
                    front.Reference);
                _vk.CmdSetStencilReference(
                    _commandBuffer,
                    StencilFaceFlags.FaceBackBit,
                    back.Reference);
            }
            if (resources.VertexBuffers.Length != 0)
            {
                var buffers = stackalloc VkBuffer[resources.VertexBuffers.Length];
                var offsets = stackalloc ulong[resources.VertexBuffers.Length];
                var handles = stackalloc ulong[resources.VertexBuffers.Length];
                var perInstance = stackalloc bool[resources.VertexBuffers.Length];
                var sourceIndices = stackalloc int[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    handles[index] = resources.VertexBuffers[index].Buffer.Handle;
                    perInstance[index] = resources.VertexBuffers[index].PerInstance;
                }

                var bindingCount = VulkanVertexBindingPlanner.BuildUniqueSourceIndices(
                    new ReadOnlySpan<ulong>(handles, resources.VertexBuffers.Length),
                    new ReadOnlySpan<bool>(perInstance, resources.VertexBuffers.Length),
                    new Span<int>(sourceIndices, resources.VertexBuffers.Length));
                for (var bindingIndex = 0; bindingIndex < bindingCount; bindingIndex++)
                {
                    var sourceIndex = sourceIndices[bindingIndex];
                    buffers[bindingIndex] = resources.VertexBuffers[sourceIndex].Buffer;
                    // The pipeline attribute already contains OffsetBytes.
                    offsets[bindingIndex] = 0;
                }

                _vk.CmdBindVertexBuffers(
                    _commandBuffer,
                    0,
                    (uint)bindingCount,
                    buffers,
                    offsets);
            }

            // Replaying a full-screen primitive once per 512x512 scissor tile
            // multiplies an ordinary 4K composite into 32 complete draws. On
            // MoltenVK this starves the render thread and makes the guest fall
            // behind its own flip queue. Vulkan clips a normal fullscreen draw
            // efficiently; keep tiling only as an explicit driver diagnostic.
            var maxPixelsPerDraw = _chunkedDrawsEnabled
                ? 512u * 512u
                : uint.MaxValue;
            var rowsPerDraw = Math.Max(
                1u,
                Math.Min(drawScissor.Height, maxPixelsPerDraw / Math.Max(drawScissor.Width, 1u)));
            var drawCount = 0u;
            for (var y = 0u; y < drawScissor.Height; y += rowsPerDraw)
            {
                var scissor = new Rect2D(
                    new Offset2D(
                        drawScissor.X,
                        checked(drawScissor.Y + (int)y)),
                    new Extent2D(
                        drawScissor.Width,
                        Math.Min(rowsPerDraw, drawScissor.Height - y)));
                _vk.CmdSetScissor(_commandBuffer, 0, 1, &scissor);

                if (resources.IndexBuffer.Handle != 0)
                {
                    _vk.CmdBindIndexBuffer(
                        _commandBuffer,
                        resources.IndexBuffer,
                        0,
                        resources.Index32Bit ? IndexType.Uint32 : IndexType.Uint16);
                    // vertexOffset = ResolveVertexOffset(GE_INDX_OFFSET).
                    // GTA UI glyphs use relative indices + a nonzero base vertex.
                    _vk.CmdDrawIndexed(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        0,
                        resources.BaseVertex,
                        0);
                }
                else
                {
                    _vk.CmdDraw(
                        _commandBuffer,
                        resources.VertexCount,
                        resources.InstanceCount,
                        (uint)Math.Max(resources.BaseVertex, 0),
                        0);
                }

                drawCount++;
            }

            if (drawCount > 1)
            {
                TraceVulkanShader(
                    $"vk.graphics_chunked target={extent.Width}x{extent.Height} " +
                    $"draws={drawCount} rows={rowsPerDraw} " +
                    $"scissor={drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height} " +
                    $"viewport={drawViewport.X:0.###},{drawViewport.Y:0.###}," +
                    $"{drawViewport.Width:0.###}x{drawViewport.Height:0.###} " +
                    $"name={resources.DebugName}");
            }
        }

        private void DestroyTranslatedDrawResources(TranslatedDrawResources resources)
        {
            if (resources.TransientFramebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resources.TransientFramebuffer, null);
            }

            if (resources.TransientRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resources.TransientRenderPass, null);
            }

            foreach (var texture in resources.Textures)
            {
                if (texture is null || texture.Cached)
                {
                    continue;
                }

                if (texture.FeedbackSnapshotKey is not null)
                {
                    RetireFeedbackSnapshot(texture);
                    continue;
                }

                if (texture.OwnsStorage && texture.View.Handle != 0)
                {
                    _vk.DestroyImageView(_device, texture.View, null);
                }

                if (texture.OwnsStorage && texture.Image.Handle != 0)
                {
                    _vk.DestroyImage(_device, texture.Image, null);
                }

                if (texture.OwnsStorage && texture.ImageMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.ImageMemory, null);
                }

                if (texture.StagingBuffer.Handle != 0)
                {
                    RecycleHostBuffer(texture.StagingBuffer, texture.StagingMemory);
                }

                if (texture.NeedsUpload &&
                    texture.GuestImage is { Initialized: false } guestImage)
                {
                    guestImage.InitialUploadPending = false;
                }
            }

            foreach (var (buffer, memory) in resources.DeferredTextureStagingBuffers)
            {
                if (buffer.Handle != 0)
                {
                    RecycleHostBuffer(buffer, memory);
                }
            }
            resources.DeferredTextureStagingBuffers.Clear();

            foreach (var globalBuffer in resources.GlobalMemoryBuffers)
            {
                if (globalBuffer is null || globalBuffer.Allocation is not null)
                {
                    continue;
                }

                RecycleHostBuffer(globalBuffer.Buffer, globalBuffer.Memory);
            }

            foreach (var vertexBuffer in resources.VertexBuffers)
            {
                if (vertexBuffer is null || !vertexBuffer.OwnsBuffer)
                {
                    continue;
                }

                RecycleHostBuffer(vertexBuffer.Buffer, vertexBuffer.Memory);
            }

            RecycleHostBuffer(resources.IndexBuffer, resources.IndexMemory);

            if (!resources.PipelineCached && resources.Pipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, resources.Pipeline, null);
            }

            if (resources.DescriptorPool.Handle != 0)
            {
                if (_recycledDescriptorPools.Count < 256)
                {
                    _recycledDescriptorPools.Push(resources.DescriptorPool);
                }
                else
                {
                    _vk.DestroyDescriptorPool(_device, resources.DescriptorPool, null);
                }
            }

            if (!resources.DescriptorLayoutCached &&
                resources.PipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, resources.PipelineLayout, null);
            }

            if (!resources.DescriptorLayoutCached &&
                resources.DescriptorSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(_device, resources.DescriptorSetLayout, null);
            }
        }
    }
}

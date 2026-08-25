// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns sampled-texture construction and upload recording.

    private static readonly bool _retireCachedTextureStaging = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_RETIRE_CACHED_TEXTURE_STAGING"),
        "0",
        StringComparison.Ordinal);

    private sealed partial class Presenter
    {
        // GPU deswizzle (default on; SHARPEMU_GPU_DETILE=0 forces the CPU path).
        // Lazily built on the first tiled texture; disposed with the presenter.
        private static readonly bool _gpuDetileEnabled = !string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_GPU_DETILE"), "0", StringComparison.Ordinal);
        private static readonly bool _gpuDetileLog = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_GPU_DETILE"), "1", StringComparison.Ordinal);
        private VulkanDetilePass? _detilePass;
        private long _gpuDetileCount;

        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureUploads = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint Format)> _dumpedTextures = new();
        private readonly Dictionary<
            (ulong Address, uint Width, uint Height, uint Format),
            (ulong Hash, long Version)> _tracedTextureUploadContents = new();

        private sealed class TextureResource
        {
            public ulong Address;
            public VkBuffer StagingBuffer;
            public DeviceMemory StagingMemory;
            public Image Image;
            public DeviceMemory ImageMemory;
            public ImageView View;
            public uint Width;
            public uint Height;
            public uint Depth = 1;
            public uint Type = Gen5TextureType2D;
            public uint RowLength;
            public uint DstSelect;
            public uint Layers = 1;
            public uint MipLevel;
            public uint MipLevels = 1;
            public GuestTextureMipUpload[]? MipUploads;
            public bool NeedsUpload;
            public bool RefreshesExistingImage;
            public bool OwnsStorage;
            public bool IsStorage;
            public bool Cached;
            public bool IsHostMovie;
            public int HostMoviePlane = -1;
            public long HostMovieFrameSerial;
            public ulong CpuContentFingerprint;
            public bool UpdatesCpuContent;
            public GuestSampler SamplerState;
            public Sampler Sampler;
            public GuestImageResource? GuestImage;
            public GuestDepthResource? GuestDepth;
            // Write-tracker generation of the guest memory the staged pixels
            // were read from; -1 when unknown. Recorded on the guest image
            // after upload so stale-content skips can be detected.
            public long WriteGeneration = -1;
            // A sampled render-target alias cannot remain bound to the same
            // image while that image is a color attachment. The per-draw
            // snapshot uses this source to copy the target's pre-draw contents
            // before the render pass begins. The snapshot itself is owned by
            // this TextureResource and retires with the draw fence.
            public GuestImageResource? FeedbackSource;
            public GuestDepthResource? DepthFeedbackSource;
            public bool ReadOnlyDepthFeedback;
            public VulkanFeedbackSnapshotKey? FeedbackSnapshotKey;
            public ulong FeedbackAllocationBytes;
        }

        private static string TextureDebugName(GuestDrawTexture texture, Format format) =>
            $"SharpEmu texture 0x{texture.Address:X16} {texture.Width}x{texture.Height} " +
            $"fmt{texture.Format}/{format}";

        private VulkanDetilePass EnsureDetilePass() =>
            _detilePass ??= new VulkanDetilePass(
                _vk, _device, _queue, _physicalDevice, _queueFamilyIndex);

        private TextureResource CreateTextureResource(GuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var depth = GetGuestTextureDepth(texture.Type, texture.Depth);
            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);

            var layers = IsGuestTexture3D(texture.Type)
                ? 1u
                : Math.Max(texture.ArrayLayers, 1);
            var mipLevels = texture.MipUploads is { Length: > 0 }
                ? Math.Max(texture.ResourceMipLevels, 1)
                : 1u;
            var expectedSize = GetTextureByteCount(
                texture.Format,
                rowLength,
                height,
                depth);
            if (ShouldTraceVulkanResources() &&
                _tracedTextureUploads.Add((texture.Address, width, height, vkFormat)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.texture addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} vk={vkFormat} " +
                    $"size={width}x{height}x{depth} type={texture.Type} " +
                    $"row={rowLength} tile={texture.TileMode} layers={layers} " +
                    $"view_mips={texture.BaseMipLevel}+{texture.MipLevels} " +
                    $"resource_mips={texture.ResourceMipLevels} upload_mips={texture.MipUploads?.Length ?? 1} " +
                    $"dst=0x{texture.DstSelect:X3} " +
                    $"bytes={texture.RgbaPixels.Length} expected={expectedSize}");
            }
            // The GPU detile pass deswizzles plain 2D and array textures (one
            // dispatch-Z layer per slice) at 4/8/16 bpp, including block-compressed
            // formats (element grid = ceil(texels/4), smaller than the texel grid).
            // Validate against the element grid + bpp from the resolved params, and
            // require the tiled source to cover every layer's linear extent (tiled
            // slices are >= the linear size due to whole-block padding).
            DetileParams? gpuDetileParams = null;
            byte[]? gpuTiledSource = null;
            if (_gpuDetileEnabled &&
                texture.Detile is { } detileCandidate &&
                texture.TiledSource is { Length: > 0 } tiledCandidate &&
                VulkanDetilePass.Supports(detileCandidate) &&
                !IsGuestTexture3D(texture.Type) &&
                detileCandidate.ElementsWide > 0 &&
                detileCandidate.ElementsHigh > 0 &&
                (long)tiledCandidate.Length >=
                    (long)detileCandidate.ElementsWide * detileCandidate.ElementsHigh *
                    detileCandidate.BytesPerElement * layers &&
                tiledCandidate.Length % (int)(layers * (uint)detileCandidate.BytesPerElement) == 0)
            {
                gpuDetileParams = detileCandidate;
                gpuTiledSource = tiledCandidate;
            }

            VkBuffer stagingBuffer = default;
            DeviceMemory stagingMemory = default;
            ulong contentFingerprint;
            if (gpuTiledSource is { } gpuSource)
            {
                // GPU detile: no CPU staging; the compute pass writes the image directly.
                contentFingerprint = ComputeTextureContentFingerprint(gpuSource);
            }
            else
            {
                // Safety net: the AGC gate can package a texture as a GPU-detile
                // candidate (empty RgbaPixels + TiledSource) that this path did
                // not accept for the GPU compute pass (see the gpuTiledSource
                // guard above). Detile the raw tiled bytes on the CPU here rather
                // than letting empty RgbaPixels fall through to a blank fallback
                // image — otherwise such textures render empty (missing text).
                var cpuDetiled = texture.RgbaPixels;
                if (cpuDetiled.Length == 0 &&
                    layers == 1 &&
                    texture.TiledSource is { Length: > 0 } fallbackTiled &&
                    texture.Detile is { } fallbackParams &&
                    expectedSize > 0 &&
                    expectedSize <= int.MaxValue)
                {
                    var linear = new byte[expectedSize];
                    if (GnmTiling.TryDetile(
                            fallbackTiled,
                            linear,
                            texture.TileMode,
                            fallbackParams.ElementsWide,
                            fallbackParams.ElementsHigh,
                            fallbackParams.BytesPerElement))
                    {
                        cpuDetiled = linear;
                    }
                }

                var expectedUploadSize = texture.MipUploads is { Length: > 0 } mipUploads
                    ? GetMipUploadByteCount(texture.Format, mipUploads, layers)
                    : expectedSize * layers;
                var pixels = expectedUploadSize <= int.MaxValue &&
                    cpuDetiled.Length == (int)expectedUploadSize
                    ? cpuDetiled
                    : CreateFallbackTexturePixels(texture.Format, rowLength, height, expectedSize);
                if (!ReferenceEquals(pixels, texture.RgbaPixels))
                {
                    layers = 1;
                }
                if (AddressListContains("SHARPEMU_FORCE_WHITE_TEXTURE_TARGETS", texture.Address))
                {
                    pixels = pixels.ToArray();
                    pixels.AsSpan().Fill(0xFF);
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.texture_force_white addr=0x{texture.Address:X16} " +
                        $"size={width}x{height} bytes={pixels.Length}");
                }
                DumpTextureUpload(texture, pixels, rowLength, width, height);
                TraceTextureUploadContents(
                    texture,
                    pixels,
                    rowLength,
                    width,
                    height,
                    vkFormat,
                    "create");
                var uploadPixels = texture.Format == 13
                    ? ExpandRgb32Pixels(pixels)
                    : pixels;
                contentFingerprint = ComputeTextureContentFingerprint(pixels);
                (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                    uploadPixels,
                    $"{TextureDebugName(texture, vkFormat)} staging");
            }

            var supportsMutableUsage = !IsBlockCompressedFormat(vkFormat);
            var supportsAttachmentUsage =
                supportsMutableUsage &&
                !IsGuestTexture3D(texture.Type);
            var supportsStorageUsage =
                supportsMutableUsage &&
                SupportsStorageImage(vkFormat);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = supportsMutableUsage
                    ? ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit
                    : 0,
                ImageType = GetGuestTextureImageType(texture.Type),
                Format = vkFormat,
                Extent = new Extent3D(width, height, depth),
                MipLevels = mipLevels,
                ArrayLayers = layers,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = supportsMutableUsage
                    ? ImageUsageFlags.TransferDstBit |
                      ImageUsageFlags.SampledBit |
                      (supportsAttachmentUsage
                          ? ImageUsageFlags.ColorAttachmentBit
                          : (ImageUsageFlags)0) |
                      (supportsStorageUsage ? ImageUsageFlags.StorageBit : (ImageUsageFlags)0) |
                      ImageUsageFlags.TransferSrcBit
                    : ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(texture)");
            _vk.GetImageMemoryRequirements(_device, image, out var imageRequirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = imageRequirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    imageRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var imageMemory), "vkAllocateMemory(texture)");
            Check(_vk.BindImageMemory(_device, image, imageMemory, 0), "vkBindImageMemory(texture)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = GetGuestTextureViewType(
                    texture.Type,
                    texture.ArrayedView),
                Format = vkFormat,
                Components = ToVkComponentMapping(texture.DstSelect),
                SubresourceRange = ColorSubresourceRange(
                    texture.BaseMipLevel,
                    Math.Min(texture.MipLevels, mipLevels - texture.BaseMipLevel),
                    layers),
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "vkCreateImageView(texture)");
            var debugName = TextureDebugName(texture, vkFormat);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");

            // GPU detile: record the deswizzle into the shared batch command buffer
            // (async — never a blocking submit on the render thread), leaving the
            // image ShaderReadOnly before the draw that samples it. The transient
            // buffers + descriptor pool retire with the batch fence. On any failure
            // fall back to a CPU detile + normal staged upload.
            var gpuDetiled = false;
            if (gpuTiledSource is { } detileSource && gpuDetileParams is { } detileParameters)
            {
                try
                {
                    var detileCommandBuffer = BeginBatchedGuestCommands();
                    CloseOpenTranslatedRenderPass();
                    if (EnsureDetilePass().RecordDetile(
                            detileCommandBuffer,
                            image,
                            ImageLayout.Undefined,
                            width,
                            height,
                            layers,
                            detileSource,
                            detileParameters,
                            out var detileTransients))
                    {
                        _batchRetireDetile.Add(detileTransients);
                        gpuDetiled = true;
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] GPU detile failed for addr=0x{texture.Address:X16}, " +
                        $"falling back to CPU: {exception.Message}");
                    gpuDetiled = false;
                }

                if (!gpuDetiled)
                {
                    // CPU fallback: the tiled source packs the array slices
                    // contiguously, so detile each slice into its layer-major
                    // linear region (single layer degrades to one iteration).
                    var totalLinear = checked((int)(expectedSize * layers));
                    var linear = new byte[totalLinear];
                    var sliceTiledBytes = detileSource.Length / (int)layers;
                    var sliceLinearBytes = (int)expectedSize;
                    var detiledAll = true;
                    for (var layer = 0; layer < layers; layer++)
                    {
                        // TryDetile iterates the element grid (for BC, ceil(texels/4)).
                        if (!GnmTiling.TryDetile(
                                detileSource.AsSpan(layer * sliceTiledBytes, sliceTiledBytes),
                                linear.AsSpan(layer * sliceLinearBytes, sliceLinearBytes),
                                texture.TileMode,
                                detileParameters.ElementsWide,
                                detileParameters.ElementsHigh,
                                detileParameters.BytesPerElement))
                        {
                            detiledAll = false;
                            break;
                        }
                    }

                    if (detiledAll)
                    {
                        (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                            linear,
                            $"{TextureDebugName(texture, vkFormat)} staging(cpu-fallback)");
                    }
                }
                else if (_gpuDetileLog && Interlocked.Increment(ref _gpuDetileCount) is 1 or 100 or 1000 or 10000)
                {
                    Console.Error.WriteLine(
                        $"[GPU-DETILE] active: {_gpuDetileCount} texture(s) detiled on GPU " +
                        $"(latest {width}x{height} mode {texture.TileMode}).");
                }
            }

            var resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = image,
                ImageMemory = imageMemory,
                View = view,
                Width = width,
                Height = height,
                Depth = depth,
                Type = texture.Type,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                Layers = layers,
                MipLevels = mipLevels,
                MipUploads = texture.MipUploads,
                NeedsUpload = !gpuDetiled,
                OwnsStorage = true,
                SamplerState = texture.Sampler,
                CpuContentFingerprint = contentFingerprint,
                UpdatesCpuContent = texture.Address != 0,
                WriteGeneration = texture.WriteGeneration,
            };

            if (texture.Address != 0 &&
                !texture.ArrayedView &&
                layers == 1 &&
                !gpuDetiled &&
                !_guestImages.ContainsKey(texture.Address))
            {
                var guestFormat = GetGuestTextureFormat(texture.Format, texture.NumberType);

                var canonicalView = view;
                if (texture.DstSelect != 0xFAC)
                {
                    var identityViewInfo = new ImageViewCreateInfo
                    {
                        SType = StructureType.ImageViewCreateInfo,
                        Image = image,
                        ViewType = GetGuestTextureViewType(
                            texture.Type,
                            texture.ArrayedView),
                        Format = vkFormat,
                        Components = ToVkComponentMapping(0xFAC),
                        SubresourceRange = ColorSubresourceRange(layerCount: layers),
                    };
                    Check(
                        _vk.CreateImageView(_device, &identityViewInfo, null, out canonicalView),
                        "vkCreateImageView(texture identity)");
                    SetDebugName(ObjectType.ImageView, canonicalView.Handle, $"{debugName} identity view");
                }

                var guestImage = new GuestImageResource
                {
                    Address = texture.Address,
                    Width = width,
                    Height = height,
                    Depth = depth,
                    Type = texture.Type,
                    LogicalWidth = width,
                    LogicalHeight = height,
                    LogicalDepth = depth,
                    MipLevels = 1,
                    TileMode = texture.TileMode,
                    GuestFormat = guestFormat,
                    Format = vkFormat,
                    Image = image,
                    Memory = imageMemory,
                    View = canonicalView,
                    InitialUploadPending = true,
                    IsCpuBacked = true,
                    CpuContentFingerprint = contentFingerprint,
                    SupportsStorageUsage = supportsStorageUsage,
                };
                _guestImages.Add(texture.Address, guestImage);
                resource.OwnsStorage = false;
                resource.GuestImage = guestImage;
                TrackCpuBackedGuestImage(guestImage);
                lock (_gate)
                {
                    if (guestFormat != 0)
                    {
                        _availableGuestImages[texture.Address] = guestFormat;
                    }

                    if (texture.WriteGeneration >= 0)
                    {
                        _cpuBackedUploadGenerations[texture.Address] =
                            texture.WriteGeneration;
                    }

                    _guestImageExtents[texture.Address] =
                        (width, height, expectedSize);
                }
            }

            return resource;
        }

        private (VkBuffer Buffer, DeviceMemory Memory) CreateTextureStagingBuffer(
            byte[] pixels,
            string debugName)
        {
            var buffer = CreateHostBuffer(
                pixels,
                BufferUsageFlags.TransferSrcBit,
                out var memory,
                out _);
            SetDebugName(ObjectType.Buffer, buffer.Handle, debugName);
            return (buffer, memory);
        }

        private static ulong GetMipUploadByteCount(
            uint format,
            IReadOnlyList<GuestTextureMipUpload> uploads,
            uint layers)
        {
            ulong byteCount = 0;
            foreach (var upload in uploads)
            {
                var mipBytes = GetTextureByteCount(
                    format,
                    upload.RowLength,
                    upload.Height);
                byteCount = Math.Max(
                    byteCount,
                    checked(upload.BufferOffset + mipBytes * layers));
            }

            return byteCount;
        }

        private void DumpTextureUpload(
            GuestDrawTexture texture,
            byte[] pixels,
            uint rowLength,
            uint width,
            uint height)
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("SHARPEMU_DUMP_TEXTURES"),
                    "1",
                    StringComparison.Ordinal) ||
                texture.IsFallback ||
                texture.IsStorage ||
                GetTextureBytesPerPixel(texture.Format) != 4 ||
                width == 0 ||
                height == 0 ||
                !_dumpedTextures.Add((texture.Address, width, height, texture.Format)))
            {
                return;
            }

            var rowBytes = checked((int)rowLength * 4);
            var visibleRowBytes = checked((int)width * 4);
            if (pixels.Length < checked(rowBytes * (int)height))
            {
                return;
            }

            var directory = Path.Combine(AppContext.BaseDirectory, "texture-dumps");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"tex-{texture.Address:X16}-{width}x{height}-fmt{texture.Format}-row{rowLength}.bmp");
            WriteRgbaBmp(path, pixels, rowBytes, visibleRowBytes, (int)width, (int)height);
        }

        private void TraceTextureUploadContents(
            GuestDrawTexture texture,
            byte[] pixels,
            uint rowLength,
            uint width,
            uint height,
            Format format,
            string source)
        {
            if (!_traceGuestImageAddressFilterEnabled ||
                !AddressListContains("SHARPEMU_TRACE_GUEST_IMAGE_ADDRS", texture.Address))
            {
                return;
            }

            var key = (texture.Address, width, height, texture.Format);
            var hash = ComputeTextureContentFingerprint(pixels);
            var version = 1L;
            if (_tracedTextureUploadContents.TryGetValue(key, out var previous))
            {
                if (previous.Hash == hash)
                {
                    return;
                }

                version = previous.Version + 1;
            }
            _tracedTextureUploadContents[key] = (hash, version);

            var bytesPerPixel = checked((uint)GetTextureBytesPerPixel(texture.Format));
            var nonzeroBytes = 0L;
            foreach (var value in pixels)
            {
                nonzeroBytes += value == 0 ? 0 : 1;
            }

            var centerOffset = checked(
                ((int)(height / 2) * (int)rowLength + (int)(width / 2)) *
                (int)bytesPerPixel);
            var center = centerOffset + bytesPerPixel <= pixels.Length
                ? Convert.ToHexString(
                    pixels.AsSpan(centerOffset, checked((int)bytesPerPixel)))
                : "out-of-range";
            Console.Error.WriteLine(
                "[LOADER][TRACE] " +
                $"vk.texture_upload_contents addr=0x{texture.Address:X16} " +
                $"version={version} source={source} " +
                $"size={width}x{height} row={rowLength} format={format} " +
                $"guest_format={texture.Format} nonzero_bytes={nonzeroBytes}/{pixels.Length} " +
                $"nonblack_pixels={CountNonblackPixels(pixels, format, bytesPerPixel)}/{(ulong)rowLength * height} " +
                $"center={center} sample_unique={CountSampledUniquePixels(pixels, bytesPerPixel)} " +
                $"hash=0x{hash:X16}");

            var directory =
                Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var sequence = Interlocked.Increment(ref _guestImageDumpSequence);
            var path = Path.Combine(
                directory,
                $"{sequence:D4}-texture-0x{texture.Address:X16}-{width}x{height}-" +
                $"row{rowLength}-fmt{texture.Format}-{format}.rgba");
            File.WriteAllBytes(path, pixels);
        }

        private static void WriteRgbaBmp(
            string path,
            byte[] rgba,
            int sourceRowBytes,
            int visibleRowBytes,
            int width,
            int height)
        {
            const int fileHeaderSize = 14;
            const int infoHeaderSize = 40;
            const int bytesPerPixel = 4;
            var pixelBytes = checked(width * height * bytesPerPixel);
            var fileSize = fileHeaderSize + infoHeaderSize + pixelBytes;
            var output = new byte[fileSize];

            output[0] = (byte)'B';
            output[1] = (byte)'M';
            WriteUInt32(output, 2, (uint)fileSize);
            WriteUInt32(output, 10, fileHeaderSize + infoHeaderSize);
            WriteUInt32(output, 14, infoHeaderSize);
            WriteInt32(output, 18, width);
            WriteInt32(output, 22, -height);
            WriteUInt16(output, 26, 1);
            WriteUInt16(output, 28, 32);
            WriteUInt32(output, 34, (uint)pixelBytes);

            var destinationOffset = fileHeaderSize + infoHeaderSize;
            for (var y = 0; y < height; y++)
            {
                var sourceOffset = y * sourceRowBytes;
                for (var x = 0; x < visibleRowBytes; x += bytesPerPixel)
                {
                    var destination = destinationOffset + y * visibleRowBytes + x;
                    output[destination + 0] = rgba[sourceOffset + x + 2];
                    output[destination + 1] = rgba[sourceOffset + x + 1];
                    output[destination + 2] = rgba[sourceOffset + x + 0];
                    output[destination + 3] = rgba[sourceOffset + x + 3];
                }
            }

            File.WriteAllBytes(path, output);
        }

        private static byte[] CreateFallbackTexturePixels(uint format, uint width, uint height, ulong expectedSize)
        {
            if (format is 9 or 10)
            {
                var pixels = new byte[checked((int)expectedSize)];
                for (var offset = 3; offset < pixels.Length; offset += 4)
                {
                    pixels[offset] = 0xFF;
                }

                return pixels;
            }

            return new byte[checked((int)expectedSize)];
        }

        private void RecordTextureUploads(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (!texture.ReadOnlyDepthFeedback &&
                    texture.GuestDepth is { } depth)
                {
                    RecordGuestDepthForSampling(depth, shaderStage);
                }

                if (!texture.IsStorage && texture.GuestImage is { } sampledGuestImage)
                {
                    RecordGuestImageForSampling(sampledGuestImage, shaderStage);
                }

                if (!texture.NeedsUpload)
                {
                    continue;
                }

                var hostMovieImageInitialized = texture.HostMoviePlane switch
                {
                    0 => _hostMovieImageInitialized,
                    1 => _hostMovieChromaImageInitialized,
                    _ => false,
                };
                var uploadBaseMip = texture.MipUploads is { Length: > 0 }
                    ? 0u
                    : texture.MipLevel;
                var uploadMipLevels = texture.MipUploads is { Length: > 0 }
                    ? texture.MipLevels
                    : 1u;
                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = texture.RefreshesExistingImage ||
                        texture.IsHostMovie && hostMovieImageInitialized
                        ? AccessFlags.ShaderReadBit
                        : 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = texture.RefreshesExistingImage ||
                        texture.IsHostMovie && hostMovieImageInitialized
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(
                        uploadBaseMip,
                        uploadMipLevels,
                        texture.Layers),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    texture.RefreshesExistingImage ||
                        texture.IsHostMovie && hostMovieImageInitialized
                        ? PipelineStageFlags.AllCommandsBit
                        : PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                if (texture.MipUploads is { Length: > 0 } mipUploads)
                {
                    var copyRegions = new BufferImageCopy[mipUploads.Length];
                    for (var mip = 0; mip < mipUploads.Length; mip++)
                    {
                        var upload = mipUploads[mip];
                        copyRegions[mip] = new BufferImageCopy
                        {
                            BufferOffset = upload.BufferOffset,
                            BufferRowLength = upload.RowLength > upload.Width
                                ? upload.RowLength
                                : 0,
                            ImageSubresource = new ImageSubresourceLayers
                            {
                                AspectMask = ImageAspectFlags.ColorBit,
                                MipLevel = upload.MipLevel,
                                LayerCount = texture.Layers,
                            },
                            ImageExtent = new Extent3D(
                                upload.Width,
                                upload.Height,
                                texture.Depth),
                        };
                    }

                    fixed (BufferImageCopy* copyRegionPointer = copyRegions)
                    {
                        _vk.CmdCopyBufferToImage(
                            _commandBuffer,
                            texture.StagingBuffer,
                            texture.Image,
                            ImageLayout.TransferDstOptimal,
                            (uint)copyRegions.Length,
                            copyRegionPointer);
                    }
                }
                else
                {
                    var copyRegion = new BufferImageCopy
                    {
                        BufferRowLength = texture.RowLength > texture.Width
                            ? texture.RowLength
                            : 0,
                        ImageSubresource = new ImageSubresourceLayers
                        {
                            AspectMask = ImageAspectFlags.ColorBit,
                            MipLevel = texture.MipLevel,
                            LayerCount = texture.Layers,
                        },
                        ImageExtent = new Extent3D(
                            texture.Width,
                            texture.Height,
                            texture.Depth),
                    };
                    _vk.CmdCopyBufferToImage(
                        _commandBuffer,
                        texture.StagingBuffer,
                        texture.Image,
                        ImageLayout.TransferDstOptimal,
                        1,
                        &copyRegion);
                }

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(
                        uploadBaseMip,
                        uploadMipLevels,
                        texture.Layers),
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
                if (texture.Cached)
                {
                    // The queue executes command buffers in submission order,
                    // so once this upload is recorded every later draw can
                    // reuse the image without restaging it.
                    texture.NeedsUpload = false;
                }
                if (texture.IsHostMovie)
                {
                    if (texture.HostMoviePlane == 0)
                    {
                        _hostMovieImageInitialized = true;
                        _hostMovieLumaUploadedFrameSerial = texture.HostMovieFrameSerial;
                    }
                    else if (texture.HostMoviePlane == 1)
                    {
                        _hostMovieChromaImageInitialized = true;
                        _hostMovieChromaUploadedFrameSerial = texture.HostMovieFrameSerial;
                    }
                }

                if (_retireCachedTextureStaging &&
                    texture.Cached &&
                    texture.StagingBuffer.Handle != 0)
                {
                    // The upload command owns this staging allocation. Keep the
                    // staging allocation until the submission fence releases the
                    // translated resources. Do not keep the staging allocation for
                    // the life of the cached image.
                    resources.DeferredTextureStagingBuffers.Add(
                        (texture.StagingBuffer, texture.StagingMemory));
                    texture.StagingBuffer = default;
                    texture.StagingMemory = default;
                }
            }
        }
    }
}

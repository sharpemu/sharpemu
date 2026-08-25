// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns the creation and registry of guest color images.

    private static readonly Dictionary<ulong, uint> _availableGuestImages = new();
    private static readonly Dictionary<ulong, (uint Width, uint Height, ulong ByteCount)>
        _guestImageExtents = new();

    internal static bool TryGetGuestImageExtent(
        ulong address,
        out uint width,
        out uint height,
        out ulong byteCount)
    {
        lock (_gate)
        {
            if (_guestImageExtents.TryGetValue(address, out var extent))
            {
                (width, height, byteCount) = extent;
                return true;
            }
        }

        width = 0;
        height = 0;
        byteCount = 0;
        return false;
    }

    internal static IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents()
    {
        lock (_gate)
        {
            return _guestImageExtents
                .Select(entry => (
                    entry.Key,
                    entry.Value.Width,
                    entry.Value.Height,
                    entry.Value.ByteCount))
                .ToArray();
        }
    }

    internal static bool IsGuestImageAvailable(
        ulong address,
        uint format,
        uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_availableGuestImages.TryGetValue(address, out var availableFormat))
            {
                return false;
            }

            if (availableFormat == guestFormat)
            {
                return true;
            }

            return TryDecodeRenderTargetFormat(
                    (availableFormat >> 8) & 0x1FFu,
                    availableFormat & 0xFFu,
                    out var resident) &&
                TryDecodeRenderTargetFormat(format, numberType, out var requested) &&
                Presenter.IsCompatibleViewFormat(resident.Format, requested.Format);
        }
    }

    internal static void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        if (address == 0 || guestFormat == 0)
        {
            return;
        }

        lock (_gate)
        {
            _availableGuestImages[address] = guestFormat;
        }
    }

    internal static bool IsGpuGuestImageAvailable(
        ulong address,
        uint format,
        uint numberType) =>
        IsGuestImageAvailable(address, format, numberType);

    private sealed partial class Presenter
    {
        private readonly Dictionary<ulong, GuestImageResource> _guestImages = new();

        private static string GuestImageDebugName(GuestRenderTarget target, Format format) =>
            $"SharpEmu guest 0x{target.Address:X16} {target.Width}x{target.Height} " +
            $"fmt{target.Format}/{format} tile{target.TileMode}";

        private sealed class GuestImageResource
        {
            public ulong Address;
            public long FlipVersion;
            public uint Width;
            public uint Height;
            public uint Depth = 1;
            public uint Type = Gen5TextureType2D;
            // Unscaled guest-requested size; Width/Height are the physical (scaled) backing size.
            public uint LogicalWidth;
            public uint LogicalHeight;
            public uint LogicalDepth = 1;
            public uint MipLevels;
            public uint TileMode;
            public uint GuestFormat;
            public Format Format;
            public Image Image;
            public DeviceMemory Memory;
            public ImageView View;
            public ImageView[] MipViews = [];
            public Dictionary<(Format Format, uint MipLevel, uint LevelCount, uint DstSelect), ImageView> FormatViews { get; } = new();
            public RenderPass RenderPass;
            public RenderPass InitialRenderPass;
            public Framebuffer Framebuffer;
            public Dictionary<Format, ReinterpretedGuestImageViews> ReinterpretCache { get; } = new();
            public Dictionary<GuestDepthKey, DepthFramebufferResource> DepthFramebuffers { get; } = new();
            public bool Initialized;
            public bool InitialUploadPending;
            public bool IsCpuBacked;
            public ulong CpuContentFingerprint;
            public bool SupportsStorageUsage;
        }

        private readonly record struct ReinterpretedGuestImageViews(
            ImageView View,
            ImageView[] MipViews,
            RenderPass RenderPass,
            RenderPass InitialRenderPass,
            Framebuffer Framebuffer);

        private GuestImageResource GetOrCreateGuestImage(
            GuestRenderTarget target,
            Format format,
            bool requiresStorage = false,
            uint type = Gen5TextureType2D,
            uint depth = 1)
        {
            depth = GetGuestTextureDepth(type, depth);
            var supportsStorageUsage = SupportsStorageImage(format);
            if (requiresStorage && !supportsStorageUsage)
            {
                throw new InvalidOperationException(
                    $"Storage image format {format} is unsupported for guest " +
                    $"address 0x{target.Address:X16}.");
            }

            if (!supportsStorageUsage)
            {
                // sRGB targets must stay shareable with later UNORM
                // ImageLoad/Store aliases of the same surface. The image is
                // created with MUTABLE_FORMAT | EXTENDED_USAGE, so carrying
                // storage usage is legal as long as the view-compatible UNORM
                // counterpart supports storage; stores then go through the
                // UNORM alias view instead of recreating the image.
                var storageCounterpart = GetStorageImageFormat(format);
                supportsStorageUsage = storageCounterpart != format &&
                    IsCompatibleViewFormat(format, storageCounterpart) &&
                    SupportsStorageImage(storageCounterpart);
            }

            // Storage/UAV images keep native guest dimensions (compute shaders index them directly).
            var physicalWidth = requiresStorage
                ? target.Width
                : ScaleGuestDimension(target.Width);
            var physicalHeight = requiresStorage
                ? target.Height
                : ScaleGuestDimension(target.Height);
            var physicalDepth = depth;
            var mipLevels = ClampMipLevels(
                physicalWidth,
                physicalHeight,
                physicalDepth,
                target.MipLevels);
            var guestFormat = GetGuestTextureFormat(target.Format, target.NumberType);
            var requestedKey = new GuestImageVariantKey(
                target.Address,
                target.Width,
                target.Height,
                depth,
                type,
                mipLevels,
                target.TileMode,
                guestFormat,
                format);
            if (_guestImages.TryGetValue(target.Address, out var existing))
            {
                // View-compatible formats (sRGB vs UNORM of the same texel
                // layout) are the same guest surface accessed through
                // different number formats — render as sRGB, ImageLoad/Store
                // as UNORM is a standard PS5 pattern (AvPlayer movie copies,
                // post-process chains). Recreating the image for that case
                // ping-pongs content between two VkImages and every transition
                // loses the rendered pixels; the mutable-format image accepts
                // an alternate-format view instead. Aliasing is limited to
                // sRGB/UNORM counterparts: broader same-class reinterpretation
                // (e.g. R32Uint over R8G8B8A8Unorm) would attach pipelines
                // whose fragment output type no longer matches the attachment,
                // so those keep the recreate path.
                var exactFormatMatch =
                    existing.GuestFormat == guestFormat &&
                    existing.Format == format;
                if (existing.LogicalWidth == target.Width &&
                    existing.LogicalHeight == target.Height &&
                    existing.LogicalDepth == depth &&
                    existing.Type == type &&
                    existing.MipLevels == mipLevels &&
                    (!requiresStorage || existing.SupportsStorageUsage) &&
                    HasCompatibleGuestImageTileMode(
                        target.TileMode,
                        existing.TileMode) &&
                    (exactFormatMatch ||
                    IsAliasableGuestImageFormat(existing.Format, format)))
                {
                    existing.IsCpuBacked = false;
                    existing.CpuContentFingerprint = 0;
                    if (existing.RenderPass.Handle == 0 &&
                        !requiresStorage &&
                        !IsGuestTexture3D(type))
                    {
                        var attachmentView = existing.MipViews.Length > 0
                            ? existing.MipViews[0]
                            : existing.View;
                        var promoted = CreateRenderPassAndFramebuffer(
                            existing.Format,
                            attachmentView,
                            existing.Width,
                            existing.Height);
                        existing.RenderPass = promoted.RenderPass;
                        existing.InitialRenderPass = promoted.InitialRenderPass;
                        existing.Framebuffer = promoted.Framebuffer;
                        var promotedRenderPass = promoted.RenderPass;
                        var promotedInitialRenderPass = promoted.InitialRenderPass;
                        var promotedFramebuffer = promoted.Framebuffer;
                        var promotedName = GuestImageDebugName(target, format);
                        SetDebugName(ObjectType.RenderPass, promotedRenderPass.Handle, $"{promotedName} renderpass");
                        SetDebugName(ObjectType.RenderPass, promotedInitialRenderPass.Handle, $"{promotedName} initial-renderpass");
                        SetDebugName(ObjectType.Framebuffer, promotedFramebuffer.Handle, $"{promotedName} framebuffer");
                    }

                    return existing;
                }

                if (existing.Width == target.Width &&
                    existing.Height == target.Height &&
                    existing.MipLevels == mipLevels &&
                    (!requiresStorage || existing.SupportsStorageUsage) &&
                    HasCompatibleGuestImageTileMode(
                        target.TileMode,
                        existing.TileMode) &&
                    IsCompatibleViewFormat(existing.Format, format))
                {
                    if (_traceGuestImageEvents)
                    {
                        Console.Error.WriteLine(
                            $"[GIMG] reinterpret addr=0x{target.Address:X} " +
                            $"{existing.Format}->{format} {target.Width}x{target.Height} " +
                            $"initialized={existing.Initialized}");
                    }

                    ReinterpretGuestImageFormat(existing, format, !requiresStorage, target);
                    existing.GuestFormat = guestFormat;
                    existing.IsCpuBacked = false;
                    existing.CpuContentFingerprint = 0;
                    if (!requiresStorage && existing.RenderPass.Handle == 0)
                    {
                        var attachmentView = existing.MipViews.Length > 0
                            ? existing.MipViews[0]
                            : existing.View;
                        var promoted = CreateRenderPassAndFramebuffer(
                            existing.Format,
                            attachmentView,
                            existing.Width,
                            existing.Height);
                        existing.RenderPass = promoted.RenderPass;
                        existing.InitialRenderPass = promoted.InitialRenderPass;
                        existing.Framebuffer = promoted.Framebuffer;
                        var promotedName = GuestImageDebugName(target, format);
                        SetDebugName(ObjectType.RenderPass, promoted.RenderPass.Handle, $"{promotedName} renderpass");
                        SetDebugName(ObjectType.RenderPass, promoted.InitialRenderPass.Handle, $"{promotedName} initial-renderpass");
                        SetDebugName(ObjectType.Framebuffer, promoted.Framebuffer.Handle, $"{promotedName} framebuffer");
                    }

                    return existing;
                }

                if (_traceGuestImageEvents)
                {
                    Console.Error.WriteLine(
                        $"[GIMG] recreate addr=0x{target.Address:X} " +
                        $"old={existing.Width}x{existing.Height}/{existing.Format}/m{existing.MipLevels} " +
                        $"new={target.Width}x{target.Height}/{format}/m{mipLevels} " +
                        $"initialized={existing.Initialized}");
                }

                StoreGuestImageVariant(
                    new GuestImageVariantKey(
                        existing.Address,
                        existing.LogicalWidth,
                        existing.LogicalHeight,
                        existing.LogicalDepth,
                        existing.Type,
                        existing.MipLevels,
                        existing.TileMode,
                        existing.GuestFormat,
                        existing.Format),
                    existing);
                _guestImages.Remove(target.Address);
                lock (_gate)
                {
                    _availableGuestImages.Remove(target.Address);
                    _cpuBackedUploadGenerations.Remove(target.Address);
                    _guestImageExtents.Remove(target.Address);
                }

                // Address-filtered readback diagnostics should inspect the
                // replacement image too; a resized/reformatted allocation at
                // the same guest address is a different piece of evidence.
                _tracedGuestImageContents.Remove(target.Address);

                SharpEmu.HLE.GuestImageWriteTracker.UntrackProtected(target.Address);
            }

            if (_guestImageVariants.Remove(requestedKey, out var retained))
            {
                if (requiresStorage && !retained.SupportsStorageUsage)
                {
                    // Do not reuse retained image if it lacks required storage usage
                    DestroyGuestImage(retained);
                }
                else
                {
                    retained.IsCpuBacked = false;
                    retained.CpuContentFingerprint = 0;
                    _guestImages.Add(target.Address, retained);
                    var retainedByteCount = GetTextureByteCount(
                        target.Format,
                        target.Width,
                        target.Height,
                        depth);
                    lock (_gate)
                    {
                        _cpuBackedUploadGenerations.Remove(target.Address);
                        _guestImageExtents[target.Address] = (
                            target.Width,
                            target.Height,
                            retainedByteCount);
                    }

                    // Arm the exact extent the flip/acquire sync path would read
                    // back, budgeted by bytes rather than by resolution: the old
                    // 1920x1080 cap left every 4K surface permanently
                    // un-invalidated, so a guest CPU rewrite of one was never
                    // reflected and the sample served stale bytes.
                    if (ShouldTrackGuestImageWrites(retainedByteCount))
                    {
                        SharpEmu.HLE.GuestImageWriteTracker.Track(
                            target.Address,
                            retainedByteCount,
                            CurrentGuestWorkSequenceForDiagnostics,
                            "vulkan.render-target");
                    }

                    if (_traceGuestImageEvents)
                    {
                        Console.Error.WriteLine(
                            $"[GIMG] retained addr=0x{target.Address:X} " +
                            $"{target.Width}x{target.Height} fmt={format} " +
                            $"initialized={retained.Initialized}");
                    }

                    return retained;
                }
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags =
                    ImageCreateFlags.CreateMutableFormatBit |
                    ImageCreateFlags.CreateExtendedUsageBit,
                ImageType = GetGuestTextureImageType(type),
                Format = format,
                Extent = new Extent3D(physicalWidth, physicalHeight, physicalDepth),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    (IsGuestTexture3D(type)
                        ? (ImageUsageFlags)0
                        : ImageUsageFlags.ColorAttachmentBit) |
                    ImageUsageFlags.SampledBit |
                    (supportsStorageUsage ? ImageUsageFlags.StorageBit : (ImageUsageFlags)0) |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(offscreen)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &allocationInfo, null, out var memory),
                "vkAllocateMemory(offscreen)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(offscreen)");
            // Rendering and uploads only define the mips they touch; define the whole
            // chain once so full-chain sampled binds never read Undefined layout.
            TransitionNewGuestImageToSampled(image, mipLevels);

            var viewUsageInfo = new ImageViewUsageCreateInfo
            {
                SType = StructureType.ImageViewUsageCreateInfo,
                Usage = GetNonStorageGuestImageViewUsage(type),
            };
            var restrictViewUsage = supportsStorageUsage &&
                !SupportsStorageImage(format);
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                PNext = restrictViewUsage ? &viewUsageInfo : null,
                Image = image,
                ViewType = GetGuestTextureViewType(type),
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(0, mipLevels),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(offscreen)");

            var mipViews = new ImageView[mipLevels];
            for (uint mipLevel = 0; mipLevel < mipLevels; mipLevel++)
            {
                viewInfo.SubresourceRange = ColorSubresourceRange(mipLevel, 1);
                ImageView mipView;
                Check(
                    _vk.CreateImageView(
                        _device,
                        &viewInfo,
                        null,
                        out mipView),
                    "vkCreateImageView(offscreen mip)");
                mipViews[mipLevel] = mipView;
            }

            RenderPass renderPass = default;
            RenderPass initialRenderPass = default;
            Framebuffer framebuffer = default;
            if (!requiresStorage && !IsGuestTexture3D(type))
            {
                (renderPass, initialRenderPass, framebuffer) =
                    CreateRenderPassAndFramebuffer(
                        format,
                        mipViews[0],
                        physicalWidth,
                        physicalHeight);
            }

            var resource = new GuestImageResource
            {
                Address = target.Address,
                Width = physicalWidth,
                Height = physicalHeight,
                Depth = physicalDepth,
                Type = type,
                LogicalWidth = target.Width,
                LogicalHeight = target.Height,
                LogicalDepth = depth,
                MipLevels = mipLevels,
                TileMode = target.TileMode,
                GuestFormat = guestFormat,
                Format = format,
                Image = image,
                Memory = memory,
                View = view,
                MipViews = mipViews,
                RenderPass = renderPass,
                InitialRenderPass = initialRenderPass,
                Framebuffer = framebuffer,
                SupportsStorageUsage = supportsStorageUsage,
            };
            var debugName = GuestImageDebugName(target, format);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            for (var mipLevel = 0; mipLevel < mipViews.Length; mipLevel++)
            {
                SetDebugName(
                    ObjectType.ImageView,
                    mipViews[mipLevel].Handle,
                    $"{debugName} mip{mipLevel}");
            }
            if (renderPass.Handle != 0)
            {
                SetDebugName(ObjectType.RenderPass, renderPass.Handle, $"{debugName} renderpass");
                SetDebugName(ObjectType.RenderPass, initialRenderPass.Handle, $"{debugName} initial-renderpass");
                SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{debugName} framebuffer");
            }
            _guestImages.Add(target.Address, resource);
            var createdByteCount = GetTextureByteCount(
                target.Format,
                target.Width,
                target.Height,
                depth);
            lock (_gate)
            {
                _guestImageExtents[target.Address] = (
                    target.Width,
                    target.Height,
                    createdByteCount);
            }

            // See the retained-variant path above: track the full backing
            // extent under a byte budget instead of a resolution cap so
            // oversized render targets the guest later rewrites with the CPU
            // are re-uploaded on the next sample.
            if (ShouldTrackGuestImageWrites(createdByteCount))
            {
                SharpEmu.HLE.GuestImageWriteTracker.Track(
                    target.Address,
                    createdByteCount,
                    CurrentGuestWorkSequenceForDiagnostics,
                    "vulkan.render-target");
            }

            if (_traceGuestImageEvents)
            {
                Console.Error.WriteLine(
                    $"[GIMG] created-as-rt addr=0x{target.Address:X} " +
                    $"{target.Width}x{target.Height} fmt={format}");
            }

            return resource;
        }

        private void ReinterpretGuestImageFormat(
            GuestImageResource resource,
            Format format,
            bool promoteRenderPass,
            GuestRenderTarget target)
        {
            if (_realFormatConversionEnabled &&
                RequiresRealFormatConversion(resource.Format, format))
            {
                ConvertGuestImageBytesInPlace(resource, resource.Format, format);
                foreach (var cachedEntry in resource.ReinterpretCache.Values)
                {
                    DestroyReinterpretedGuestImageViews(cachedEntry);
                }
                resource.ReinterpretCache.Clear();
            }

            var previous = new ReinterpretedGuestImageViews(
                resource.View,
                resource.MipViews,
                resource.RenderPass,
                resource.InitialRenderPass,
                resource.Framebuffer);
            resource.ReinterpretCache.TryAdd(resource.Format, previous);

            if (resource.ReinterpretCache.Remove(format, out var cached))
            {
                resource.View = cached.View;
                resource.MipViews = cached.MipViews;
                resource.RenderPass = cached.RenderPass;
                resource.InitialRenderPass = cached.InitialRenderPass;
                resource.Framebuffer = cached.Framebuffer;
                resource.Format = format;
                return;
            }

            var viewUsageInfo = new ImageViewUsageCreateInfo
            {
                SType = StructureType.ImageViewUsageCreateInfo,
                Usage = GetNonStorageGuestImageViewUsage(resource.Type),
            };
            var restrictViewUsage = resource.SupportsStorageUsage &&
                !SupportsStorageImage(format);
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                PNext = restrictViewUsage ? &viewUsageInfo : null,
                Image = resource.Image,
                ViewType = GetGuestTextureViewType(resource.Type),
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(0, resource.MipLevels),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var newView),
                "vkCreateImageView(guest reinterpret)");
            resource.View = newView;

            var mipViews = new ImageView[resource.MipLevels];
            for (uint mipLevel = 0; mipLevel < resource.MipLevels; mipLevel++)
            {
                viewInfo.SubresourceRange = ColorSubresourceRange(mipLevel, 1);
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out var mipView),
                    "vkCreateImageView(guest reinterpret mip)");
                mipViews[mipLevel] = mipView;
            }

            resource.MipViews = mipViews;
            resource.Format = format;

            if (promoteRenderPass)
            {
                var attachmentView = resource.MipViews.Length > 0
                    ? resource.MipViews[0]
                    : resource.View;
                var promoted = CreateRenderPassAndFramebuffer(
                    resource.Format,
                    attachmentView,
                    resource.Width,
                    resource.Height);
                resource.RenderPass = promoted.RenderPass;
                resource.InitialRenderPass = promoted.InitialRenderPass;
                resource.Framebuffer = promoted.Framebuffer;
                var promotedName = GuestImageDebugName(target, format);
                SetDebugName(ObjectType.RenderPass, promoted.RenderPass.Handle, $"{promotedName} renderpass");
                SetDebugName(ObjectType.RenderPass, promoted.InitialRenderPass.Handle, $"{promotedName} initial-renderpass");
                SetDebugName(ObjectType.Framebuffer, promoted.Framebuffer.Handle, $"{promotedName} framebuffer");
            }
        }

        private void DestroyReinterpretedGuestImageViews(ReinterpretedGuestImageViews views)
        {
            if (views.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, views.Framebuffer, null);
            }

            if (views.RenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, views.RenderPass, null);
            }

            if (views.InitialRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, views.InitialRenderPass, null);
            }

            if (views.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, views.View, null);
            }

            foreach (var mipView in views.MipViews)
            {
                if (mipView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, mipView, null);
                }
            }
        }

        private void DestroyGuestImage(GuestImageResource resource)
        {
            foreach (var depthFramebuffer in resource.DepthFramebuffers.Values)
            {
                DestroyDepthFramebuffer(depthFramebuffer);
            }
            resource.DepthFramebuffers.Clear();

            foreach (var view in resource.FormatViews.Values)
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }
            }
            resource.FormatViews.Clear();

            foreach (var cached in resource.ReinterpretCache.Values)
            {
                DestroyReinterpretedGuestImageViews(cached);
            }
            resource.ReinterpretCache.Clear();

            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.RenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.RenderPass, null);
            }

            if (resource.InitialRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.InitialRenderPass, null);
            }

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }

            foreach (var mipView in resource.MipViews)
            {
                if (mipView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, mipView, null);
                }
            }

            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }

            if (resource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, resource.Memory, null);
            }

        }

        private void TransitionNewGuestImageToSampled(Image image, uint mipLevels)
        {
            var commandBuffer = AllocateGuestCommandBuffer();
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(
                _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                "vkBeginCommandBuffer(guest image init)");
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = ColorSubresourceRange(0, mipLevels),
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
            Check(
                _vk.EndCommandBuffer(commandBuffer),
                "vkEndCommandBuffer(guest image init)");
            // Same-queue submission order makes the transition visible to any
            // later use of the image; no CPU-side wait is needed.
            SubmitGuestCommandBuffer(commandBuffer, [], []);
        }

    }
}

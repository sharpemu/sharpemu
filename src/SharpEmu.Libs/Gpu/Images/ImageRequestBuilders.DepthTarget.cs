// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// A resolved depth target: the request plus the planes, sizes and clear enables of the attachment.
public readonly record struct DepthTargetResolution(
    ImageRequest Request,
    Format Format,
    uint Width,
    uint Height,
    uint Samples,
    bool HasStencil,
    bool HasHtile,
    ulong DepthAddress,
    ulong DepthSize,
    ulong StencilAddress,
    ulong StencilSize,
    ulong HtileAddress,
    ulong HtileSize,
    bool DepthClearEnabled,
    bool StencilClearEnabled,
    bool DepthWriteDisabled,
    bool StencilWriteDisabled);

public static partial class ImageRequestBuilders
{
    public static ImageUsageFlags DepthTargetUsage =>
        ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

    // The first host format of the rule the device supports at the sample count, or Undefined.
    private static Format HostDepthAttachmentFormat(IImageFormatSupport device, DepthFormatRule rule, bool hasStencil, uint samples)
    {
        var required = ImageDescription.VulkanSampleCount(samples);
        bool Supports(Format format) =>
            format != Format.Undefined &&
            device.TryGetImageFormatProperties(format, ImageType.Type2D, ImageTiling.Optimal, DepthTargetUsage, 0, out var properties) &&
            (properties.SampleCounts & required) != 0;

        if (!hasStencil)
        {
            return Supports(rule.DepthAttachmentFormat) ? rule.DepthAttachmentFormat : Format.Undefined;
        }

        foreach (var format in rule.StencilAttachmentFormats)
        {
            if (Supports(format))
            {
                return format;
            }
        }

        return Format.Undefined;
    }

    private static bool HtileStencilCompatible(bool hasStencil, bool hasHtile, bool htileStencilDisabled) => !hasStencil || !hasHtile || htileStencilDisabled;

    // Builds the request for the bound depth target. Null when no depth or stencil state is active.
    public static DepthTargetResolution? DepthTarget(in DepthTargetWords depthWords, IImageFormatSupport device)
    {
        var hasStencil = depthWords.StencilFormat != GuestStencilFormat.Invalid;
        var depthActive = depthWords.DepthTestEnabled || depthWords.DepthWriteEnabled || depthWords.DepthBoundsEnabled || depthWords.DepthClearEnabled || depthWords.CopyDepthToColor;
        var stencilActive = hasStencil && (depthWords.StencilTestEnabled || depthWords.StencilClearEnabled || depthWords.CopyStencilToColor);
        if (!depthActive && !stencilActive)
        {
            return null;
        }

        // The size register is independent state; a zero encoding alone must not produce an attachment.
        var attachmentUnbound =
            depthWords.DepthFormat == GuestDepthFormat.Invalid && !hasStencil && depthWords.SamplesLog2 == 0 && !depthWords.ZExpClear && !depthWords.ZPartiallyResident && depthWords.MaxMip == 0 &&
            !depthWords.StencilExpClear && !depthWords.StencilPartiallyResident && depthWords.SliceStart == 0 && depthWords.SliceMax == 0 && depthWords.ViewMipLevel == 0 &&
            !depthWords.DepthWriteDisabled && !depthWords.StencilWriteDisabled && depthWords.ZReadBase == 0 && depthWords.ZWriteBase == 0 && depthWords.StencilReadBase == 0 &&
            depthWords.StencilWriteBase == 0 && depthWords.HtileBase == 0 && !depthWords.HtileAcceleration && depthWords.ShadingRateEncoding == 0 && depthWords.XMax == 0 && depthWords.YMax == 0;
        if (attachmentUnbound)
        {
            return null;
        }

        var hasHtile = depthWords.HtileAcceleration;
        var unsupportedShadingRate = depthWords.ShadingRateEncoding > 1 || (depthWords.ShadingRateEncoding != 0 && !hasHtile);
        var samples = SampleCount(depthWords.SamplesLog2);
        if (samples == 0)
        {
            throw SubmissionScheduler.Fatal($"The depth fragment count is not supported: samples={depthWords.SamplesLog2}.");
        }

        if (!TargetViewRange.TryResolve(depthWords.SliceStart, depthWords.SliceMax, 0, out var view))
        {
            throw SubmissionScheduler.Fatal($"The depth view is invalid: base={depthWords.SliceStart} last={depthWords.SliceMax}.");
        }

        if (depthWords.CopyDepthToColor || depthWords.CopyStencilToColor || depthWords.CopyCentroid || depthWords.CopySample != 0 || depthWords.ZExpClear || depthWords.StencilExpClear ||
            depthWords.ZPartiallyResident || depthWords.StencilPartiallyResident || depthWords.MaxMip != 0 || depthWords.ViewMipLevel != 0 || unsupportedShadingRate ||
            depthWords.ZReadBase == 0 || (!depthWords.DepthWriteDisabled && depthWords.ZWriteBase != depthWords.ZReadBase) || (depthWords.ZReadBase & 0xFFFF) != 0 || depthWords.DepthCompare > 7)
        {
            throw SubmissionScheduler.Fatal(
                $"The depth register state is not supported: zInfo=0x{depthWords.ZInfo:X8} stencilInfo=0x{depthWords.StencilInfo:X8} view=0x{depthWords.DepthView:X8} " +
                $"renderControl=0x{depthWords.RenderControl:X8} shadingRate={depthWords.ShadingRateEncoding} read=0x{depthWords.ZReadBase:X16} write=0x{depthWords.ZWriteBase:X16}.");
        }

        if (hasStencil)
        {
            if (depthWords.StencilFormat != GuestStencilFormat.Stencil8UInt || !HtileStencilCompatible(hasStencil, hasHtile, depthWords.HtileStencilDisabled) ||
                depthWords.StencilReadBase == 0 || (!depthWords.StencilWriteDisabled && depthWords.StencilWriteBase != depthWords.StencilReadBase) || (depthWords.StencilReadBase & 0xFFFF) != 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The stencil attachment state is not supported: stencilInfo=0x{depthWords.StencilInfo:X8} htile={hasHtile} read=0x{depthWords.StencilReadBase:X16} write=0x{depthWords.StencilWriteBase:X16}.");
            }
        }
        else if (depthWords.StencilReadBase != 0 || depthWords.StencilWriteBase != 0)
        {
            throw SubmissionScheduler.Fatal($"Stencil state is set without an active stencil attachment: read=0x{depthWords.StencilReadBase:X16} write=0x{depthWords.StencilWriteBase:X16}.");
        }

        if (hasHtile)
        {
            if (depthWords.HtileBase == 0 || (depthWords.HtileBase & 0x7FFF) != 0)
            {
                throw SubmissionScheduler.Fatal($"The HTile metadata address is invalid: htile=0x{depthWords.HtileBase:X16}.");
            }

            if (depthWords.SliceMax >= 32)
            {
                throw SubmissionScheduler.Fatal($"HTile clear tracking supports at most 32 slices: last={depthWords.SliceMax}.");
            }
        }

        if (!depthWords.DepthSizeValid)
        {
            throw SubmissionScheduler.Fatal("The depth extent is missing.");
        }

        var width = depthWords.XMax + 1;
        var height = depthWords.YMax + 1;
        if (width > 16384 || height > 16384)
        {
            throw SubmissionScheduler.Fatal($"The depth extent is invalid: extent={width}x{height}.");
        }

        var rule = DepthFormatRule.Find(depthWords.DepthFormat) ?? throw SubmissionScheduler.Fatal($"The depth/stencil format pair is not supported: zFormat={(uint)depthWords.DepthFormat} stencil={hasStencil}.");
        var format = HostDepthAttachmentFormat(device, rule, hasStencil, samples);
        if (format == Format.Undefined)
        {
            throw SubmissionScheduler.Fatal($"No host depth/stencil format supports the required usage: ideal={(int)rule.AttachmentFormat(hasStencil)} samples={samples}.");
        }

        var bytes = rule.BytesPerElement;
        var pitch = TileGeometry.DepthPitch(width, bytes, depthWords.SamplesLog2);
        if (!TileGeometry.TryGetDepthSize(width, height, 0, depthWords.DepthFormat, depthWords.StencilFormat, hasHtile, out var stencilSize, out var htileSize, out var depthSize, depthWords.SamplesLog2) ||
            depthSize.Align != 65536 || depthSize.Size == 0 ||
            hasStencil != (stencilSize.Align == 65536 && stencilSize.Size != 0) ||
            hasHtile != (htileSize.Align == 32768 && htileSize.Size != 0))
        {
            throw SubmissionScheduler.Fatal(
                $"The depth/stencil/HTile footprint is not supported: extent={width}x{height} zFormat={(uint)depthWords.DepthFormat} stencil={hasStencil} htile={hasHtile} samplesLog2={depthWords.SamplesLog2}.");
        }

        if (depthSize.Size > ulong.MaxValue / view.ImageLayers || stencilSize.Size > ulong.MaxValue / view.ImageLayers || htileSize.Size > ulong.MaxValue / view.ImageLayers)
        {
            throw SubmissionScheduler.Fatal($"The layered depth footprint overflows: layers={view.ImageLayers}.");
        }

        var depthBackingSize = (ulong)depthSize.Size * view.ImageLayers;
        var stencilBackingSize = (ulong)stencilSize.Size * view.ImageLayers;
        var htileBackingSize = (ulong)htileSize.Size * view.ImageLayers;
        if (!new GuestSpan(depthWords.ZReadBase, depthBackingSize).IsValid ||
            (hasStencil && !new GuestSpan(depthWords.StencilReadBase, stencilBackingSize).IsValid) ||
            (hasHtile && !new GuestSpan(depthWords.HtileBase, htileBackingSize).IsValid))
        {
            throw SubmissionScheduler.Fatal(
                $"The layered depth backing range is invalid: depth=0x{depthWords.ZReadBase:X16}+0x{depthBackingSize:X} stencil=0x{depthWords.StencilReadBase:X16}+0x{stencilBackingSize:X} htile=0x{depthWords.HtileBase:X16}+0x{htileBackingSize:X}.");
        }

        var stencilAddress = hasStencil ? depthWords.StencilReadBase : 0;
        stencilBackingSize = hasStencil ? stencilBackingSize : 0;
        var htileAddress = hasHtile ? depthWords.HtileBase : 0;
        htileBackingSize = hasHtile ? htileBackingSize : 0;
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(depthWords.ZReadBase, depthBackingSize);
        description.Stencil = new GuestSpan(stencilAddress, stencilBackingSize);
        description.PixelFormat = format;
        description.GuestFormat = rule.GuestFormat;
        description.Type = GuestImageType.Color2D;
        description.Extent = new Extent3D(width, height, 1);
        description.Resources = new SubresourceCount(1, view.ImageLayers);
        description.Pitch = pitch;
        description.BytesPerBlock = bytes;
        description.Samples = samples;
        description.TileMode = GuestTileMode.Depth;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = depthBackingSize, Pitch = pitch, Height = height };
        description.Metadata.Range = new GuestSpan(htileAddress, htileBackingSize);
        description.Metadata.Kind = hasHtile ? MetadataKind.Htile : MetadataKind.None;
        description.Metadata.StencilCompressed = hasStencil && hasHtile && !depthWords.HtileStencilDisabled;
        var viewDescription = new ImageViewDescription(
            format, view.LayerCount == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray, ViewFormatRules.DepthAspects(format),
            0, 1, view.BaseLayer, view.LayerCount, default, ImageUsageFlags.DepthStencilAttachmentBit);
        var request = new ImageRequest(description, viewDescription, ImageRole.DepthTarget);
        return new DepthTargetResolution(
            request, format, width, height, samples, hasStencil, hasHtile,
            depthWords.ZReadBase, depthBackingSize, stencilAddress, stencilBackingSize, htileAddress, htileBackingSize,
            depthWords.DepthClearEnabled, hasStencil && depthWords.StencilClearEnabled && !depthWords.StencilWriteDisabled, depthWords.DepthWriteDisabled, depthWords.StencilWriteDisabled);
    }

    // Builds the request for a video-out surface registered by the guest.
    public static ImageRequest DisplaySurface(in DisplaySurfaceWords surface)
    {
        const ulong StrictColorimetryOption = 1;
        var compression = ImageDescription.ClassifyDisplayCompression(surface.Compressed, surface.MetadataAddress, surface.DccControl, surface.DccClearColor);
        if (surface.Width == 0 || surface.Height == 0 || surface.Width > 16384 || surface.Height > 16384 ||
            (surface.Option != 0 && surface.Option != StrictColorimetryOption) || surface.TilingMode != 0 || surface.DataAddress == 0 ||
            compression == DisplayCompression.Unsupported)
        {
            throw SubmissionScheduler.Fatal(
                $"The video-out surface attributes are not supported: address=0x{surface.DataAddress:X16} extent={surface.Width}x{surface.Height} tiling={surface.TilingMode} " +
                $"option=0x{surface.Option:X} compressed={surface.Compressed} metadata=0x{surface.MetadataAddress:X16} dccControl=0x{surface.DccControl:X8}.");
        }

        if (!DisplayFormatRule.TryDecode(surface.PixelFormat, out var pixelFormat))
        {
            throw SubmissionScheduler.Fatal($"The video-out pixel format is not supported: format=0x{surface.PixelFormat:X16}.");
        }

        const GuestTileMode tileMode = GuestTileMode.RenderTarget;
        var pitch = TileGeometry.TexturePitch(pixelFormat.GuestFormat, surface.Width, tileMode);
        var total = TileGeometry.TextureTotalSize(pixelFormat.GuestFormat, surface.Width, surface.Height, 1, 1, tileMode, false);
        if (total.Size == 0 || total.Align != 65536 || (surface.DataAddress & (total.Align - 1UL)) != 0)
        {
            throw SubmissionScheduler.Fatal($"The video-out surface footprint or alignment is invalid: address=0x{surface.DataAddress:X16} size=0x{total.Size:X} align=0x{total.Align:X}.");
        }

        var description = ImageDescription.Create();
        description.Data = new GuestSpan(surface.DataAddress, total.Size);
        description.PixelFormat = pixelFormat.HostFormat;
        description.GuestFormat = pixelFormat.GuestFormat;
        description.Type = GuestImageType.Color2D;
        description.Extent = new Extent3D(surface.Width, surface.Height, 1);
        description.Resources = SubresourceCount.Single;
        description.Pitch = pitch;
        description.BytesPerBlock = pixelFormat.BytesPerElement;
        description.Samples = 1;
        description.TileMode = tileMode;
        description.Bgra16 = pixelFormat.Bgra16;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = total.Size, Pitch = pitch, Height = surface.Height };
        if (compression != DisplayCompression.Uncompressed)
        {
            description.Metadata.Range = new GuestSpan(surface.MetadataAddress, 0);
            description.Metadata.Kind = MetadataKind.Dcc;
            description.Metadata.Control = surface.DccControl;
            description.Metadata.Compression = compression;
        }

        description.Validate();
        if (!DisplayFormatRule.Supports(description))
        {
            throw SubmissionScheduler.Fatal($"The normalized video-out format is not supported: format={(int)description.PixelFormat} guestFormat={(uint)description.GuestFormat}.");
        }

        var view = new ImageViewDescription(
            description.PixelFormat, ImageViewType.Type2D, ImageAspectFlags.ColorBit, 0, 1, 0, 1, default, ImageUsageFlags.TransferSrcBit);
        return new ImageRequest(description, view, ImageRole.DisplaySurface);
    }
}

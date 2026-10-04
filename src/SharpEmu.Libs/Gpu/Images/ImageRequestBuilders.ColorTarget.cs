// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// A resolved color target: the request plus what the draw needs to attach and clear it.
public readonly record struct ColorTargetResolution(
    ImageRequest Request,
    ulong BaseAddress,
    ulong BackingSize,
    Extent2D Extent,
    uint BaseMipLevel,
    uint BaseArrayLayer,
    uint Samples,
    ColorComponentMap ExportMapping,
    bool MetadataClearSupported,
    bool MetadataFixedClearSupported,
    ClearColorValue ColorClearValue);

// One layer window of a target view; the whole image has LastLayer + 1 layers.
public readonly record struct TargetViewRange(uint BaseLayer, uint LayerCount, uint ImageLayers)
{
    public static bool TryResolve(uint baseLayer, uint lastLayer, uint drawLayerOffset, out TargetViewRange range)
    {
        range = default;
        if (baseLayer > lastLayer || drawLayerOffset != 0)
        {
            return false;
        }

        range = new TargetViewRange(baseLayer, lastLayer - baseLayer + 1, lastLayer + 1);
        return true;
    }
}

public static partial class ImageRequestBuilders
{
    private static long _normalizedSliceRanges;

    public static uint SampleCount(uint encodedLog2) => encodedLog2 <= 3 ? 1u << (int)encodedLog2 : 0;

    private static bool DccAlphaOnMsb(in ColorTargetWords words)
    {
        switch (words.Layout)
        {
            case ChannelLayout.Bits10_10_10_2:
            case ChannelLayout.Bits10_10_10_2Float:
            case ChannelLayout.Bits5_5_5_1:
                return true;
            case ChannelLayout.Bits2_10_10_10:
            case ChannelLayout.Bits1_5_5_5:
                return false;
        }

        var components = GuestPixelFormats.ResolveRenderTargetEncoding(words.Layout, words.NumberType).Components;
        if (components == 1)
        {
            return words.Order != ChannelOrder.Standard;
        }

        return components == 3 || words.Order is ChannelOrder.Standard or ChannelOrder.Alternate;
    }

    // DCC clears use the target's packed clear word; the fixed-clear set is a format allow-list.
    private static (bool Supported, bool FixedSupported, ClearColorValue Value) DccClearInfo(Format format, bool hasDcc, uint packedClear)
    {
        ClearColorValue value = default;
        var supported = hasDcc && PackedClearValue.TryDecodeColor(format, packedClear, out value);
        var fixedSupported = hasDcc && PackedClearValue.SupportsDccFixedColor(format);
        return (supported, fixedSupported, supported ? value : default);
    }

    public static bool SupportsDccFixedClear(Format format) => format switch
    {
        Format.R8Unorm or Format.R8G8Unorm or Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb or Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb or
        Format.A2B10G10R10UnormPack32 or Format.A2R10G10B10UnormPack32 or Format.R5G6B5UnormPack16 or Format.A1R5G5B5UnormPack16 or
        Format.R4G4B4A4UnormPack16 or Format.R16Unorm or Format.R16G16Unorm or Format.R16G16B16A16Unorm or Format.R16Sfloat or
        Format.R16G16Sfloat or Format.R16G16B16A16Sfloat or Format.R32Sfloat or Format.R32G32Sfloat or Format.R32G32B32A32Sfloat or
        Format.B10G11R11UfloatPack32 => true,
        _ => false,
    };

    // Builds the request for a bound color target. Null when the slot carries no target.
    public static ColorTargetResolution? ColorTarget(in ColorTargetWords words, uint targetMask, uint drawLayerOffset, bool ignoreTargetMask)
    {
        var mask = targetMask & 0xF;
        if (ignoreTargetMask && words.BaseAddress != 0 && mask == 0)
        {
            mask = 0xF;
        }

        if (words.BaseAddress == 0 || mask == 0)
        {
            if (Rendering.RenderTrace.Enabled)
            {
                Rendering.RenderTrace.Write($"ColorAttachmentRejected reason={(words.BaseAddress == 0 ? "zero-address" : "masked-output")} address=0x{words.BaseAddress:X16} mask=0x{mask:X}");
            }
            return null;
        }

        var samples = SampleCount(words.FragmentsLog2);
        if (samples == 0 || words.SamplesLog2 != words.FragmentsLog2)
        {
            throw SubmissionScheduler.Fatal($"The render-target sample configuration is not supported: samples={words.SamplesLog2} fragments={words.FragmentsLog2}.");
        }

        var levels = words.MaxMip + 1;
        if (levels > ImageDescription.MaxLevels || words.MipLevel >= levels)
        {
            throw SubmissionScheduler.Fatal($"The render-target mip range is not supported: current={words.MipLevel} levels={levels}.");
        }

        if (words.Dimension > 2)
        {
            throw SubmissionScheduler.Fatal($"The render-target dimension is not supported: dimension={words.Dimension}.");
        }

        var imageType = words.Dimension switch { 0 => GuestImageType.Color1D, 1 => GuestImageType.Color2D, _ => GuestImageType.Color3D };
        var is1D = imageType == GuestImageType.Color1D;
        var volume = imageType == GuestImageType.Color3D;
        if (is1D && words.Height != 0)
        {
            throw SubmissionScheduler.Fatal($"A 1D render target has a nonzero height: height={words.Height}.");
        }

        if (!volume && words.Depth != 0)
        {
            throw SubmissionScheduler.Fatal($"A render target that is not 3D has a nonzero depth: depth={words.Depth}.");
        }

        if (is1D && samples != 1)
        {
            throw SubmissionScheduler.Fatal("A multisampled 1D render target is not supported.");
        }

        if (volume && samples != 1)
        {
            throw SubmissionScheduler.Fatal("A multisampled 3D render target is not supported.");
        }

        var depth = volume ? words.Depth + 1 : 1;
        // A volume's VIEW range bounds the slices exported by this draw, while
        // ATTRIB3 carries the allocation depth. Guests may leave SLICE_MAX at
        // the full-volume bound when rendering a mip, so cap it to that mip's
        // last physical Z slice before resolving the attachment view.
        var lastLayer = volume
            ? Math.Min(words.SliceMax, Math.Max(depth >> (int)words.MipLevel, 1) - 1)
            : words.SliceMax;
        if (volume && lastLayer != words.SliceMax)
        {
            var normalized = Interlocked.Increment(ref _normalizedSliceRanges);
            if (normalized <= 8 || (normalized & (normalized - 1)) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] normalized a 3D color-target slice range to last={lastLayer} depth={depth} count={normalized}");
            }
        }

        if (!TargetViewRange.TryResolve(words.SliceStart, lastLayer, drawLayerOffset, out var view))
        {
            throw SubmissionScheduler.Fatal($"The render-target view is invalid: base={words.SliceStart} last={words.SliceMax} drawOffset={drawLayerOffset}.");
        }
        var tileMode = words.TileMode;
        var standard4 = tileMode == GuestTileMode.Standard4KB;
        var standard64 = tileMode == GuestTileMode.Standard64KB;
        var depthTile = tileMode == GuestTileMode.Depth;
        var textureTile = standard4 || standard64 || depthTile;
        bool tiled;
        switch (tileMode)
        {
            case GuestTileMode.Linear:
            case GuestTileMode.Standard4KB:
            case GuestTileMode.Standard64KB:
            case GuestTileMode.Depth:
            case GuestTileMode.RenderTarget:
                tiled = tileMode != GuestTileMode.Linear;
                break;
            default:
                throw SubmissionScheduler.Fatal($"The render-target tile mode is unknown: tile={(uint)tileMode}.");
        }

        if (!tiled && levels > 1)
        {
            throw SubmissionScheduler.Fatal("A linear mipmapped render target is not supported.");
        }

        if (samples > 1 && (!tiled || levels != 1))
        {
            throw SubmissionScheduler.Fatal("A multisampled render target needs a single-mip tiled surface.");
        }

        if (textureTile && samples != 1)
        {
            throw SubmissionScheduler.Fatal("A texture-tiled color render target does not support multisampling.");
        }

        var width = words.Width + 1;
        var height = words.Height + 1;
        var targetFormat = TextureTransferLayout.RenderTargetFormat(words.Layout, words.NumberType, words.Order);
        var bytesPerElement = targetFormat.BytesPerElement;
        if (bytesPerElement == 0)
        {
            throw SubmissionScheduler.Fatal("The render-target format has no valid element size.");
        }

        var transferFormat = ImageDescription.RenderTargetTransferFormat(bytesPerElement);
        TileTextureBlockLayout textureTileLayout = default;
        if (textureTile &&
            (!TileGeometry.TryGetTextureBlockLayout(transferFormat, tileMode, volume, out textureTileLayout) ||
             (words.BaseAddress & (textureTileLayout.Block.BlockSize - 1UL)) != 0 ||
             words.FmaskCompression || words.FmaskCompressionDisabled || words.FmaskOneFragment || words.FastClear || words.DccEnabled ||
             words.CmaskAddress != 0 || words.FmaskAddress != 0 || words.DccAddress != 0 || words.DataWriteOnDccClearToRegister))
        {
            throw SubmissionScheduler.Fatal(
                $"The texture-tiled render target is not supported: address=0x{words.BaseAddress:X16} tile={(uint)tileMode} info=0x{words.Info:X8} " +
                $"cmask=0x{words.CmaskAddress:X16} fmask=0x{words.FmaskAddress:X16} dcc=0x{words.DccAddress:X16} dccControl=0x{words.DccControl:X8}.");
        }

        if ((standard64 || depthTile) && (words.Dimension != 1 || words.Depth != 0 || (depthTile && (view.BaseLayer != 0 || view.ImageLayers != 1))))
        {
            throw SubmissionScheduler.Fatal(
                $"The 64 KiB texture-tiled render-target view is not supported: dimension={words.Dimension} depth={words.Depth} baseLayer={view.BaseLayer} layers={view.ImageLayers}.");
        }

        var pitch = !tiled || volume || textureTile
            ? TileGeometry.TexturePitch(transferFormat, width, tileMode)
            : TileGeometry.RenderTargetPitch(width, bytesPerElement, words.FragmentsLog2);
        if (pitch == 0)
        {
            throw SubmissionScheduler.Fatal($"The render-target pitch is not supported: width={width} bytes={bytesPerElement}.");
        }

        var mipSpans = new TileLevelSpan[TiledSurfaceLayout.MaxLevels];
        var mipPadded = new TilePaddedSize[TiledSurfaceLayout.MaxLevels];
        TiledSurfaceLayout? volumeLayout = null;
        ulong size;
        ulong backingSize = 0;
        if (volume)
        {
            var surfaceDescription = new TiledSurfaceDescription(transferFormat, tileMode, TileSurfaceDimension.Volume3D, width, height, depth, levels, 1);
            if (!tiled || !TileGeometry.TryGetTiledTextureLayout(surfaceDescription, out volumeLayout))
            {
                throw SubmissionScheduler.Fatal($"The 3D render-target layout is not supported: extent={width}x{height}x{depth} levels={levels} tile={(uint)tileMode}.");
            }

            size = volumeLayout.BlockSliceSize;
            backingSize = volumeLayout.TotalSize;
        }
        else if (tiled)
        {
            TileSizeAndAlignment layout = default;
            bool validLayout;
            if (textureTile)
            {
                TileGeometry.TryGetTextureSize(transferFormat, width, height, levels, tileMode, out layout, mipSpans, mipPadded);
                validLayout = layout.Size != 0 && layout.Align == textureTileLayout.Block.BlockSize;
            }
            else
            {
                validLayout = levels == 1
                    ? TileGeometry.TryGetRenderTargetSize(width, height, pitch, bytesPerElement, out layout, words.FragmentsLog2)
                    : TileGeometry.TryGetRenderTargetMipLayout(width, height, pitch, bytesPerElement, levels, out layout, mipSpans, mipPadded);
            }

            if (!validLayout)
            {
                throw SubmissionScheduler.Fatal($"The render-target layout is not supported: extent={width}x{height} pitch={pitch} bytes={bytesPerElement} levels={levels}.");
            }

            size = layout.Size;
            if (levels == 1)
            {
                mipSpans[0] = new TileLevelSpan { Size = (uint)size };
                mipPadded[0] = new TilePaddedSize(pitch, height);
            }
        }
        else
        {
            size = (ulong)pitch * height * bytesPerElement * samples;
            if (size > uint.MaxValue)
            {
                throw SubmissionScheduler.Fatal($"The linear render-target slice exceeds the supported layout size: size=0x{size:X}.");
            }

            mipSpans[0] = new TileLevelSpan { Size = (uint)size };
            mipPadded[0] = new TilePaddedSize(pitch, height);
        }

        if (size == 0 || (!volume && size > ulong.MaxValue / view.ImageLayers))
        {
            throw SubmissionScheduler.Fatal($"The render-target memory footprint is invalid: size=0x{size:X} layers={view.ImageLayers}.");
        }

        if (!volume)
        {
            backingSize = size * view.ImageLayers;
        }

        if (backingSize == 0)
        {
            throw SubmissionScheduler.Fatal($"The render-target backing is empty: address=0x{words.BaseAddress:X16}.");
        }

        if (!new GuestSpan(words.BaseAddress, backingSize).IsValid)
        {
            throw SubmissionScheduler.Fatal($"The render-target backing range is invalid: address=0x{words.BaseAddress:X16} size=0x{backingSize:X}.");
        }

        var viewExtent = new Extent2D(Math.Max(width >> (int)words.MipLevel, 1), Math.Max(height >> (int)words.MipLevel, 1));
        var viewDepth = Math.Max(depth >> (int)words.MipLevel, 1);
        if (volume && view.BaseLayer < viewDepth && view.LayerCount > viewDepth - view.BaseLayer)
        {
            // Titles bind a whole volume with the last slice set to its depth, one past the end;
            // the color block stops at the last slice of the mip.
            view = view with { LayerCount = viewDepth - view.BaseLayer, ImageLayers = viewDepth };
        }

        if (volume && view.BaseLayer >= viewDepth)
        {
            throw SubmissionScheduler.Fatal($"The 3D render-target view starts past the mip depth: base={view.BaseLayer} count={view.LayerCount} depth={viewDepth} mip={words.MipLevel}.");
        }

        var description = ImageDescription.Create();
        description.Data = new GuestSpan(words.BaseAddress, backingSize);
        description.PixelFormat = targetFormat.HostFormat;
        description.GuestFormat = transferFormat;
        description.Type = imageType;
        description.Extent = new Extent3D(width, height, depth);
        description.Resources = new SubresourceCount(levels, volume ? 1 : view.ImageLayers);
        description.Pitch = pitch;
        description.BytesPerBlock = bytesPerElement;
        description.Samples = samples;
        description.TileMode = tileMode;
        var hasDcc = words.DccEnabled && words.DccAddress != 0;
        if (hasDcc)
        {
            // DCC lives in its own allocation; the address lets the cache match fills seen before the target.
            _ = TileGeometry.TryGetDccSize(
                width, height, volume ? depth : view.ImageLayers, bytesPerElement, levels, tileMode,
                out var metadataSize, words.FragmentsLog2);
            description.Metadata.Kind = MetadataKind.Dcc;
            description.Metadata.Range = new GuestSpan(words.DccAddress, metadataSize.Size);
            description.Metadata.DccClearWord = words.ClearWord0;
            description.Metadata.DccClearRegisterValid = true;
            description.Metadata.DccAlphaMsb = DccAlphaOnMsb(words);
        }

        for (var level = 0; level < levels; level++)
        {
            if (volume)
            {
                var mip = volumeLayout!.Mips[level];
                description.MipLayout[level] = new MipLevelLayout { Offset = mip.Offset, Size = mip.Size, Pitch = mip.PaddedWidth, Height = mip.PaddedHeight };
                continue;
            }

            var span = mipSpans[level];
            var levelOffset = span.SourceSize != 0 ? span.SourceOffset : span.Offset;
            var levelSize = (ulong)(span.SourceSize != 0 ? span.SourceSize : span.Size) * view.ImageLayers;
            description.MipLayout[level] = new MipLevelLayout { Offset = levelOffset, Size = levelSize, Pitch = mipPadded[level].Width, Height = mipPadded[level].Height };
        }

        var viewType = is1D
            ? view.LayerCount == 1 ? ImageViewType.Type1D : ImageViewType.Type1DArray
            : view.LayerCount == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray;
        var viewDescription = new ImageViewDescription(
            targetFormat.HostFormat, viewType, ImageAspectFlags.ColorBit, words.MipLevel, 1, 0, view.BaseLayer, view.LayerCount, default, ImageUsageFlags.ColorAttachmentBit);
        var request = new ImageRequest(description, viewDescription, ImageRole.ColorTarget);
        var (clearSupported, fixedClearSupported, clearValue) = DccClearInfo(targetFormat.HostFormat, hasDcc, words.ClearWord0);
        return new ColorTargetResolution(
            request, words.BaseAddress, backingSize, viewExtent, words.MipLevel, view.BaseLayer, samples, targetFormat.ExportMapping,
            clearSupported, fixedClearSupported, clearValue);
    }
}

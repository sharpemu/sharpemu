// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// A texture request built from the descriptor words and the shape the shader was compiled with.
public readonly record struct TextureRequestResolution(ImageRequest Request, bool ExactFormat, Format ViewFormat, uint Swizzle);

public static partial class ImageRequestBuilders
{
    // A null descriptor binds a one-texel image of the numeric class the shader expects.
    public static ImageRequest NullTexture(TextureNumericClass numericClass, bool storage)
    {
        var (format, guestFormat) = numericClass switch
        {
            TextureNumericClass.Float => (Format.R32Sfloat, GuestPixelFormat.Bits32Float),
            TextureNumericClass.Uint => (Format.R32Uint, GuestPixelFormat.Bits32UInt),
            TextureNumericClass.Sint => (Format.R32Sint, GuestPixelFormat.Bits32SInt),
            _ => throw SubmissionScheduler.Fatal($"A null image needs a supported numeric class: class={numericClass}."),
        };
        var description = ImageDescription.Create();
        description.PixelFormat = format;
        description.GuestFormat = guestFormat;
        description.Type = GuestImageType.Color2D;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = SubresourceCount.Single;
        description.BytesPerBlock = 4;
        description.Samples = 1;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 0, Pitch = 1, Height = 1 };
        var view = ImageViewDescription.Default with
        {
            Format = format,
            Type = ImageViewType.Type2D,
            Aspect = ImageAspectFlags.ColorBit,
            Usage = storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit,
        };
        return new ImageRequest(description, view, storage ? ImageRole.StorageImage : ImageRole.Texture);
    }

    // Fills the mip layout of a texture from the guest tiling rules.
    public static void PopulateTextureMipLayout(ref ImageDescription description)
    {
        if (description.IsVolume && description.TileMode != GuestTileMode.Linear)
        {
            var surfaceDescription = new TiledSurfaceDescription(
                description.GuestFormat, description.TileMode, TileSurfaceDimension.Volume3D,
                description.Extent.Width, description.Extent.Height, description.Extent.Depth, description.Resources.Levels, 1);
            if (!TileGeometry.TryGetTiledTextureLayout(surfaceDescription, out var surface))
            {
                throw SubmissionScheduler.Fatal(
                    $"The volume texture layout is not supported: format={(uint)description.GuestFormat} tile={(uint)description.TileMode} " +
                    $"extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} levels={description.Resources.Levels}.");
            }

            for (var level = 0; level < description.Resources.Levels; level++)
            {
                var mip = surface.Mips[level];
                description.MipLayout[level] = new MipLevelLayout { Offset = mip.Offset, Size = mip.Size, Pitch = mip.PaddedWidth, Height = mip.PaddedHeight };
            }

            return;
        }

        var spans = new TileLevelSpan[TiledSurfaceLayout.MaxLevels];
        var padded = new TilePaddedSize[TiledSurfaceLayout.MaxLevels];
        TileGeometry.TryGetTextureSize(
            description.GuestFormat, description.Extent.Width, description.Extent.Height, description.Resources.Levels, description.TileMode,
            out _, spans, padded);
        for (var level = 0; level < description.Resources.Levels; level++)
        {
            var span = spans[level];
            var offset = span.SourceSize != 0 ? span.SourceOffset : span.Offset;
            ulong size = span.SourceSize != 0 ? span.SourceSize : span.Size;
            size *= description.IsVolume ? Math.Max(description.Extent.Depth >> level, 1) : description.Resources.Layers;
            description.MipLayout[level] = new MipLevelLayout
            {
                Offset = offset,
                Size = size,
                Pitch = padded[level].Width != 0 ? padded[level].Width : Math.Max(description.Pitch >> level, 1),
                Height = padded[level].Height != 0 ? padded[level].Height : Math.Max(description.Extent.Height >> level, 1),
            };
        }
    }

    // The view follows the compiled module: a volume, a layer window to the last layer, or one layer.
    private static ImageViewDescription TextureView(
        in TextureDescriptorWords descriptor, in ShaderImageShape shape, Format format, bool shaderConversion, uint viewLevels, uint imageLayers)
    {
        var mapping = shape.Storage || shaderConversion ? default : ViewFormatRules.ComponentMapping(DestinationSwizzle(descriptor));
        var usage = shape.Storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit;
        if (shape.Volume)
        {
            return new ImageViewDescription(format, ImageViewType.Type3D, ImageAspectFlags.ColorBit, descriptor.BaseLevel, viewLevels, 0, 1, mapping, usage);
        }

        var baseLayer = descriptor.BaseArray;
        if (baseLayer >= imageLayers)
        {
            throw SubmissionScheduler.Fatal($"The texture base layer is outside the image: baseLayer={baseLayer} layers={imageLayers} address=0x{descriptor.BaseAddress:X16}.");
        }

        var type = shape.Arrayed ? ImageViewType.Type2DArray : ImageViewType.Type2D;
        var layerCount = shape.Arrayed ? imageLayers - baseLayer : 1;
        return new ImageViewDescription(format, type, ImageAspectFlags.ColorBit, descriptor.BaseLevel, viewLevels, baseLayer, layerCount, mapping, usage);
    }

    public static uint DestinationSwizzle(in TextureDescriptorWords descriptor) =>
        ViewFormatRules.PackDestinationSelect(descriptor.DestinationSelectX, descriptor.DestinationSelectY, descriptor.DestinationSelectZ, descriptor.DestinationSelectW);

    // Cube maps become two-dimensional arrays; the multisample kinds keep their own type.
    private static GuestImageType TextureType(GuestImageType type) => type == GuestImageType.Cube ? GuestImageType.Color2DArray : type;

    // Every non-volume texture is a two-dimensional image; the translator compiles 1D textures as 2D.
    private static GuestImageType TextureBaseType(GuestImageType type) => type == GuestImageType.Color3D ? GuestImageType.Color3D : GuestImageType.Color2D;

    private static bool IsMultisampledTexture(GuestImageType type) => type is GuestImageType.Color2DMsaa or GuestImageType.Color2DMsaaArray;

    // Builds the request for a sampled or storage texture; the words are the eight T# dwords.
    public static TextureRequestResolution Texture(ReadOnlySpan<uint> words, in ShaderImageShape shape)
    {
        Span<uint> padded = stackalloc uint[8];
        words[..Math.Min(words.Length, 8)].CopyTo(padded);
        var descriptor = new TextureDescriptorWords(padded);
        var compactDescriptor = words.Length < 8;
        var storage = shape.Storage;
        if (descriptor.BaseAddress == 0)
        {
            var nullRequest = NullTexture(shape.NumericClass, storage);
            return new TextureRequestResolution(nullRequest, false, nullRequest.View.Format, 0);
        }

        var address = descriptor.BaseAddress;
        var width = descriptor.Width + 1;
        var height = descriptor.Height + 1;
        var baseLevel = descriptor.BaseLevel;
        var lastLevel = descriptor.LastLevel;
        var type = TextureType(descriptor.Type);
        var multisampled = IsMultisampledTexture(type);
        var maxMip = compactDescriptor ? lastLevel : descriptor.MaxMip;
        var levels = multisampled ? 1 : maxMip + 1;
        var dynamicStorage = storage && shape.DynamicMip;
        var viewLastLevel = !multisampled && !dynamicStorage ? Math.Min(lastLevel, maxMip) : lastLevel;
        var tile = descriptor.TileMode;
        var depthTile = tile == GuestTileMode.Depth;
        var msaaTile = depthTile || tile == GuestTileMode.RenderTarget;
        var msaaArray = type == GuestImageType.Color2DMsaaArray;
        if ((!multisampled && (baseLevel > viewLastLevel || viewLastLevel >= levels)) ||
            (multisampled &&
             (baseLevel != 0 || lastLevel == 0 || lastLevel > 3 || maxMip != lastLevel || !msaaTile || (descriptor.MsaaDepth && !depthTile) ||
              (!msaaArray && (descriptor.Depth != 0 || descriptor.BaseArray != 0)))))
        {
            throw SubmissionScheduler.Fatal(
                $"The texture mip view is not supported: address=0x{address:X16} type={(uint)type} baseLevel={baseLevel} lastLevel={lastLevel} maxMip={maxMip} " +
                $"levels={levels} tile={(uint)tile} depth={descriptor.Depth} baseArray={descriptor.BaseArray} msaaDepth={descriptor.MsaaDepth} storage={storage}.");
        }

        var samples = multisampled ? 1u << (int)lastLevel : 1u;
        var viewLevels = multisampled ? 1 : viewLastLevel - baseLevel + 1;
        var depth = descriptor.Depth + 1;
        var format = descriptor.Format;
        var surfaceFormat = TextureTransferLayout.SurfaceFormat(format);
        var shaderConversion = surfaceFormat.ConversionFormat != GuestPixelFormat.Invalid;
        var volume = type == GuestImageType.Color3D;
        var layered = type is GuestImageType.Color1DArray or GuestImageType.Color2DArray or GuestImageType.Color2DMsaaArray;
        var imageLayers = layered ? depth : 1;
        uint pitch;
        TileSizeAndAlignment size;
        if (multisampled)
        {
            var bytes = GuestPixelFormats.BytesPerElement(format);
            pitch = depthTile ? TileGeometry.DepthPitch(width, bytes, lastLevel) : TileGeometry.RenderTargetPitch(width, bytes, lastLevel);
            if (pitch == 0 || !TileGeometry.TryGetRenderTargetSize(width, height, pitch, bytes, out size, lastLevel) || size.Size > uint.MaxValue / imageLayers)
            {
                throw SubmissionScheduler.Fatal(
                    $"The multisample texture layout is not supported: address=0x{address:X16} extent={width}x{height} bytes={bytes} pitch={pitch} samplesLog2={lastLevel} layers={imageLayers}.");
            }

            size = new TileSizeAndAlignment(size.Size * imageLayers, size.Align);
        }
        else
        {
            pitch = TileGeometry.TexturePitch(format, width, tile);
            size = TileGeometry.TextureTotalSize(format, width, height, volume ? depth : imageLayers, levels, tile, volume);
        }

        if (size.Size == 0 || size.Align == 0 || (address & (size.Align - 1UL)) != 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The texture footprint or alignment is invalid: address=0x{address:X16} size=0x{size.Size:X} align=0x{size.Align:X} format={(uint)format} tile={(uint)tile}.");
        }

        var pixelFormat = surfaceFormat.HostFormat;
        var storageViewFormat = storage && format == GuestPixelFormat.Bits32SInt ? Format.R32Uint : ViewFormatRules.SrgbStorageFormat(pixelFormat);
        var viewFormat = storage && storageViewFormat != Format.Undefined ? storageViewFormat : pixelFormat;
        var blockBytes = GuestPixelFormats.BlockCompressedBytes(format);
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(address, size.Size);
        description.PixelFormat = pixelFormat;
        description.GuestFormat = format;
        description.Type = TextureBaseType(type);
        description.Extent = new Extent3D(width, height, volume ? depth : 1);
        description.Resources = new SubresourceCount(levels, imageLayers);
        description.Pitch = pitch;
        description.BytesPerBlock = blockBytes != 0 ? blockBytes : GuestPixelFormats.BytesPerElement(format);
        description.Samples = samples;
        description.TileMode = tile;
        if (samples > 1)
        {
            description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size.Size, Pitch = pitch, Height = height };
        }
        else
        {
            PopulateTextureMipLayout(ref description);
        }

        var view = TextureView(descriptor, shape, viewFormat, shaderConversion, viewLevels, description.Resources.Layers);
        var request = new ImageRequest(description, view, storage ? ImageRole.StorageImage : ImageRole.Texture);
        return new TextureRequestResolution(request, shaderConversion, pixelFormat, DestinationSwizzle(descriptor));
    }

    // After the lookup: a stencil association redirects to its depth owner; the view rules run on the owner.
    public static ResourceSlotIdentifier ValidateTextureOwner(GuestImageCache cache, ResourceSlotIdentifier imageIdentifier, in TextureRequestResolution resolution)
    {
        var image = cache.GetImage(imageIdentifier);
        if (image.DepthOwner.IsValid)
        {
            return image.DepthOwner;
        }

        var storage = resolution.Request.Role == ImageRole.StorageImage;
        if (image.Description.IsDepth)
        {
            if (storage)
            {
                throw SubmissionScheduler.Fatal($"A depth target cannot be bound as a storage image: address=0x{image.Description.Data.Address:X16}.");
            }

            _ = ViewFormatRules.SelectSampledDepthView(image.Description.PixelFormat, resolution.ViewFormat, resolution.Swizzle);
        }
        else if (storage)
        {
            ViewFormatRules.ValidateStorageColorView(image.Description.PixelFormat, resolution.Request.View.Format, resolution.Swizzle);
        }
        else
        {
            _ = ViewFormatRules.SelectSampledColorView(image.Description.PixelFormat, resolution.ViewFormat, resolution.Swizzle);
        }

        return imageIdentifier;
    }
}

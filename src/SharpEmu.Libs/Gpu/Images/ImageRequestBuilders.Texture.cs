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
    public static ImageRequest NullTexture(in ShaderImageShape shape)
    {
        var numericClass = shape.NumericClass;
        var storage = shape.Storage;
        var (format, guestFormat) = numericClass switch
        {
            TextureNumericClass.Float => (Format.R32Sfloat, GuestPixelFormat.Bits32Float),
            TextureNumericClass.Uint => (Format.R32Uint, GuestPixelFormat.Bits32UInt),
            TextureNumericClass.Sint => (Format.R32Sint, GuestPixelFormat.Bits32SInt),
            _ => throw SubmissionScheduler.Fatal($"A null image needs a supported numeric class: class={numericClass}."),
        };
        // Depth-reference sampling needs a depth view even for the placeholder.
        var depthCompare = shape.DepthCompare && numericClass == TextureNumericClass.Float && !storage;
        if (depthCompare)
        {
            format = Format.D32Sfloat;
        }

        var description = ImageDescription.Create();
        description.PixelFormat = format;
        description.GuestFormat = guestFormat;
        description.Type = shape.Volume
            ? GuestImageType.Color3D
            : shape.OneDimensional ? GuestImageType.Color1D : GuestImageType.Color2D;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = new SubresourceCount(1, shape.Cube ? 6u : 1u);
        description.BytesPerBlock = 4;
        description.Samples = shape.Multisampled ? 4u : 1u;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 0, Pitch = 1, Height = 1 };
        var view = ImageViewDescription.Default with
        {
            Format = format,
            Type = shape.Volume
                ? ImageViewType.Type3D
                : shape.Cube
                    ? ImageViewType.TypeCube
                : shape.OneDimensional
                    ? shape.Arrayed ? ImageViewType.Type1DArray : ImageViewType.Type1D
                    : shape.Arrayed ? ImageViewType.Type2DArray : ImageViewType.Type2D,
            Aspect = depthCompare ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit,
            LayerCount = shape.Cube ? 6u : 1u,
            Usage = storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit,
        };
        return new ImageRequest(description, view, storage ? ImageRole.StorageImage : ImageRole.Texture);
    }

    // A depth placeholder reads its value into red, as sampled depth views do.
    public static TextureRequestResolution NullTextureResolution(in ShaderImageShape shape)
    {
        var request = NullTexture(shape);
        var swizzle = request.Description.IsDepth ? ViewFormatRules.PackDestinationSelect(4, 0, 0, 1) : 0u;
        return new TextureRequestResolution(request, false, request.View.Format, swizzle);
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
        in TextureDescriptorWords descriptor, in ShaderImageShape shape, Format format, bool shaderConversion, uint viewLevels, uint imageLayers,
        uint firstLevel = 0)
    {
        var baseLevel = descriptor.BaseLevel - firstLevel;
        var mapping = shape.Storage || shaderConversion ? default : ViewFormatRules.ComponentMapping(DestinationSwizzle(descriptor));
        var usage = shape.Storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit;
        if (shape.Volume)
        {
            return WithMinLod(new ImageViewDescription(format, ImageViewType.Type3D, ImageAspectFlags.ColorBit, baseLevel, viewLevels, 0, 1, mapping, usage), descriptor, shape, firstLevel);
        }

        var baseLayer = descriptor.BaseArray;
        if (baseLayer >= imageLayers)
        {
            throw SubmissionScheduler.Fatal($"The texture base layer is outside the image: baseLayer={baseLayer} layers={imageLayers} address=0x{descriptor.BaseAddress:X16}.");
        }

        var layerCount = shape.Arrayed ? imageLayers - baseLayer : 1;
        var type = shape.OneDimensional
            ? shape.Arrayed ? ImageViewType.Type1DArray : ImageViewType.Type1D
            : shape.Arrayed ? ImageViewType.Type2DArray : ImageViewType.Type2D;
        return WithMinLod(new ImageViewDescription(format, type, ImageAspectFlags.ColorBit, baseLevel, viewLevels, baseLayer, layerCount, mapping, usage), descriptor, shape, firstLevel);
    }

    // MIN_LOD (4.8 fixed point, absolute mip levels) keeps sampling off mips a streamed texture has not
    // loaded yet; ignoring it sampled the unloaded mip 0 of Astro Bot's stadium sky and bloom spread its
    // garbage HDR values over the screen. Storage views address levels explicitly and are not clamped.
    private static ImageViewDescription WithMinLod(ImageViewDescription view, in TextureDescriptorWords descriptor, in ShaderImageShape shape, uint firstLevel)
    {
        // The host image starts at the first resident guest level; view levels count from there.
        var minLod = descriptor.MinLod / 256f - firstLevel;
        if (shape.Storage || minLod <= view.BaseLevel)
        {
            return view;
        }

        return view with { MinLod = Math.Min(minLod, view.BaseLevel + view.LevelCount - 1) };
    }

    public static uint DestinationSwizzle(in TextureDescriptorWords descriptor) =>
        ViewFormatRules.PackDestinationSelect(descriptor.DestinationSelectX, descriptor.DestinationSelectY, descriptor.DestinationSelectZ, descriptor.DestinationSelectW);

    // Cube maps become two-dimensional arrays; the multisample kinds keep their own type.
    private static GuestImageType TextureType(GuestImageType type) => type == GuestImageType.Cube ? GuestImageType.Color2DArray : type;

    private static GuestImageType TextureBaseType(GuestImageType type) => type switch
    {
        GuestImageType.Color1DArray => GuestImageType.Color1D,
        GuestImageType.Color2DArray or GuestImageType.Color2DMsaa or GuestImageType.Color2DMsaaArray => GuestImageType.Color2D,
        _ => type,
    };

    private static bool IsMultisampledTexture(GuestImageType type) => type is GuestImageType.Color2DMsaa or GuestImageType.Color2DMsaaArray;

    private readonly record struct TextureKey(uint W0, uint W1, uint W2, uint W3, uint W4, uint W5, uint W6, uint W7, ShaderImageShape Shape);

    private const int TextureCacheLimit = 8192;

    // Every texture binding of every draw resolves its T# here, and the tiled layout math
    // allocates several arrays each time; the result depends only on the words and shape
    // and is a pure value, so it is memoised per thread.
    [ThreadStatic]
    private static Dictionary<TextureKey, TextureRequestResolution>? _textureCache;

    // Whether the T# describes a texture the host can create: a non-null base and a format with
    // a host equivalent. The hardware reads any other descriptor as the null texture.
    public static bool DescribesHostTexture(ReadOnlySpan<uint> words)
    {
        if (words.Length < 4)
        {
            return false;
        }

        var descriptor = new TextureDescriptorWords(words);
        return !descriptor.IsNull &&
            GuestPixelFormats.HostFormat(GuestPixelFormats.RemapTextureFormat(descriptor.Format)) != Format.Undefined;
    }

    // Builds the request for a sampled or storage texture; the words are the eight T# dwords.
    public static TextureRequestResolution Texture(ReadOnlySpan<uint> words, in ShaderImageShape shape)
    {
        Span<uint> key = stackalloc uint[8];
        words[..Math.Min(words.Length, 8)].CopyTo(key);
        var cacheKey = new TextureKey(key[0], key[1], key[2], key[3], key[4], key[5], key[6], key[7], shape);
        var cache = _textureCache ??= new Dictionary<TextureKey, TextureRequestResolution>();
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolution = BuildTexture(key, shape, tolerateInvalid: false, out _);
        if (cache.Count >= TextureCacheLimit)
        {
            cache.Clear();
        }

        cache[cacheKey] = resolution;
        return resolution;
    }

    // A T# read at run time can be any lane's register contents, which the hardware reads as
    // the null texture when they describe no valid view. False instead of stopping on it.
    public static bool TryTexture(ReadOnlySpan<uint> words, in ShaderImageShape shape, out TextureRequestResolution resolution)
    {
        Span<uint> key = stackalloc uint[8];
        words[..Math.Min(words.Length, 8)].CopyTo(key);
        var cacheKey = new TextureKey(key[0], key[1], key[2], key[3], key[4], key[5], key[6], key[7], shape);
        var cache = _textureCache ??= new Dictionary<TextureKey, TextureRequestResolution>();
        if (cache.TryGetValue(cacheKey, out resolution))
        {
            return true;
        }

        resolution = BuildTexture(key, shape, tolerateInvalid: true, out var invalid);
        if (invalid is not null)
        {
            return false;
        }

        if (cache.Count >= TextureCacheLimit)
        {
            cache.Clear();
        }

        cache[cacheKey] = resolution;
        return true;
    }

    private static TextureRequestResolution Reject(string message, bool tolerateInvalid, out string? invalid)
    {
        if (!tolerateInvalid)
        {
            throw SubmissionScheduler.Fatal(message);
        }

        invalid = message;
        return default;
    }

    private static TextureRequestResolution BuildTexture(ReadOnlySpan<uint> words, in ShaderImageShape shape, bool tolerateInvalid,
        out string? invalid)
    {
        invalid = null;
        Span<uint> padded = stackalloc uint[8];
        words[..Math.Min(words.Length, 8)].CopyTo(padded);
        var descriptor = new TextureDescriptorWords(padded);
        var storage = shape.Storage;
        if (descriptor.BaseAddress == 0)
        {
            return NullTextureResolution(shape);
        }

        var address = descriptor.BaseAddress;
        var width = descriptor.Width + 1;
        var height = descriptor.Height + 1;
        var baseLevel = descriptor.BaseLevel;
        var lastLevel = descriptor.LastLevel;
        if (descriptor.Type is < GuestImageType.Color1D or > GuestImageType.Color2DMsaaArray)
        {
            return Reject($"The texture type is not an image type: address=0x{address:X16} type={(uint)descriptor.Type}.", tolerateInvalid, out invalid);
        }

        var type = TextureType(descriptor.Type);
        var multisampled = IsMultisampledTexture(type);
        var maxMip = shape.R128 ? lastLevel : descriptor.MaxMip;
        var levels = multisampled ? 1 : maxMip + 1;
        var dynamicStorage = storage && shape.DynamicMip;
        var viewLastLevel = !multisampled && !dynamicStorage ? Math.Min(lastLevel, maxMip) : lastLevel;
        var tile = descriptor.TileMode;
        var depthTile = tile == GuestTileMode.Depth;
        var msaaTile = depthTile || tile == GuestTileMode.RenderTarget;
        var msaaArray = type == GuestImageType.Color2DMsaaArray;
     
        if (!multisampled && baseLevel >= levels)
        {
            return NullTextureResolution(shape);
        }

        if ((!multisampled && (baseLevel > viewLastLevel || viewLastLevel >= levels)) ||
            (multisampled &&
             (baseLevel != 0 || lastLevel == 0 || lastLevel > 3 || maxMip != lastLevel || !msaaTile || (descriptor.MsaaDepth && !depthTile) ||
              (!msaaArray && (descriptor.Depth != 0 || descriptor.BaseArray != 0)))))
        {
            return Reject(
                $"The texture mip view is not supported: address=0x{address:X16} type={(uint)type} baseLevel={baseLevel} lastLevel={lastLevel} maxMip={maxMip} " +
                $"levels={levels} tile={(uint)tile} depth={descriptor.Depth} baseArray={descriptor.BaseArray} msaaDepth={descriptor.MsaaDepth} storage={storage}.", tolerateInvalid, out invalid);
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
        if (shape.Cube &&
            (volume || multisampled || width != height || descriptor.BaseArray > descriptor.Depth ||
             (descriptor.Depth - descriptor.BaseArray + 1) % 6 != 0))
        {
            return Reject(
                $"The cubemap view is invalid: address=0x{address:X16} extent={width}x{height} layers={imageLayers} baseArray={descriptor.BaseArray} samples={samples}.", tolerateInvalid, out invalid);
        }
        uint pitch;
        TileSizeAndAlignment size;
        if (multisampled)
        {
            var bytes = GuestPixelFormats.BytesPerElement(format);
            pitch = depthTile ? TileGeometry.DepthPitch(width, bytes, lastLevel) : TileGeometry.RenderTargetPitch(width, bytes, lastLevel);
            if (pitch == 0 || !TileGeometry.TryGetRenderTargetSize(width, height, pitch, bytes, out size, lastLevel) || size.Size > uint.MaxValue / imageLayers)
            {
                return Reject(
                    $"The multisample texture layout is not supported: address=0x{address:X16} extent={width}x{height} bytes={bytes} pitch={pitch} samplesLog2={lastLevel} layers={imageLayers}.", tolerateInvalid, out invalid);
            }

            size = new TileSizeAndAlignment(size.Size * imageLayers, size.Align);
        }
        else
        {
            pitch = TileGeometry.TexturePitch(format, width, tile);
            size = TileGeometry.TextureTotalSize(format, width, height, volume ? depth : imageLayers, levels, tile, volume);
        }

        // T# encodes its byte address in 256-byte units. The layout alignment
        // describes a new surface allocation, not an addressed view inside one.
        if (size.Size == 0 || size.Align == 0 || (address & 0xFFUL) != 0)
        {
            return Reject(
                $"The texture footprint or alignment is invalid: address=0x{address:X16} size=0x{size.Size:X} align=0x{size.Align:X} format={(uint)format} tile={(uint)tile}.", tolerateInvalid, out invalid);
        }

        var pixelFormat = surfaceFormat.HostFormat;
        // Comparison sampling needs a depth image, even when no depth target created it.
        if (shape.DepthCompare && DepthFormatRule.FindByGuestFormat(format) is { } depthFormat)
        {
            pixelFormat = depthFormat.DepthAttachmentFormat;
        }
        // Atomic storage images are declared as UINT in SPIR-V, including float atomics.
        // Write-only SINT storage images are declared as UINT and use the same-size UINT view.
        var storageViewFormat = !storage ? Format.Undefined
            : shape.Atomic || format == GuestPixelFormat.Bits32SInt ? Format.R32Uint
            : shape.NumericClass == TextureNumericClass.Uint && ViewFormatRules.UintStorageFormat(pixelFormat) is var uintView && uintView != Format.Undefined ? uintView
            : ViewFormatRules.SrgbStorageFormat(pixelFormat);
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
        if (!shape.R128 && words.Length >= 8 && descriptor.MetadataCompress &&
            tile != GuestTileMode.Depth && !description.IsDepth)
        {
            _ = TileGeometry.TryGetDccSize(
                width, height, volume ? depth : imageLayers, description.BytesPerBlock, levels, tile,
                out var metadataSize, (uint)System.Numerics.BitOperations.TrailingZeroCount(samples));
            description.Metadata.Kind = MetadataKind.Dcc;
            description.Metadata.Range = new GuestSpan(descriptor.MetadataAddress << 8, metadataSize.Size);
            description.Metadata.DccAlphaMsb = descriptor.DccAlphaMsb;
        }

        if (samples > 1)
        {
            description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size.Size, Pitch = pitch, Height = height };
        }
        else
        {
            if (description.IsVolume && description.TileMode != GuestTileMode.Linear &&
                !TileGeometry.TryGetTiledTextureLayout(new TiledSurfaceDescription(description.GuestFormat, description.TileMode,
                    TileSurfaceDimension.Volume3D, width, height, depth, levels, 1), out _))
            {
                return Reject($"The volume texture layout is not supported: address=0x{address:X16} format={(uint)format} tile={(uint)tile} " +
                    $"extent={width}x{height}x{depth} levels={levels}.", tolerateInvalid, out invalid);
            }

            PopulateTextureMipLayout(ref description);
        }

        // A sampled texture whose descriptor starts past mip 0 is a streamed texture with only
        // those mips resident: the host image holds them alone instead of the whole chain.
        var firstLevel = !storage && !volume && samples == 1 && baseLevel > 0 && baseLevel < levels ? baseLevel : 0u;
        if (firstLevel != 0)
        {
            description.FirstLevel = firstLevel;
            description.Resources = new SubresourceCount(levels - firstLevel, imageLayers);
        }

        if (!shape.Volume && descriptor.BaseArray >= description.Resources.Layers)
        {
            return Reject($"The texture base layer is outside the image: baseLayer={descriptor.BaseArray} layers={description.Resources.Layers} address=0x{address:X16}.",
                tolerateInvalid, out invalid);
        }

        var view = TextureView(descriptor, shape, viewFormat, shaderConversion, viewLevels, description.Resources.Layers, firstLevel);
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

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
    // A conservative byte domain for point-sampled integer data. Tiling changes
    // texel addresses, not the byte positions inside an RGBA8 texel. Including
    // padding overestimates the domain; every byte must still be readable.
    internal static bool TryReadPointSampledByteDomain(ReadOnlySpan<uint> imageWords,
        ReadOnlySpan<uint> samplerWords, uint channels,
        SharpEmu.ShaderCompiler.Resources.GuestWordReader readCleanWord, out uint[] values, bool gathered = false,
        SharpEmu.ShaderCompiler.Resources.ResidentGuestBytesReader? readCleanRange = null)
    {
        values = [];
        if (imageWords.Length != 8 || samplerWords.Length != 4 || channels is 0 or > 15) return false;
        var image = new TextureDescriptorWords(imageWords);
        var sampler = new SamplerDescriptorWords(samplerWords);
        if (image.Format == GuestPixelFormat.Bits8UInt)
            return TryReadR8TexelDomain(imageWords, sampler, channels, readCleanWord, out values, gathered);
        if (image.IsNull || image.Format != GuestPixelFormat.Bits8_8_8_8UInt ||
            image.Type != GuestImageType.Color2D || image.BaseLevel != 0 || image.LastLevel != 0 ||
            image.MaxMip != 0 || image.Depth != 0 || image.BaseArray != 0 || image.MinLod != 0 ||
            image.WriteCompress || image.MetadataCompress ||
            image.TileMode is not (GuestTileMode.Linear or GuestTileMode.Standard4KB) ||
            !gathered && (sampler.MagnifyFilter != (uint)SamplerFilter.Point || sampler.MinifyFilter != (uint)SamplerFilter.Point ||
                sampler.MipFilter > (uint)SamplerMipFilter.Point || sampler.MaxAnisotropyRatio != 0) ||
            sampler.DepthCompareFunction != 0 || sampler.BorderColorType == (uint)SamplerBorderColor.FromTable)
            return false;

        uint storedChannels = 0;
        var found = new bool[256];
        for (var channel = 0; channel < 4; channel++)
        {
            if ((channels & (1u << channel)) == 0) continue;
            var selection = (image.DestinationSelectXyzw >> (channel * 3)) & 7;
            if (selection >= 4) storedChannels |= 1u << (int)(selection - 4);
            else if (selection <= 1) found[selection] = true;
            else return false;
        }
        // Include all built-in integer border values, even when the sampler's
        // address modes make the border unreachable.
        found[0] = found[1] = found[255] = true;
        var request = Texture(imageWords, new ShaderImageShape(false, false, false, false, TextureNumericClass.Uint));
        if (request.Request.Description.PixelFormat != Format.R8G8B8A8Uint) return false;
        var data = request.Request.Description.Data;
        if (data.Size is 0 or > 16 * 1024 * 1024 || (data.Size & 3) != 0) return false;
        Span<byte> block = stackalloc byte[4096];
        for (ulong offset = 0; offset < data.Size;)
        {
            var bytes = readCleanRange is null ? sizeof(uint) : (int)Math.Min((ulong)block.Length, data.Size - offset);
            if (readCleanRange is not null && !readCleanRange(data.Address + offset, block[..bytes], true)) return false;
            for (var within = 0; within < bytes; within += 4)
            {
                uint word;
                if (readCleanRange is null)
                {
                    if (!readCleanWord(data.Address + offset, out word)) return false;
                }
                else word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block[within..]);
                for (var channel = 0; channel < 4; channel++)
                    if ((storedChannels & (1u << channel)) != 0) found[(word >> (channel * 8)) & 255] = true;
            }
            offset += (uint)bytes;
        }
        values = Enumerable.Range(0, 256).Where(value => found[value]).Select(value => (uint)value).ToArray();
        return true;
    }

    internal static bool TryGetStorageAllocationRange(ReadOnlySpan<uint> words, out ulong address, out ulong size)
    {
        address = size = 0;
        if (words.Length != 8) return false;
        var descriptor = new TextureDescriptorWords(words);
        if (descriptor.IsNull || descriptor.Type is not (GuestImageType.Color2D or GuestImageType.Color3D) ||
            descriptor.WriteCompress || descriptor.MetadataCompress) return false;
        var numeric = SharpEmu.ShaderCompiler.Resources.GuestImageFormat.SampledNumericClass(
            SharpEmu.ShaderCompiler.Resources.GuestImageFormat.FormatOf(words));
        if (numeric == SharpEmu.ShaderCompiler.Resources.ImageNumericClass.Unsupported) return false;
        var storageClass = numeric switch
        {
            SharpEmu.ShaderCompiler.Resources.ImageNumericClass.Uint => TextureNumericClass.Uint,
            SharpEmu.ShaderCompiler.Resources.ImageNumericClass.Sint => TextureNumericClass.Sint,
            _ => TextureNumericClass.Float,
        };
        var shape = new ShaderImageShape(false, false, true, true, storageClass)
        {
            Volume = descriptor.Type == GuestImageType.Color3D,
        };
        var resolved = Texture(words, shape);
        var data = resolved.Request.Description.Data;
        if (resolved.Request.Description.PixelFormat == Format.Undefined ||
            resolved.Request.Description.GuestFormat != descriptor.Format ||
            data.Address != descriptor.BaseAddress || data.Size == 0 ||
            data.Address >= 1ul << 48 || data.Size > (1ul << 48) - data.Address) return false;
        address = data.Address;
        size = data.Size;
        return true;
    }

    private static bool TryReadR8TexelDomain(ReadOnlySpan<uint> imageWords, in SamplerDescriptorWords sampler,
        uint channels, SharpEmu.ShaderCompiler.Resources.GuestWordReader reader, out uint[] values, bool gathered)
    {
        values = [];
        var image = new TextureDescriptorWords(imageWords);
        if (image.IsNull || image.Type != GuestImageType.Color2D || image.BaseLevel != 0 || image.LastLevel != 0 ||
            image.MaxMip != 0 || image.Depth != 0 || image.BaseArray != 0 || image.MinLod != 0 ||
            image.WriteCompress || image.MetadataCompress || image.TileMode is not (GuestTileMode.Linear or GuestTileMode.Standard4KB) ||
            sampler.DepthCompareFunction != 0 || sampler.BorderColorType == (uint)SamplerBorderColor.FromTable ||
            !gathered && (sampler.MaxAnisotropyRatio != 0 || sampler.MagnifyFilter != (uint)SamplerFilter.Point ||
                sampler.MinifyFilter != (uint)SamplerFilter.Point || sampler.MipFilter > (uint)SamplerMipFilter.Point)) return false;
        var description = Texture(imageWords,
            new ShaderImageShape(false, false, false, false, TextureNumericClass.Uint)).Request.Description;
        var data = description.Data;
        if (data.Size is 0 or > 16 * 1024 * 1024 || data.Address >= 1ul << 48 || data.Size > (1ul << 48) - data.Address) return false;
        var stored = false;
        var found = new bool[256];
        for (var channel = 0; channel < 4; channel++)
        {
            if ((channels & (1u << channel)) == 0) continue;
            var selection = (image.DestinationSelectXyzw >> (channel * 3)) & 7;
            if (selection == 4) stored = true;
            else if (selection <= 1) found[selection] = true;
            else return false;
        }
        // Built-in integer borders remain possible even when absent from the texels.
        found[0] = found[1] = true;
        TiledSurfaceLayout? surface = null;
        if (image.TileMode == GuestTileMode.Standard4KB &&
            (!TileGeometry.TryGetTiledTextureLayout(new(image.Format, image.TileMode, TileSurfaceDimension.Flat2D,
                description.Extent.Width, description.Extent.Height), out surface) || surface.TotalSize != data.Size)) return false;
        var words = new Dictionary<ulong, uint>();
        if (stored)
        for (uint y = 0; y < description.Extent.Height; y++)
        for (uint x = 0; x < description.Extent.Width; x++)
        {
            ulong offset;
            if (surface is null) offset = (ulong)y * description.MipLayout[0].Pitch + x;
            else
            {
                var block = surface.Texture.Block;
                var mip = surface.Mips[0];
                if (!TileGeometry.TryGetBlockOffset(block, x % block.BlockWidth + mip.TailX,
                    y % block.BlockHeight + mip.TailY, 0, out var within)) return false;
                offset = mip.Offset + ((ulong)(y / block.BlockHeight) * (mip.PaddedWidth / block.BlockWidth) +
                    x / block.BlockWidth) * block.BlockSize + within;
            }
            if (offset >= data.Size) return false;
            var at = data.Address + (offset & ~3ul);
            if (!words.TryGetValue(at, out var word))
            {
                if (!reader(at, out word)) return false;
                words.Add(at, word);
            }
            found[(word >> (int)((offset & 3) * 8)) & 255] = true;
        }
        values = Enumerable.Range(0, 256).Where(value => found[value]).Select(value => (uint)value).ToArray();
        return true;
    }

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
        description.Resources = SubresourceCount.Single;
        description.BytesPerBlock = 4;
        description.Samples = shape.Multisampled ? 4u : 1u;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 0, Pitch = 1, Height = 1 };
        var view = ImageViewDescription.Default with
        {
            Format = format,
            Type = shape.Volume
                ? ImageViewType.Type3D
                : shape.OneDimensional
                    ? shape.Arrayed ? ImageViewType.Type1DArray : ImageViewType.Type1D
                    : shape.Arrayed ? ImageViewType.Type2DArray : ImageViewType.Type2D,
            Aspect = depthCompare ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit,
            Usage = storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit,
        };
        return new ImageRequest(description, view, storage ? ImageRole.StorageImage : ImageRole.Texture);
    }

    // A depth placeholder reads its value into red, as sampled depth views do.
    private static TextureRequestResolution NullTextureResolution(in ShaderImageShape shape)
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
        in TextureDescriptorWords descriptor, in ShaderImageShape shape, Format format, bool shaderConversion, uint viewLevels, uint imageLayers)
    {
        var mapping = shape.Storage || shaderConversion ? default : ViewFormatRules.ComponentMapping(DestinationSwizzle(descriptor));
        var usage = shape.Storage ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit;
        if (shape.Volume)
        {
            return WithMinLod(new ImageViewDescription(format, ImageViewType.Type3D, ImageAspectFlags.ColorBit, descriptor.BaseLevel, viewLevels, 0, 1, mapping, usage), descriptor, shape);
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
        return WithMinLod(new ImageViewDescription(format, type, ImageAspectFlags.ColorBit, descriptor.BaseLevel, viewLevels, baseLayer, layerCount, mapping, usage), descriptor, shape);
    }

    // MIN_LOD (4.8 fixed point, absolute mip levels) keeps sampling off mips a streamed texture has not
    // loaded yet; ignoring it sampled the unloaded mip 0 of Astro Bot's stadium sky and bloom spread its
    // garbage HDR values over the screen. Storage views address levels explicitly and are not clamped.
    private static ImageViewDescription WithMinLod(ImageViewDescription view, in TextureDescriptorWords descriptor, in ShaderImageShape shape)
    {
        var minLod = descriptor.MinLod / 256f;
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

        var resolution = BuildTexture(key, shape);
        if (cache.Count >= TextureCacheLimit)
        {
            cache.Clear();
        }

        cache[cacheKey] = resolution;
        return resolution;
    }

    private static TextureRequestResolution BuildTexture(ReadOnlySpan<uint> words, in ShaderImageShape shape)
    {
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
        if (shape.Cube &&
            (volume || multisampled || width != height || descriptor.BaseArray > descriptor.Depth ||
             (descriptor.Depth - descriptor.BaseArray + 1) % 6 != 0))
        {
            throw SubmissionScheduler.Fatal(
                $"The cubemap view is invalid: address=0x{address:X16} extent={width}x{height} layers={imageLayers} baseArray={descriptor.BaseArray} samples={samples}.");
        }
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

        // T# encodes its byte address in 256-byte units. The layout alignment
        // describes a new surface allocation, not an addressed view inside one.
        if (size.Size == 0 || size.Align == 0 || (address & 0xFFUL) != 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The texture footprint or alignment is invalid: address=0x{address:X16} size=0x{size.Size:X} align=0x{size.Align:X} format={(uint)format} tile={(uint)tile}.");
        }

        var pixelFormat = surfaceFormat.HostFormat;
        // Comparison sampling needs a depth image, even when no depth target created it.
        if (shape.DepthCompare && DepthFormatRule.FindByGuestFormat(format) is { } depthFormat)
        {
            pixelFormat = depthFormat.DepthAttachmentFormat;
        }
        // Atomic storage images are declared as UINT in SPIR-V, including float atomics.
        var storageViewFormat = storage && (shape.Atomic || format == GuestPixelFormat.Bits32SInt) ? Format.R32Uint : ViewFormatRules.SrgbStorageFormat(pixelFormat);
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
        var metadataAddress = descriptor.MetadataAddress << 8;
        if (!storage && type == GuestImageType.Color2D && samples == 1 && levels == 1 && tile == GuestTileMode.RenderTarget && descriptor.MetadataCompress &&
            metadataAddress != 0 && metadataAddress < TrackerLayout.SpaceBytes)
        {
            description.Metadata.Kind = MetadataKind.Dcc;
            description.Metadata.Range = new GuestSpan(metadataAddress, 0);
        }

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

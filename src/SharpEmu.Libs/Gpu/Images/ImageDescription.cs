// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public enum DisplayCompression : byte
{
    Uncompressed,
    Dcc256_256_0,
    Dcc256_64_64,
    Unsupported,
}

public enum MetadataKind : byte
{
    None,
    Htile,
    Dcc,
}

public struct MetadataDescription
{
    public GuestSpan Range;
    public MetadataKind Kind;
    public uint Control;
    public DisplayCompression Compression;
    public bool StencilCompressed;
}

public readonly record struct SubresourceCount(uint Levels, uint Layers) : IComparable<SubresourceCount>
{
    public static SubresourceCount Single => new(1, 1);

    // Levels order first, then layers.
    public int CompareTo(SubresourceCount other) => Levels != other.Levels ? Levels.CompareTo(other.Levels) : Layers.CompareTo(other.Layers);

    public static bool operator <(SubresourceCount left, SubresourceCount right) => left.CompareTo(right) < 0;

    public static bool operator >(SubresourceCount left, SubresourceCount right) => left.CompareTo(right) > 0;

    public static SubresourceCount Max(SubresourceCount left, SubresourceCount right) => left < right ? right : left;
}

public readonly record struct SubresourceRange(uint BaseLevel, uint LevelCount, uint BaseLayer, uint LayerCount)
{
    public static SubresourceRange First => new(0, 1, 0, 1);
}

public struct MipLevelLayout
{
    public ulong Offset;
    public ulong Size;
    public uint Pitch;
    public uint Height;
}

[InlineArray(ImageDescription.MaxLevels)]
public struct MipLevelLayouts
{
    private MipLevelLayout _first;
}

// Guest geometry, formats and memory placement of one image; copied by value.
public struct ImageDescription
{
    public const int MaxLevels = 16;

    public GuestSpan Data;
    public GuestSpan Stencil;
    public MetadataDescription Metadata;
    public uint HtileClearMask;
    public Format PixelFormat;
    public GuestPixelFormat GuestFormat;
    public GuestImageType Type;
    public Extent3D Extent;
    public SubresourceCount Resources;
    public uint Pitch;
    public uint BytesPerBlock;
    public uint Samples;
    public GuestTileMode TileMode;
    public bool Bgra16;
    public MipLevelLayouts MipLayout;

    public static ImageDescription Create() => new()
    {
        HtileClearMask = uint.MaxValue,
        Type = GuestImageType.Color2D,
        Extent = new Extent3D(1, 1, 1),
        Resources = SubresourceCount.Single,
        Samples = 1,
    };

    // A range is empty only when both fields are zero; a valid range has a nonzero address and size.
    public static bool IsEmptyRange(GuestSpan range) => range.Address == 0 && range.Size == 0;

    public static bool IsValidRange(GuestSpan range) => range.Address != 0 && range.Size != 0 && range.IsValid;

    public readonly bool HasStencil => !IsEmptyRange(Stencil);

    public readonly bool HasMetadata => Metadata.Kind != MetadataKind.None;

    public readonly bool IsDepth => DepthFormatRule.AspectTransferFormat(PixelFormat) != Format.Undefined;

    public readonly bool IsBlock => GuestPixelFormats.BlockCompressedBytes(GuestFormat) != 0;

    public readonly bool IsTiled => TileMode != GuestTileMode.Linear;

    public readonly bool IsVolume => Type == GuestImageType.Color3D;

    public readonly bool IsLayered => !IsVolume && Resources.Layers > 1;

    public readonly uint TransferLayers => IsVolume ? Extent.Depth : Resources.Layers;

    public readonly Extent2D BlockExtent
    {
        get
        {
            var shift = IsBlock ? 2 : 0;
            return new Extent2D(Pitch >> shift, Extent.Height >> shift);
        }
    }

    public readonly bool IsCompatible(in ImageDescription other) =>
        PixelFormat == other.PixelFormat && Samples == other.Samples && BytesPerBlock == other.BytesPerBlock;

    // Return the matching mip level in the containing image, or -1 if none matches.
    public readonly int FindMatchingMipLevel(in ImageDescription container)
    {
        if (container.Resources.Levels > MaxLevels)
        {
            return -1;
        }

        for (uint level = 0; level < container.Resources.Levels; level++)
        {
            if (FindMatchingArraySlice(container, (int)level) >= 0)
            {
                return (int)level;
            }
        }

        return -1;
    }

    // Return the matching slice in the containing mip level, or -1 if none matches.
    public readonly int FindMatchingArraySlice(in ImageDescription container, int mip)
    {
        if (!IsCompatible(container) || TileMode != container.TileMode || Type != container.Type || IsVolume ||
            Resources.Levels != 1 || Resources.Layers == 0 || container.Resources.Layers == 0 || mip < 0 ||
            (uint)mip >= container.Resources.Levels || container.Resources.Levels > MaxLevels ||
            !IsValidRange(Data) || !IsValidRange(container.Data) || Data.Address < container.Data.Address || Data.End > container.Data.End)
        {
            return -1;
        }

        if (HasStencil != container.HasStencil ||
            (HasStencil && (Stencil.Address < container.Stencil.Address || Stencil.End > container.Stencil.End)))
        {
            return -1;
        }

        var layout = container.MipLayout[mip];
        if (Extent.Width != Math.Max(container.Extent.Width >> mip, 1) || Extent.Height != Math.Max(container.Extent.Height >> mip, 1) ||
            Extent.Depth != container.Extent.Depth || MipLayout[0].Pitch != layout.Pitch || MipLayout[0].Height != layout.Height ||
            layout.Size == 0 || layout.Size % container.Resources.Layers != 0 ||
            container.Data.Size % container.Resources.Layers != 0 || Data.Size % Resources.Layers != 0)
        {
            return -1;
        }

        // PS5 array slices contain the whole reversed mip chain, rather than
        // storing all slices of one mip contiguously.
        var sliceStride = container.Data.Size / container.Resources.Layers;
        var childStride = Data.Size / Resources.Layers;
        if (childStride != layout.Size / container.Resources.Layers || layout.Offset >= sliceStride ||
            childStride > sliceStride - layout.Offset || (Resources.Layers > 1 && childStride != sliceStride))
        {
            return -1;
        }

        var addressDelta = Data.Address - container.Data.Address;
        if (addressDelta < layout.Offset || (addressDelta - layout.Offset) % sliceStride != 0)
        {
            return -1;
        }

        var layer = (addressDelta - layout.Offset) / sliceStride;
        if (layer > int.MaxValue || layer >= container.Resources.Layers || Resources.Layers > container.Resources.Layers - layer)
        {
            return -1;
        }

        return (int)layer;
    }

    public readonly bool IsSupportedDepthTarget
    {
        get
        {
            var rule = DepthFormatRule.FindByGuestFormat(GuestFormat);
            return rule != null && BytesPerBlock == rule.BytesPerElement &&
                   (HasStencil ? rule.IsStencilAttachmentFormat(PixelFormat) : PixelFormat == rule.DepthAttachmentFormat);
        }
    }

    public readonly bool IsSupportedDepthPlaneReadback
    {
        get
        {
            if (!IsSupportedDepthTarget)
            {
                return false;
            }

            var transferBytes = DepthFormatRule.AspectTransferBytes(PixelFormat);
            return transferBytes == BytesPerBlock || (BytesPerBlock == sizeof(ushort) && transferBytes == sizeof(uint));
        }
    }

    public readonly bool IsSupportedDisplayFormat => DisplayFormatRule.Supports(this);

    public readonly bool IsSupportedStandard64RenderTarget
    {
        get
        {
            if (TileMode != GuestTileMode.Standard64KB || Data.Address == 0 || (Data.Address & 0xffff) != 0 ||
                Extent.Width == 0 || Extent.Height == 0 || BytesPerBlock != 4 || Resources.Levels != 1 ||
                Resources.Layers != 1 || Samples != 1)
            {
                return false;
            }

            var expectedPitch = ((ulong)Extent.Width + 127) & ~127UL;
            var paddedHeight = ((ulong)Extent.Height + 127) & ~127UL;
            return expectedPitch <= uint.MaxValue && Pitch == expectedPitch &&
                   expectedPitch <= ulong.MaxValue / paddedHeight / BytesPerBlock &&
                   Data.Size == expectedPitch * paddedHeight * BytesPerBlock;
        }
    }

    public readonly bool IsTiledRenderTarget => TileMode == GuestTileMode.RenderTarget || IsSupportedStandard64RenderTarget;

    public static bool IsSupportedDisplayRenderTargetTileMode(GuestTileMode tileMode) => tileMode == GuestTileMode.RenderTarget;

    public static bool CanUseDisplayNativeWithoutUpload(DisplayCompression compression, bool renderTarget, bool gpuModified, bool guestModified) =>
        compression is not (DisplayCompression.Uncompressed or DisplayCompression.Unsupported) && !guestModified && (renderTarget || gpuModified);

    public static DisplayCompression ClassifyDisplayCompression(bool compressed, ulong metadataAddress, uint dccControl, ulong dccClearColor)
    {
        const uint Dcc256_256_0 = 0x00000048;
        const uint Dcc256_64_64 = 0x00000208;
        if (!compressed)
        {
            return metadataAddress == 0 && dccControl == 0 && dccClearColor == 0 ? DisplayCompression.Uncompressed : DisplayCompression.Unsupported;
        }

        if (metadataAddress == 0 || (metadataAddress & 0xff) != 0 || dccClearColor != 0)
        {
            return DisplayCompression.Unsupported;
        }

        return dccControl switch
        {
            Dcc256_256_0 => DisplayCompression.Dcc256_256_0,
            Dcc256_64_64 => DisplayCompression.Dcc256_64_64,
            _ => DisplayCompression.Unsupported,
        };
    }

    // The size-equivalent guest format used to move render-target bytes through the tiler.
    public static GuestPixelFormat RenderTargetTransferFormat(uint bytesPerElement) => bytesPerElement switch
    {
        1 => GuestPixelFormat.Bits8UNorm,
        2 => GuestPixelFormat.Bits16UNorm,
        4 => GuestPixelFormat.Bits32Float,
        8 => GuestPixelFormat.Bits16_16_16_16Float,
        16 => GuestPixelFormat.Bits32_32_32_32Float,
        _ => throw SubmissionScheduler.Fatal($"No transfer format exists for this render-target element size: bytes={bytesPerElement}."),
    };

    private static bool IsValidOptionalRange(GuestSpan range) => IsEmptyRange(range) || IsValidRange(range);

    // Rejects geometry, format and metadata combinations the image store cannot hold.
    public readonly void Validate()
    {
        if (!IsValidOptionalRange(Data))
        {
            throw SubmissionScheduler.Fatal($"The image data range is invalid: address=0x{Data.Address:X16} size=0x{Data.Size:X16}.");
        }

        if (!IsValidOptionalRange(Stencil))
        {
            throw SubmissionScheduler.Fatal($"The image stencil range is invalid: address=0x{Stencil.Address:X16} size=0x{Stencil.Size:X16}.");
        }

        if (PixelFormat == Format.Undefined)
        {
            if (IsEmptyRange(Data) || HasStencil || Metadata.Kind != MetadataKind.None || !IsEmptyRange(Metadata.Range) ||
                Metadata.Control != 0 || Metadata.Compression != DisplayCompression.Uncompressed || Metadata.StencilCompressed ||
                Extent.Width == 0 || Extent.Height == 0 || Extent.Depth == 0 ||
                Resources.Levels != 1 || Resources.Layers != 1 || Samples != 1 || Pitch != 0 || BytesPerBlock != 0)
            {
                throw SubmissionScheduler.Fatal("A stencil-association image must carry only a data range and an extent.");
            }

            return;
        }

        if (Extent.Width == 0 || Extent.Height == 0 || Extent.Depth == 0 || Resources.Levels == 0 ||
            Resources.Levels > MaxLevels || Resources.Layers == 0 || VulkanSampleCount(Samples) == 0 ||
            BytesPerBlock == 0 || (Data.Address != 0 && Pitch == 0))
        {
            throw SubmissionScheduler.Fatal($"The image geometry or format is invalid: extent={Extent.Width}x{Extent.Height}x{Extent.Depth} levels={Resources.Levels} layers={Resources.Layers} samples={Samples} bytesPerBlock={BytesPerBlock} pitch={Pitch}.");
        }

        switch (Type)
        {
            case GuestImageType.Color1D when Extent.Height != 1 || Extent.Depth != 1:
                throw SubmissionScheduler.Fatal($"A 1D image must have a height and depth of one: extent={Extent.Width}x{Extent.Height}x{Extent.Depth}.");
            case GuestImageType.Color3D when Resources.Layers != 1:
                throw SubmissionScheduler.Fatal($"A 3D image cannot have array layers: layers={Resources.Layers}.");
            case GuestImageType.Color2D when Extent.Depth != 1:
                throw SubmissionScheduler.Fatal($"A 2D image must have a depth of one: extent={Extent.Width}x{Extent.Height}x{Extent.Depth}.");
            case GuestImageType.Color1D:
            case GuestImageType.Color2D:
            case GuestImageType.Color3D:
                break;
            default:
                throw SubmissionScheduler.Fatal($"The image type is not a base type: type={(uint)Type}.");
        }

        if (Samples > 1 && Resources.Levels != 1)
        {
            throw SubmissionScheduler.Fatal($"A multisampled image cannot have mip levels: samples={Samples} levels={Resources.Levels}.");
        }

        if (Metadata.StencilCompressed && !HasStencil)
        {
            throw SubmissionScheduler.Fatal("Compressed stencil metadata needs a stencil plane.");
        }

        switch (Metadata.Kind)
        {
            case MetadataKind.None:
                if (!IsEmptyRange(Metadata.Range) || Metadata.Control != 0 ||
                    Metadata.Compression != DisplayCompression.Uncompressed || Metadata.StencilCompressed)
                {
                    throw SubmissionScheduler.Fatal("An image without metadata carries metadata state.");
                }

                break;
            case MetadataKind.Htile:
                if (!IsValidRange(Metadata.Range) || Metadata.Compression != DisplayCompression.Uncompressed)
                {
                    throw SubmissionScheduler.Fatal($"The HTile metadata is invalid: address=0x{Metadata.Range.Address:X16} size=0x{Metadata.Range.Size:X16}.");
                }

                break;
            case MetadataKind.Dcc:
                if (Metadata.Range.Address == 0 || Metadata.Range.Address >= TrackerLayout.SpaceBytes ||
                    Metadata.Range.Size > TrackerLayout.SpaceBytes - Metadata.Range.Address ||
                    Metadata.Compression == DisplayCompression.Unsupported)
                {
                    throw SubmissionScheduler.Fatal($"The DCC metadata is invalid: address=0x{Metadata.Range.Address:X16} size=0x{Metadata.Range.Size:X16} compression={Metadata.Compression}.");
                }

                break;
        }
    }

    public static SampleCountFlags VulkanSampleCount(uint samples) => samples switch
    {
        1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        4 => SampleCountFlags.Count4Bit,
        8 => SampleCountFlags.Count8Bit,
        _ => 0,
    };
}

// The host depth and stencil attachment formats of each guest depth format.
public sealed record DepthFormatRule(GuestDepthFormat DepthFormat, GuestPixelFormat GuestFormat, uint BytesPerElement, Format DepthAttachmentFormat, Format[] StencilAttachmentFormats)
{
    public static readonly DepthFormatRule[] Table =
    [
        new(GuestDepthFormat.Z16, GuestPixelFormat.Bits16UNorm, 2, Format.D16Unorm, [Format.D16UnormS8Uint, Format.D24UnormS8Uint, Format.D32SfloatS8Uint]),
        new(GuestDepthFormat.Z32Float, GuestPixelFormat.Bits32Float, 4, Format.D32Sfloat, [Format.D32SfloatS8Uint, Format.Undefined, Format.Undefined]),
    ];

    public static DepthFormatRule? Find(GuestDepthFormat depthFormat) => Array.Find(Table, rule => rule.DepthFormat == depthFormat);

    public static DepthFormatRule? FindByGuestFormat(GuestPixelFormat guestFormat) => Array.Find(Table, rule => rule.GuestFormat == guestFormat);

    public bool IsStencilAttachmentFormat(Format format) =>
        format != Format.Undefined && Array.IndexOf(StencilAttachmentFormats, format) >= 0;

    public Format AttachmentFormat(bool hasStencil) => hasStencil ? StencilAttachmentFormats[0] : DepthAttachmentFormat;

    public static Format AttachmentFormat(GuestDepthFormat depthFormat, GuestStencilFormat stencilFormat)
    {
        bool hasStencil;
        switch (stencilFormat)
        {
            case GuestStencilFormat.Invalid: hasStencil = false; break;
            case GuestStencilFormat.Stencil8UInt: hasStencil = true; break;
            default: return Format.Undefined;
        }

        return Find(depthFormat)?.AttachmentFormat(hasStencil) ?? Format.Undefined;
    }

    public const ImageUsageFlags DepthTargetUsage =
        ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

    public static Format AspectTransferFormat(Format format) => format switch
    {
        Format.D16Unorm or Format.D16UnormS8Uint => Format.D16Unorm,
        Format.D24UnormS8Uint => Format.X8D24UnormPack32,
        Format.D32Sfloat or Format.D32SfloatS8Uint => Format.D32Sfloat,
        _ => Format.Undefined,
    };

    public static uint AspectTransferBytes(Format format) => AspectTransferFormat(format) switch
    {
        Format.D16Unorm => 2,
        Format.X8D24UnormPack32 or Format.D32Sfloat => 4,
        _ => 0,
    };

    public static uint EncodeD16AsD24(ushort value) => (uint)(((ulong)value * 0x00ffffff + 0x7fff) / 0xffff);

    public static uint EncodeD16AsD32(ushort value) => BitConverter.SingleToUInt32Bits(value / 65535.0f);
}

public readonly record struct DisplayPixelFormat(Format HostFormat, GuestPixelFormat GuestFormat, uint BytesPerElement, bool Bgra16);

// The six display surface pixel formats the store accepts.
public static class DisplayFormatRule
{
    public static readonly (ulong PixelFormat, DisplayPixelFormat Info)[] Table =
    [
        (0x8000000022000000, new DisplayPixelFormat(Format.R8G8B8A8Srgb, GuestPixelFormat.Bits8_8_8_8Srgb, 4, false)),
        (0x8000000000000000, new DisplayPixelFormat(Format.B8G8R8A8Srgb, GuestPixelFormat.Bits8_8_8_8Srgb, 4, false)),
        (0x8100000022000000, new DisplayPixelFormat(Format.A2B10G10R10UnormPack32, GuestPixelFormat.Bits10_10_10_2UNorm, 4, false)),
        (0x8100000000000000, new DisplayPixelFormat(Format.A2R10G10B10UnormPack32, GuestPixelFormat.Bits10_10_10_2UNorm, 4, false)),
        (0xc001000622000000, new DisplayPixelFormat(Format.R16G16B16A16Sfloat, GuestPixelFormat.Bits16_16_16_16Float, 8, false)),
        (0xc001000600000000, new DisplayPixelFormat(Format.R16G16B16A16Sfloat, GuestPixelFormat.Bits16_16_16_16Float, 8, true)),
    ];

    public static bool TryDecode(ulong pixelFormat, out DisplayPixelFormat info)
    {
        foreach (var (candidate, decoded) in Table)
        {
            if (candidate == pixelFormat)
            {
                info = decoded;
                return true;
            }
        }

        info = default;
        return false;
    }

    public static bool Supports(in ImageDescription description)
    {
        foreach (var (_, info) in Table)
        {
            if (description.PixelFormat == info.HostFormat && description.GuestFormat == info.GuestFormat &&
                description.BytesPerBlock == info.BytesPerElement && description.Bgra16 == info.Bgra16)
            {
                return true;
            }
        }

        return false;
    }
}

// Decoders for the packed 32-bit clear values guest compute fills carry.
public static class PackedClearValue
{
    public static bool TryDecodeColor(Format format, uint packed, out ClearColorValue clear)
    {
        static float Unorm8(uint value) => (value & 0xff) / 255.0f;
        static float Srgb8(uint value)
        {
            var encoded = (value & 0xff) / 255.0f;
            return encoded <= 0.04045f ? encoded / 12.92f : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }

        clear = default;
        switch (format)
        {
            // A single-plane float target carries its clear as raw float bits.
            case Format.R32Sfloat:
                clear.Float32_0 = BitConverter.UInt32BitsToSingle(packed);
                return true;
            case Format.R32Uint:
                clear.Uint32_0 = packed;
                return true;
            case Format.R32Sint:
                clear.Int32_0 = (int)packed;
                return true;
            case Format.R8G8B8A8Srgb:
                clear.Float32_0 = Srgb8(packed);
                clear.Float32_1 = Srgb8(packed >> 8);
                clear.Float32_2 = Srgb8(packed >> 16);
                clear.Float32_3 = Unorm8(packed >> 24);
                return true;
            case Format.B8G8R8A8Srgb:
                clear.Float32_0 = Srgb8(packed >> 16);
                clear.Float32_1 = Srgb8(packed >> 8);
                clear.Float32_2 = Srgb8(packed);
                clear.Float32_3 = Unorm8(packed >> 24);
                return true;
            case Format.R8G8B8A8Unorm:
                clear.Float32_0 = Unorm8(packed);
                clear.Float32_1 = Unorm8(packed >> 8);
                clear.Float32_2 = Unorm8(packed >> 16);
                clear.Float32_3 = Unorm8(packed >> 24);
                return true;
            case Format.B8G8R8A8Unorm:
                clear.Float32_0 = Unorm8(packed >> 16);
                clear.Float32_1 = Unorm8(packed >> 8);
                clear.Float32_2 = Unorm8(packed);
                clear.Float32_3 = Unorm8(packed >> 24);
                return true;
            case Format.A2B10G10R10UnormPack32:
                clear.Float32_0 = (packed & 0x3ff) / 1023.0f;
                clear.Float32_1 = ((packed >> 10) & 0x3ff) / 1023.0f;
                clear.Float32_2 = ((packed >> 20) & 0x3ff) / 1023.0f;
                clear.Float32_3 = ((packed >> 30) & 0x3) / 3.0f;
                return true;
            case Format.A2R10G10B10UnormPack32:
                clear.Float32_0 = ((packed >> 20) & 0x3ff) / 1023.0f;
                clear.Float32_1 = ((packed >> 10) & 0x3ff) / 1023.0f;
                clear.Float32_2 = (packed & 0x3ff) / 1023.0f;
                clear.Float32_3 = ((packed >> 30) & 0x3) / 3.0f;
                return true;
            default:
                return false;
        }
    }

    public static bool TryDecodeStencil(uint packed, out byte clear)
    {
        clear = (byte)packed;
        return packed == clear * 0x01010101u;
    }

    public static bool TryDecodeDepth(Format format, uint packed, out float clear)
    {
        clear = 0;
        if (format is not (Format.D32Sfloat or Format.D32SfloatS8Uint))
        {
            return false;
        }

        var value = BitConverter.UInt32BitsToSingle(packed);
        if (!float.IsFinite(value) || value < 0.0f || value > 1.0f)
        {
            return false;
        }

        clear = value;
        return true;
    }
}

// Byte-exact and tracker-page overlap tests between guest ranges.
public static class GuestRangeOverlap
{
    private static void Require(ulong left, ulong leftSize, ulong right, ulong rightSize)
    {
        if (leftSize == 0 || rightSize == 0 || left > ulong.MaxValue - leftSize || right > ulong.MaxValue - rightSize)
        {
            throw SubmissionScheduler.Fatal($"An image overlap range is invalid: left=0x{left:X16}+0x{leftSize:X} right=0x{right:X16}+0x{rightSize:X}.");
        }
    }

    public static bool Bytes(ulong left, ulong leftSize, ulong right, ulong rightSize)
    {
        Require(left, leftSize, right, rightSize);
        return left < right + rightSize && right < left + leftSize;
    }

    public static bool Bytes(GuestSpan left, GuestSpan right) => Bytes(left.Address, left.Size, right.Address, right.Size);

    public static bool Pages(ulong left, ulong leftSize, ulong right, ulong rightSize)
    {
        Require(left, leftSize, right, rightSize);
        var leftFirst = left / TrackerLayout.PageBytes;
        var leftLast = (left + leftSize - 1) / TrackerLayout.PageBytes;
        var rightFirst = right / TrackerLayout.PageBytes;
        var rightLast = (right + rightSize - 1) / TrackerLayout.PageBytes;
        return leftFirst <= rightLast && rightFirst <= leftLast;
    }

    public static bool Pages(GuestSpan left, GuestSpan right) => Pages(left.Address, left.Size, right.Address, right.Size);
}

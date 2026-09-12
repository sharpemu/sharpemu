// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// The selected view of an image: format, shape, aspect, subresource window and swizzle.
public readonly record struct ImageViewDescription(
    Format Format,
    ImageViewType Type,
    ImageAspectFlags Aspect,
    uint BaseLevel,
    uint LevelCount,
    uint BaseLayer,
    uint LayerCount,
    ComponentMapping Mapping,
    ImageUsageFlags Usage)
{
    public static ImageViewDescription Default => new(Format.Undefined, ImageViewType.Type2D, ImageAspectFlags.ColorBit, 0, 1, 0, 1, default, ImageUsageFlags.SampledBit);

    public bool Equals(ImageViewDescription other) =>
        Format == other.Format && Type == other.Type && Aspect == other.Aspect &&
        BaseLevel == other.BaseLevel && LevelCount == other.LevelCount &&
        BaseLayer == other.BaseLayer && LayerCount == other.LayerCount &&
        Mapping.R == other.Mapping.R && Mapping.G == other.Mapping.G &&
        Mapping.B == other.Mapping.B && Mapping.A == other.Mapping.A && Usage == other.Usage;

    public override int GetHashCode() =>
        HashCode.Combine(Format, Type, Aspect, BaseLevel, LevelCount, BaseLayer, LayerCount, HashCode.Combine(Mapping.R, Mapping.G, Mapping.B, Mapping.A, Usage));
}

// Which host formats may view which image formats, and which guest swizzles are legal.
public static class ViewFormatRules
{
    private const uint None = 0;
    private const uint Bit8 = 1u << 0;
    private const uint Bit16 = 1u << 1;
    private const uint Bit24 = 1u << 2;
    private const uint Bit32 = 1u << 3;
    private const uint Bit48 = 1u << 4;
    private const uint Bit64 = 1u << 5;
    private const uint Bit96 = 1u << 6;
    private const uint Bit128 = 1u << 7;
    private const uint Bit192 = 1u << 8;
    private const uint Bit256 = 1u << 9;
    private const uint Bc1Rgb = 1u << 10;
    private const uint Bc1Rgba = 1u << 11;
    private const uint Bc2 = 1u << 12;
    private const uint Bc3 = 1u << 13;
    private const uint Bc4 = 1u << 14;
    private const uint Bc5 = 1u << 15;
    private const uint Bc6h = 1u << 16;
    private const uint Bc7 = 1u << 17;
    private const uint D16 = 1u << 18;
    private const uint D16S8 = 1u << 19;
    private const uint D24 = 1u << 20;
    private const uint D24S8 = 1u << 21;
    private const uint D32 = 1u << 22;
    private const uint D32S8 = 1u << 23;
    private const uint S8 = 1u << 24;

    private static uint FormatClass(Format format) => format switch
    {
        Format.R4G4UnormPack8 or Format.R8Sint or Format.R8SNorm or Format.R8Srgb or Format.R8Sscaled or
        Format.R8Uint or Format.R8Unorm or Format.R8Uscaled => Bit8,

        Format.A1R5G5B5UnormPack16 or Format.A4B4G4R4UnormPack16 or Format.A4R4G4B4UnormPack16 or
        Format.B4G4R4A4UnormPack16 or Format.B5G5R5A1UnormPack16 or Format.B5G6R5UnormPack16 or
        Format.R10X6UnormPack16 or Format.R12X4UnormPack16 or Format.R16Sfloat or Format.R16Sint or
        Format.R16SNorm or Format.R16Sscaled or Format.R16Uint or Format.R16Unorm or Format.R16Uscaled or
        Format.R4G4B4A4UnormPack16 or Format.R5G5B5A1UnormPack16 or Format.R5G6B5UnormPack16 or
        Format.R8G8Sint or Format.R8G8SNorm or Format.R8G8Srgb or Format.R8G8Sscaled or Format.R8G8Uint or
        Format.R8G8Unorm or Format.R8G8Uscaled => Bit16,

        Format.B8G8R8Sint or Format.B8G8R8SNorm or Format.B8G8R8Srgb or Format.B8G8R8Sscaled or
        Format.B8G8R8Uint or Format.B8G8R8Unorm or Format.B8G8R8Uscaled or Format.R8G8B8Sint or
        Format.R8G8B8SNorm or Format.R8G8B8Srgb or Format.R8G8B8Sscaled or Format.R8G8B8Uint or
        Format.R8G8B8Unorm or Format.R8G8B8Uscaled => Bit24,

        Format.A2B10G10R10SintPack32 or Format.A2B10G10R10SNormPack32 or Format.A2B10G10R10SscaledPack32 or
        Format.A2B10G10R10UintPack32 or Format.A2B10G10R10UnormPack32 or Format.A2B10G10R10UscaledPack32 or
        Format.A2R10G10B10SintPack32 or Format.A2R10G10B10SNormPack32 or Format.A2R10G10B10SscaledPack32 or
        Format.A2R10G10B10UintPack32 or Format.A2R10G10B10UnormPack32 or Format.A2R10G10B10UscaledPack32 or
        Format.A8B8G8R8SintPack32 or Format.A8B8G8R8SNormPack32 or Format.A8B8G8R8SrgbPack32 or
        Format.A8B8G8R8SscaledPack32 or Format.A8B8G8R8UintPack32 or Format.A8B8G8R8UnormPack32 or
        Format.A8B8G8R8UscaledPack32 or Format.B10G11R11UfloatPack32 or Format.B8G8R8A8Sint or
        Format.B8G8R8A8SNorm or Format.B8G8R8A8Srgb or Format.B8G8R8A8Sscaled or Format.B8G8R8A8Uint or
        Format.B8G8R8A8Unorm or Format.B8G8R8A8Uscaled or Format.E5B9G9R9UfloatPack32 or
        Format.R10X6G10X6Unorm2Pack16 or Format.R12X4G12X4Unorm2Pack16 or Format.R16G16Sfloat or
        Format.R16G16Sint or Format.R16G16SNorm or Format.R16G16Sscaled or Format.R16G16Uint or
        Format.R16G16Unorm or Format.R16G16Uscaled or Format.R32Sfloat or Format.R32Sint or Format.R32Uint or
        Format.R8G8B8A8Sint or Format.R8G8B8A8SNorm or Format.R8G8B8A8Srgb or Format.R8G8B8A8Sscaled or
        Format.R8G8B8A8Uint or Format.R8G8B8A8Unorm or Format.R8G8B8A8Uscaled => Bit32,

        Format.R16G16B16Sfloat or Format.R16G16B16Sint or Format.R16G16B16SNorm or Format.R16G16B16Sscaled or
        Format.R16G16B16Uint or Format.R16G16B16Unorm or Format.R16G16B16Uscaled => Bit48,

        Format.R16G16B16A16Sfloat or Format.R16G16B16A16Sint or Format.R16G16B16A16SNorm or
        Format.R16G16B16A16Sscaled or Format.R16G16B16A16Uint or Format.R16G16B16A16Unorm or
        Format.R16G16B16A16Uscaled or Format.R32G32Sfloat or Format.R32G32Sint or Format.R32G32Uint or
        Format.R64Sfloat or Format.R64Sint or Format.R64Uint => Bit64,

        Format.R32G32B32Sfloat or Format.R32G32B32Sint or Format.R32G32B32Uint => Bit96,

        Format.R32G32B32A32Sfloat or Format.R32G32B32A32Sint or Format.R32G32B32A32Uint or
        Format.R64G64Sfloat or Format.R64G64Sint or Format.R64G64Uint => Bit128,

        Format.R64G64B64Sfloat or Format.R64G64B64Sint or Format.R64G64B64Uint => Bit192,

        Format.R64G64B64A64Sfloat or Format.R64G64B64A64Sint or Format.R64G64B64A64Uint => Bit256,

        Format.BC1RgbSrgbBlock or Format.BC1RgbUnormBlock => Bc1Rgb | Bit64,
        Format.BC1RgbaSrgbBlock or Format.BC1RgbaUnormBlock => Bc1Rgba | Bit64,
        Format.BC2SrgbBlock or Format.BC2UnormBlock => Bc2 | Bit128,
        Format.BC3SrgbBlock or Format.BC3UnormBlock => Bc3 | Bit128,
        Format.BC4SNormBlock or Format.BC4UnormBlock => Bc4 | Bit64,
        Format.BC5SNormBlock or Format.BC5UnormBlock => Bc5 | Bit128,
        Format.BC6HSfloatBlock or Format.BC6HUfloatBlock => Bc6h | Bit128,
        Format.BC7SrgbBlock or Format.BC7UnormBlock => Bc7 | Bit128,

        Format.D16Unorm => D16,
        Format.D16UnormS8Uint => D16S8,
        Format.X8D24UnormPack32 => D24,
        Format.D24UnormS8Uint => D24S8,
        Format.D32Sfloat => D32,
        Format.D32SfloatS8Uint => D32S8,
        Format.S8Uint => S8,
        _ => None,
    };

    // The bytes of one texel block of a host format; 0 for a format outside the compatibility classes.
    public static uint BlockBytes(Format format)
    {
        var formatClass = FormatClass(format);
        if ((formatClass & Bc1Rgb) != 0 || (formatClass & Bc1Rgba) != 0 || (formatClass & Bc4) != 0)
        {
            return 8;
        }

        if ((formatClass & (Bc2 | Bc3 | Bc5 | Bc6h | Bc7)) != 0)
        {
            return 16;
        }

        return formatClass switch
        {
            Bit8 or S8 => 1,
            Bit16 or D16 => 2,
            Bit24 or D16S8 => 3,
            Bit32 or D24 or D24S8 or D32 => 4,
            D32S8 => 5,
            Bit48 => 6,
            Bit64 => 8,
            Bit96 => 12,
            Bit128 => 16,
            Bit192 => 24,
            Bit256 => 32,
            _ => 0,
        };
    }

    // A view format is compatible when its class is a subset of the base class.
    public static bool AreCompatible(Format baseFormat, Format viewFormat)
    {
        if (baseFormat == viewFormat)
        {
            return true;
        }

        var baseClass = FormatClass(baseFormat);
        var viewClass = FormatClass(viewFormat);
        return viewClass != None && (baseClass & viewClass) == viewClass;
    }

    // The image must permit block views before a view can use an uncompressed format.
    internal static bool AreImageViewFormatsCompatible(Format baseFormat, Format viewFormat, ImageCreateFlags flags)
    {
        const uint compressedClasses = Bc1Rgb | Bc1Rgba | Bc2 | Bc3 | Bc4 | Bc5 | Bc6h | Bc7;
        var reinterpretsBlocks = (FormatClass(baseFormat) & compressedClasses) != 0 &&
                                (FormatClass(viewFormat) & compressedClasses) == 0;
        return (!reinterpretsBlocks || (flags & ImageCreateFlags.CreateBlockTexelViewCompatibleBit) != 0) &&
               AreCompatible(baseFormat, viewFormat);
    }

    public static ImageAspectFlags DepthAspects(Format format) => format switch
    {
        Format.D16Unorm or Format.D32Sfloat => ImageAspectFlags.DepthBit,
        Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => throw SubmissionScheduler.Fatal($"The format is not a depth/stencil format: format={(int)format}."),
    };

    public static ImageAspectFlags FullAspects(Format format) => format switch
    {
        Format.D16Unorm or Format.X8D24UnormPack32 or Format.D32Sfloat => ImageAspectFlags.DepthBit,
        Format.S8Uint => ImageAspectFlags.StencilBit,
        Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => ImageAspectFlags.ColorBit,
    };

    public static bool IsDepthCompatible(Format format) =>
        format is Format.D32Sfloat or Format.R32Sfloat or Format.R32Uint or Format.D16Unorm or Format.R16Unorm;

    public static bool IsStencilViewFormat(Format format) => format is Format.S8Uint or Format.R8Uint or Format.R8Unorm;

    public static bool IsComponentSwizzle(ComponentSwizzle swizzle) => swizzle <= ComponentSwizzle.A;

    public static bool IsSupportedSampledDepthFormat(Format imageFormat, Format viewFormat) =>
        DepthFormatRule.AspectTransferFormat(imageFormat) != Format.Undefined && IsDepthCompatible(viewFormat);

    public static uint DestinationSelect(uint swizzle, uint channel) => (swizzle >> (int)(channel * 3)) & 0x7;

    public static uint PackDestinationSelect(uint x, uint y, uint z, uint w) => x | (y << 3) | (z << 6) | (w << 9);

    public static bool IsValidSwizzle(uint swizzle)
    {
        if ((swizzle & ~0xfffu) != 0)
        {
            return false;
        }

        for (uint channel = 0; channel < 4; channel++)
        {
            if (DestinationSelect(swizzle, channel) is not (0 or 1 or 4 or 5 or 6 or 7))
            {
                return false;
            }
        }

        return true;
    }

    private static ComponentSwizzle ToComponentSwizzle(uint selector) => (ComponentSelector)selector switch
    {
        ComponentSelector.Zero => ComponentSwizzle.Zero,
        ComponentSelector.One => ComponentSwizzle.One,
        ComponentSelector.Red => ComponentSwizzle.R,
        ComponentSelector.Green => ComponentSwizzle.G,
        ComponentSelector.Blue => ComponentSwizzle.B,
        ComponentSelector.Alpha => ComponentSwizzle.A,
        _ => throw SubmissionScheduler.Fatal($"The component selector is unknown: selector={selector}."),
    };

    public static ComponentMapping ComponentMapping(uint swizzle) => new(
        ToComponentSwizzle(DestinationSelect(swizzle, 0)),
        ToComponentSwizzle(DestinationSelect(swizzle, 1)),
        ToComponentSwizzle(DestinationSelect(swizzle, 2)),
        ToComponentSwizzle(DestinationSelect(swizzle, 3)));

    public static Format SrgbStorageFormat(Format imageFormat) => imageFormat switch
    {
        Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb => Format.R8G8B8A8Unorm,
        _ => Format.Undefined,
    };

    public static bool IsSupportedSampledColorView(Format imageFormat, Format viewFormat, uint swizzle) =>
        IsValidSwizzle(swizzle) && AreCompatible(imageFormat, viewFormat);

    public static uint SelectSampledColorView(Format imageFormat, Format viewFormat, uint swizzle) =>
        IsSupportedSampledColorView(imageFormat, viewFormat, swizzle)
            ? swizzle
            : throw UnsupportedColorView("sampled", imageFormat, viewFormat, swizzle);

    public static bool IsSupportedSampledDepthView(Format imageFormat, Format viewFormat, uint swizzle) =>
        IsSupportedSampledDepthFormat(imageFormat, viewFormat) &&
        (swizzle == PackDestinationSelect(4, 4, 4, 4) || swizzle == PackDestinationSelect(4, 0, 0, 0) || swizzle == PackDestinationSelect(4, 0, 0, 1));

    public static uint SelectSampledDepthView(Format imageFormat, Format viewFormat, uint swizzle) =>
        IsSupportedSampledDepthView(imageFormat, viewFormat, swizzle)
            ? swizzle
            : throw SubmissionScheduler.Fatal($"The sampled depth view is not supported: imageFormat={(int)imageFormat} viewFormat={(int)viewFormat} swizzle=0x{swizzle:x3}.");

    public static void ValidateStorageColorView(Format imageFormat, Format viewFormat, uint swizzle)
    {
        if (!AreCompatible(imageFormat, viewFormat) || !IsValidSwizzle(swizzle))
        {
            throw UnsupportedColorView("storage", imageFormat, viewFormat, swizzle);
        }
    }

    private static Exception UnsupportedColorView(string usage, Format imageFormat, Format viewFormat, uint swizzle) =>
        SubmissionScheduler.Fatal($"The {usage} color view is not supported: imageFormat={(int)imageFormat} viewFormat={(int)viewFormat} swizzle=0x{swizzle:x3}.");
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// The raw guest buffer/texture format value; names list the channel widths in bits.
public enum GuestPixelFormat : uint
{
    Invalid = 0,
    Bits8UNorm = 1,
    Bits8SNorm = 2,
    Bits8UScaled = 3,
    Bits8SScaled = 4,
    Bits8UInt = 5,
    Bits8SInt = 6,
    Bits16UNorm = 7,
    Bits16SNorm = 8,
    Bits16UScaled = 9,
    Bits16SScaled = 10,
    Bits16UInt = 11,
    Bits16SInt = 12,
    Bits16Float = 13,
    Bits8_8UNorm = 14,
    Bits8_8SNorm = 15,
    Bits8_8UScaled = 16,
    Bits8_8SScaled = 17,
    Bits8_8UInt = 18,
    Bits8_8SInt = 19,
    Bits32UInt = 20,
    Bits32SInt = 21,
    Bits32Float = 22,
    Bits16_16UNorm = 23,
    Bits16_16SNorm = 24,
    Bits16_16UScaled = 25,
    Bits16_16SScaled = 26,
    Bits16_16UInt = 27,
    Bits16_16SInt = 28,
    Bits16_16Float = 29,
    Bits11_11_10UNorm = 30,
    Bits11_11_10SNorm = 31,
    Bits11_11_10UScaled = 32,
    Bits11_11_10SScaled = 33,
    Bits11_11_10UInt = 34,
    Bits11_11_10SInt = 35,
    Bits11_11_10Float = 36,
    Bits10_11_11UNorm = 37,
    Bits10_11_11SNorm = 38,
    Bits10_11_11UScaled = 39,
    Bits10_11_11SScaled = 40,
    Bits10_11_11UInt = 41,
    Bits10_11_11SInt = 42,
    Bits10_11_11Float = 43,
    Bits2_10_10_10UNorm = 44,
    Bits2_10_10_10SNorm = 45,
    Bits2_10_10_10UScaled = 46,
    Bits2_10_10_10SScaled = 47,
    Bits2_10_10_10UInt = 48,
    Bits2_10_10_10SInt = 49,
    Bits10_10_10_2UNorm = 50,
    Bits10_10_10_2SNorm = 51,
    Bits10_10_10_2UScaled = 52,
    Bits10_10_10_2SScaled = 53,
    Bits10_10_10_2UInt = 54,
    Bits10_10_10_2SInt = 55,
    Bits8_8_8_8UNorm = 56,
    Bits8_8_8_8SNorm = 57,
    Bits8_8_8_8UScaled = 58,
    Bits8_8_8_8SScaled = 59,
    Bits8_8_8_8UInt = 60,
    Bits8_8_8_8SInt = 61,
    Bits32_32UInt = 62,
    Bits32_32SInt = 63,
    Bits32_32Float = 64,
    Bits16_16_16_16UNorm = 65,
    Bits16_16_16_16SNorm = 66,
    Bits16_16_16_16UScaled = 67,
    Bits16_16_16_16SScaled = 68,
    Bits16_16_16_16UInt = 69,
    Bits16_16_16_16SInt = 70,
    Bits16_16_16_16Float = 71,
    Bits32_32_32UInt = 72,
    Bits32_32_32SInt = 73,
    Bits32_32_32Float = 74,
    Bits32_32_32_32UInt = 75,
    Bits32_32_32_32SInt = 76,
    Bits32_32_32_32Float = 77,
    Bits8Srgb = 128,
    Bits8_8Srgb = 129,
    Bits8_8_8_8Srgb = 130,
    Bits10_10_10_2Float = 131,
    Bits9_9_9_5Float = 132,
    Bits5_6_5UNorm = 133,
    Bits5_5_5_1UNorm = 134,
    Bits1_5_5_5UNorm = 135,
    Bits4_4_4_4UNorm = 136,
    Fmask8S2F1 = 156,
    Fmask8S4F1 = 157,
    Fmask8S8F1 = 158,
    Fmask8S2F2 = 159,
    Fmask8S4F2 = 160,
    Fmask8S4F4 = 161,
    Fmask16S16F1 = 162,
    Fmask16S8F2 = 163,
    Fmask32S16F2 = 164,
    Fmask32S8F4 = 165,
    Fmask32S8F8 = 166,
    Fmask64S16F4 = 167,
    Fmask64S16F8 = 168,
    Bc1UNorm = 169,
    Bc1Srgb = 170,
    Bc2UNorm = 171,
    Bc2Srgb = 172,
    Bc3UNorm = 173,
    Bc3Srgb = 174,
    Bc4UNorm = 175,
    Bc4SNorm = 176,
    Bc5UNorm = 177,
    Bc5SNorm = 178,
    Bc6UFloat = 179,
    Bc6SFloat = 180,
    Bc7UNorm = 181,
    Bc7Srgb = 182,
}

public enum GuestTileMode : uint
{
    Linear = 0x00,
    Standard256B = 0x01,
    Standard4KB = 0x05,
    Standard64KB = 0x09,
    Prt = 0x11,
    Depth = 0x18,
    RenderTarget = 0x1b,
}

public enum GuestImageType : uint
{
    Color1D = 8,
    Color2D = 9,
    Color3D = 10,
    Cube = 11,
    Color1DArray = 12,
    Color2DArray = 13,
    Color2DMsaa = 14,
    Color2DMsaaArray = 15,
}

public enum GuestDepthFormat : uint
{
    Invalid = 0,
    Z16 = 1,
    Z32Float = 3,
}

public enum GuestStencilFormat : uint
{
    Invalid = 0,
    Stencil8UInt = 1,
}

public enum ChannelLayout : uint
{
    Invalid = 0,
    Bits8 = 1,
    Bits16 = 2,
    Bits8_8 = 3,
    Bits32 = 4,
    Bits16_16 = 5,
    Bits11_11_10 = 6,
    Bits10_11_11 = 7,
    Bits2_10_10_10 = 8,
    Bits10_10_10_2 = 9,
    Bits8_8_8_8 = 10,
    Bits32_32 = 11,
    Bits16_16_16_16 = 12,
    Bits32_32_32 = 13,
    Bits32_32_32_32 = 14,
    Bits5_6_5 = 16,
    Bits5_5_5_1 = 17,
    Bits1_5_5_5 = 18,
    Bits4_4_4_4 = 19,
    Bits10_10_10_2Float = 31,
    Bits9_9_9_5 = 34,
    Bc1 = 35,
    Bc2 = 36,
    Bc3 = 37,
    Bc4 = 38,
    Bc5 = 39,
    Bc6 = 40,
    Bc7 = 41,
}

public enum ChannelType : uint
{
    UNorm = 0,
    SNorm = 1,
    UInt = 4,
    SInt = 5,
    Srgb = 6,
    Float = 7,
}

public enum ChannelOrder : uint
{
    Standard = 0,
    Alternate = 1,
    Reversed = 2,
    AlternateReversed = 3,
}

public enum ComponentSelector : uint
{
    Zero = 0,
    One = 1,
    Red = 4,
    Green = 5,
    Blue = 6,
    Alpha = 7,
}

public enum ChannelOrderSupport : byte
{
    None,
    StandardOnly,
    All,
}

public enum TextureNumericClass
{
    Unsupported,
    Float,
    Uint,
    Sint,
}

// Selects the logical shader component written to each physical color-attachment component.
public readonly record struct ColorComponentMap(byte Packed)
{
    public const byte IdentityPacked = 0xE4;

    public static readonly ColorComponentMap Identity = new(IdentityPacked);
    public static readonly ColorComponentMap Gr = new(0xE1);
    public static readonly ColorComponentMap Rabg = new(0x6C);
    public static readonly ColorComponentMap Rgab = new(0xB4);
    public static readonly ColorComponentMap Bgra = new(0xC6);
    public static readonly ColorComponentMap Abgr = new(0x1B);
    public static readonly ColorComponentMap Agba = new(0x27);
    public static readonly ColorComponentMap Arbg = new(0x63);
    public static readonly ColorComponentMap Agbr = new(0x87);
    public static readonly ColorComponentMap Argb = new(0x93);

    public bool IsIdentity => Packed == IdentityPacked;

    public uint Map(uint component) => component < 4 ? (uint)(Packed >> (int)(component * 2)) & 0x3 : component;

    public uint ApplyMask(uint logicalMask)
    {
        uint mapped = 0;
        for (uint physical = 0; physical < 4; physical++)
        {
            mapped |= ((logicalMask >> (int)Map(physical)) & 1) << (int)physical;
        }

        return mapped;
    }

    // This map selects an intermediate component; the next map selects the final one.
    public ColorComponentMap Then(ColorComponentMap next)
    {
        byte packed = 0;
        for (uint physical = 0; physical < 4; physical++)
        {
            packed |= (byte)(next.Map(Map(physical)) << (int)(physical * 2));
        }

        return new ColorComponentMap(packed);
    }
}

public readonly record struct RenderTargetEncoding(GuestPixelFormat Format, byte Components, ChannelOrderSupport OrderSupport)
{
    public bool IsValid => Format != GuestPixelFormat.Invalid && Components is >= 1 and <= 4 && OrderSupport != ChannelOrderSupport.None;

    public bool SupportsOrder(ChannelOrder order) => OrderSupport switch
    {
        ChannelOrderSupport.StandardOnly => order == ChannelOrder.Standard,
        ChannelOrderSupport.All => order <= ChannelOrder.AlternateReversed,
        _ => false,
    };
}

// Element sizes, texture classes, render-target encodings and the host format of each guest format.
public static class GuestPixelFormats
{
    private readonly record struct FormatFacts(
        GuestPixelFormat Format,
        uint BytesPerElement,
        uint BlockCompressedBytes,
        uint RenderTargetBytesPerElement,
        bool SampledTexture,
        bool UintTexture,
        bool SintTexture = false);

    private static readonly FormatFacts[] Facts =
    [
        new(GuestPixelFormat.Bits8UNorm, 1, 0, 1, true, false),
        new(GuestPixelFormat.Bits8SNorm, 0, 0, 1, false, false),
        new(GuestPixelFormat.Bits8UInt, 1, 0, 1, true, true),
        new(GuestPixelFormat.Bits16UNorm, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits16SNorm, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits16UInt, 2, 0, 2, true, true),
        new(GuestPixelFormat.Bits16SInt, 2, 0, 2, true, false, true),
        new(GuestPixelFormat.Bits16Float, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits8_8UNorm, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits8_8SNorm, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits8_8UScaled, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits8_8UInt, 2, 0, 2, true, true),
        new(GuestPixelFormat.Bits8_8SInt, 2, 0, 2, true, false, true),
        new(GuestPixelFormat.Bits32UInt, 4, 0, 4, true, true),
        new(GuestPixelFormat.Bits32SInt, 4, 0, 4, true, false, true),
        new(GuestPixelFormat.Bits32Float, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits16_16UNorm, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits16_16SNorm, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits16_16UInt, 4, 0, 4, true, true),
        new(GuestPixelFormat.Bits16_16SInt, 4, 0, 4, true, false, true),
        new(GuestPixelFormat.Bits16_16Float, 4, 0, 4, true, false),
        // Vulkan has no normalized 11:11:10 format. Keep the guest packing sampled
        // through the native 32-bit packed-float view, matching the host backend.
        new(GuestPixelFormat.Bits11_11_10UNorm, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits11_11_10UInt, 4, 0, 4, true, true),
        new(GuestPixelFormat.Bits11_11_10Float, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits10_10_10_2UNorm, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits10_10_10_2UInt, 4, 0, 4, true, true),
        new(GuestPixelFormat.Bits8_8_8_8UNorm, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits8_8_8_8SNorm, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits8_8_8_8UInt, 4, 0, 4, true, true),
        new(GuestPixelFormat.Bits8_8_8_8SInt, 4, 0, 4, true, false, true),
        new(GuestPixelFormat.Bits32_32UInt, 8, 0, 8, true, true),
        new(GuestPixelFormat.Bits32_32SInt, 8, 0, 8, true, false, true),
        new(GuestPixelFormat.Bits32_32Float, 8, 0, 8, true, false),
        new(GuestPixelFormat.Bits16_16_16_16UNorm, 8, 0, 8, true, false),
        new(GuestPixelFormat.Bits16_16_16_16SNorm, 8, 0, 8, true, false),
        new(GuestPixelFormat.Bits16_16_16_16UInt, 8, 0, 8, true, true),
        new(GuestPixelFormat.Bits16_16_16_16SInt, 8, 0, 8, true, false, true),
        new(GuestPixelFormat.Bits16_16_16_16Float, 8, 0, 8, true, false),
        new(GuestPixelFormat.Bits32_32_32UInt, 12, 0, 12, true, true),
        new(GuestPixelFormat.Bits32_32_32SInt, 12, 0, 12, true, false, true),
        new(GuestPixelFormat.Bits32_32_32Float, 12, 0, 12, true, false),
        new(GuestPixelFormat.Bits32_32_32_32UInt, 16, 0, 16, true, true),
        new(GuestPixelFormat.Bits32_32_32_32SInt, 16, 0, 16, true, false, true),
        new(GuestPixelFormat.Bits32_32_32_32Float, 16, 0, 16, true, false),
        new(GuestPixelFormat.Bits8Srgb, 1, 0, 1, true, false),
        new(GuestPixelFormat.Bits8_8Srgb, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits8_8_8_8Srgb, 4, 0, 4, true, false),
        new(GuestPixelFormat.Bits9_9_9_5Float, 4, 0, 0, true, false),
        new(GuestPixelFormat.Bits5_6_5UNorm, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits5_5_5_1UNorm, 2, 0, 2, true, false),
        new(GuestPixelFormat.Bits1_5_5_5UNorm, 0, 0, 2, false, false),
        new(GuestPixelFormat.Bits4_4_4_4UNorm, 2, 0, 2, true, false),
        new(GuestPixelFormat.Fmask8S2F1, 1, 0, 1, true, false),
        new(GuestPixelFormat.Fmask8S4F1, 1, 0, 1, true, false),
        new(GuestPixelFormat.Fmask8S8F1, 1, 0, 1, true, false),
        new(GuestPixelFormat.Fmask8S2F2, 1, 0, 1, true, false),
        new(GuestPixelFormat.Fmask8S4F2, 1, 0, 1, true, false),
        new(GuestPixelFormat.Fmask8S4F4, 1, 0, 1, true, false),
        new(GuestPixelFormat.Fmask16S16F1, 2, 0, 2, true, false),
        new(GuestPixelFormat.Fmask16S8F2, 2, 0, 2, true, false),
        new(GuestPixelFormat.Fmask32S16F2, 4, 0, 4, true, false),
        new(GuestPixelFormat.Fmask32S8F4, 4, 0, 4, true, false),
        new(GuestPixelFormat.Fmask32S8F8, 4, 0, 4, true, false),
        new(GuestPixelFormat.Fmask64S16F4, 8, 0, 8, true, false),
        new(GuestPixelFormat.Fmask64S16F8, 8, 0, 8, true, false),
        new(GuestPixelFormat.Bc1UNorm, 0, 8, 0, true, false),
        new(GuestPixelFormat.Bc1Srgb, 0, 8, 0, true, false),
        new(GuestPixelFormat.Bc2UNorm, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc2Srgb, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc3UNorm, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc3Srgb, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc4UNorm, 0, 8, 0, true, false),
        new(GuestPixelFormat.Bc4SNorm, 0, 8, 0, true, false),
        new(GuestPixelFormat.Bc5UNorm, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc5SNorm, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc6UFloat, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc6SFloat, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc7UNorm, 0, 16, 0, true, false),
        new(GuestPixelFormat.Bc7Srgb, 0, 16, 0, true, false),
    ];

    private readonly record struct RenderTargetLayoutFacts(
        ChannelLayout Layout,
        GuestPixelFormat FirstLinearFormat,
        byte LinearTypeMask,
        byte Components,
        GuestPixelFormat ExtraFormat = GuestPixelFormat.Invalid,
        ChannelType ExtraType = ChannelType.UNorm,
        ChannelOrderSupport OrderSupport = ChannelOrderSupport.All);

    private static byte TypeBit(ChannelType type) => (byte)(1u << (int)type);

    private static readonly byte UNormType = TypeBit(ChannelType.UNorm);
    private static readonly byte IntegerTypes = (byte)(TypeBit(ChannelType.UInt) | TypeBit(ChannelType.SInt));
    private static readonly byte NormalizedIntegerTypes = (byte)(UNormType | TypeBit(ChannelType.SNorm) | IntegerTypes);

    private static readonly RenderTargetLayoutFacts[] RenderTargetLayouts =
    [
        new(ChannelLayout.Bits8, GuestPixelFormat.Bits8UNorm, NormalizedIntegerTypes, 1, GuestPixelFormat.Bits8Srgb, ChannelType.Srgb),
        new(ChannelLayout.Bits16, GuestPixelFormat.Bits16UNorm, NormalizedIntegerTypes, 1, GuestPixelFormat.Bits16Float, ChannelType.Float),
        new(ChannelLayout.Bits8_8, GuestPixelFormat.Bits8_8UNorm, NormalizedIntegerTypes, 2, GuestPixelFormat.Bits8_8Srgb, ChannelType.Srgb),
        new(ChannelLayout.Bits32, GuestPixelFormat.Bits32UInt, IntegerTypes, 1, GuestPixelFormat.Bits32Float, ChannelType.Float),
        new(ChannelLayout.Bits16_16, GuestPixelFormat.Bits16_16UNorm, NormalizedIntegerTypes, 2, GuestPixelFormat.Bits16_16Float, ChannelType.Float),
        new(ChannelLayout.Bits11_11_10, GuestPixelFormat.Bits11_11_10UNorm, NormalizedIntegerTypes, 3, GuestPixelFormat.Bits11_11_10Float, ChannelType.Float),
        new(ChannelLayout.Bits10_11_11, GuestPixelFormat.Bits10_11_11UNorm, NormalizedIntegerTypes, 3, GuestPixelFormat.Bits10_11_11Float, ChannelType.Float),
        new(ChannelLayout.Bits2_10_10_10, GuestPixelFormat.Bits2_10_10_10UNorm, NormalizedIntegerTypes, 4),
        new(ChannelLayout.Bits10_10_10_2, GuestPixelFormat.Bits10_10_10_2UNorm, NormalizedIntegerTypes, 4),
        new(ChannelLayout.Bits8_8_8_8, GuestPixelFormat.Bits8_8_8_8UNorm, NormalizedIntegerTypes, 4, GuestPixelFormat.Bits8_8_8_8Srgb, ChannelType.Srgb),
        new(ChannelLayout.Bits32_32, GuestPixelFormat.Bits32_32UInt, IntegerTypes, 2, GuestPixelFormat.Bits32_32Float, ChannelType.Float),
        new(ChannelLayout.Bits16_16_16_16, GuestPixelFormat.Bits16_16_16_16UNorm, NormalizedIntegerTypes, 4, GuestPixelFormat.Bits16_16_16_16Float, ChannelType.Float),
        new(ChannelLayout.Bits32_32_32_32, GuestPixelFormat.Bits32_32_32_32UInt, IntegerTypes, 4, GuestPixelFormat.Bits32_32_32_32Float, ChannelType.Float),
        new(ChannelLayout.Bits5_6_5, GuestPixelFormat.Bits5_6_5UNorm, UNormType, 3),
        new(ChannelLayout.Bits5_5_5_1, GuestPixelFormat.Bits5_5_5_1UNorm, UNormType, 4),
        new(ChannelLayout.Bits1_5_5_5, GuestPixelFormat.Bits1_5_5_5UNorm, UNormType, 4),
        new(ChannelLayout.Bits4_4_4_4, GuestPixelFormat.Bits4_4_4_4UNorm, UNormType, 4),
        new(ChannelLayout.Bits10_10_10_2Float, GuestPixelFormat.Invalid, 0, 4, GuestPixelFormat.Bits10_10_10_2Float, ChannelType.Float, ChannelOrderSupport.StandardOnly),
    ];

    private static readonly (GuestPixelFormat Guest, Format Host)[] HostFormats =
    [
        (GuestPixelFormat.Bits8UNorm, Format.R8Unorm),
        (GuestPixelFormat.Bits8UInt, Format.R8Uint),
        (GuestPixelFormat.Bits16UNorm, Format.R16Unorm),
        (GuestPixelFormat.Bits16SNorm, Format.R16SNorm),
        (GuestPixelFormat.Bits16UInt, Format.R16Uint),
        (GuestPixelFormat.Bits16SInt, Format.R16Sint),
        (GuestPixelFormat.Bits16Float, Format.R16Sfloat),
        (GuestPixelFormat.Bits8_8UNorm, Format.R8G8Unorm),
        (GuestPixelFormat.Bits8_8SNorm, Format.R8G8SNorm),
        (GuestPixelFormat.Bits8_8UScaled, Format.R8G8Uscaled),
        (GuestPixelFormat.Bits8_8UInt, Format.R8G8Uint),
        (GuestPixelFormat.Bits8_8SInt, Format.R8G8Sint),
        (GuestPixelFormat.Bits32UInt, Format.R32Uint),
        (GuestPixelFormat.Bits32SInt, Format.R32Sint),
        (GuestPixelFormat.Bits32Float, Format.R32Sfloat),
        (GuestPixelFormat.Bits16_16UNorm, Format.R16G16Unorm),
        (GuestPixelFormat.Bits16_16SNorm, Format.R16G16SNorm),
        (GuestPixelFormat.Bits16_16UInt, Format.R16G16Uint),
        (GuestPixelFormat.Bits16_16SInt, Format.R16G16Sint),
        (GuestPixelFormat.Bits16_16Float, Format.R16G16Sfloat),
        (GuestPixelFormat.Bits11_11_10UNorm, Format.B10G11R11UfloatPack32),
        (GuestPixelFormat.Bits11_11_10Float, Format.B10G11R11UfloatPack32),
        (GuestPixelFormat.Bits10_10_10_2UNorm, Format.A2B10G10R10UnormPack32),
        (GuestPixelFormat.Bits10_10_10_2UInt, Format.A2B10G10R10UintPack32),
        (GuestPixelFormat.Bits8_8_8_8UNorm, Format.R8G8B8A8Unorm),
        (GuestPixelFormat.Bits8_8_8_8SNorm, Format.R8G8B8A8SNorm),
        (GuestPixelFormat.Bits8_8_8_8UInt, Format.R8G8B8A8Uint),
        (GuestPixelFormat.Bits8_8_8_8SInt, Format.R8G8B8A8Sint),
        (GuestPixelFormat.Bits32_32UInt, Format.R32G32Uint),
        (GuestPixelFormat.Bits32_32SInt, Format.R32G32Sint),
        (GuestPixelFormat.Bits32_32Float, Format.R32G32Sfloat),
        (GuestPixelFormat.Bits16_16_16_16UNorm, Format.R16G16B16A16Unorm),
        (GuestPixelFormat.Bits16_16_16_16SNorm, Format.R16G16B16A16SNorm),
        (GuestPixelFormat.Bits16_16_16_16UInt, Format.R16G16B16A16Uint),
        (GuestPixelFormat.Bits16_16_16_16SInt, Format.R16G16B16A16Sint),
        (GuestPixelFormat.Bits16_16_16_16Float, Format.R16G16B16A16Sfloat),
        (GuestPixelFormat.Bits32_32_32UInt, Format.R32G32B32Uint),
        (GuestPixelFormat.Bits32_32_32SInt, Format.R32G32B32Sint),
        (GuestPixelFormat.Bits32_32_32Float, Format.R32G32B32Sfloat),
        (GuestPixelFormat.Bits32_32_32_32UInt, Format.R32G32B32A32Uint),
        (GuestPixelFormat.Bits32_32_32_32SInt, Format.R32G32B32A32Sint),
        (GuestPixelFormat.Bits32_32_32_32Float, Format.R32G32B32A32Sfloat),
        // Narrow sRGB formats are optional in Vulkan; a same-width UNORM format stands in.
        (GuestPixelFormat.Bits8Srgb, Format.R8Unorm),
        (GuestPixelFormat.Bits8_8Srgb, Format.R8G8Unorm),
        (GuestPixelFormat.Bits8_8_8_8Srgb, Format.R8G8B8A8Srgb),
        (GuestPixelFormat.Bits9_9_9_5Float, Format.E5B9G9R9UfloatPack32),
        (GuestPixelFormat.Bits5_6_5UNorm, Format.B5G6R5UnormPack16),
        (GuestPixelFormat.Bits5_5_5_1UNorm, Format.R5G5B5A1UnormPack16),
        (GuestPixelFormat.Bits4_4_4_4UNorm, Format.R4G4B4A4UnormPack16),
        (GuestPixelFormat.Fmask8S4F4, Format.R32Sfloat),
        (GuestPixelFormat.Bc1UNorm, Format.BC1RgbaUnormBlock),
        (GuestPixelFormat.Bc1Srgb, Format.BC1RgbaSrgbBlock),
        (GuestPixelFormat.Bc2UNorm, Format.BC2UnormBlock),
        (GuestPixelFormat.Bc2Srgb, Format.BC2SrgbBlock),
        (GuestPixelFormat.Bc3UNorm, Format.BC3UnormBlock),
        (GuestPixelFormat.Bc3Srgb, Format.BC3SrgbBlock),
        (GuestPixelFormat.Bc4UNorm, Format.BC4UnormBlock),
        (GuestPixelFormat.Bc4SNorm, Format.BC4SNormBlock),
        (GuestPixelFormat.Bc5UNorm, Format.BC5UnormBlock),
        (GuestPixelFormat.Bc5SNorm, Format.BC5SNormBlock),
        (GuestPixelFormat.Bc6UFloat, Format.BC6HUfloatBlock),
        (GuestPixelFormat.Bc6SFloat, Format.BC6HSfloatBlock),
        (GuestPixelFormat.Bc7UNorm, Format.BC7UnormBlock),
        (GuestPixelFormat.Bc7Srgb, Format.BC7SrgbBlock),
    ];

    private const int LookupSize = (int)GuestPixelFormat.Bc7Srgb + 1;

    private static readonly FormatFacts?[] FactsLookup = BuildFactsLookup();
    private static readonly Format[] HostLookup = BuildHostLookup();

    private static FormatFacts?[] BuildFactsLookup()
    {
        var lookup = new FormatFacts?[LookupSize];
        foreach (var facts in Facts)
        {
            lookup[(int)facts.Format] = facts;
        }

        return lookup;
    }

    private static Format[] BuildHostLookup()
    {
        var lookup = new Format[LookupSize];
        foreach (var (guest, host) in HostFormats)
        {
            lookup[(int)guest] = host;
        }

        return lookup;
    }

    private static FormatFacts? Find(GuestPixelFormat format) =>
        (uint)format < LookupSize ? FactsLookup[(int)format] : null;

    public static uint BytesPerElement(GuestPixelFormat format) => Find(format)?.BytesPerElement ?? 0;

    public static uint BlockCompressedBytes(GuestPixelFormat format) => Find(format)?.BlockCompressedBytes ?? 0;

    public static uint RenderTargetBytesPerElement(GuestPixelFormat format) => Find(format)?.RenderTargetBytesPerElement ?? 0;

    public static bool IsFmaskFormat(GuestPixelFormat format) =>
        format >= GuestPixelFormat.Fmask8S2F1 && format <= GuestPixelFormat.Fmask64S16F8;

    public static TextureNumericClass SampledNumericClass(GuestPixelFormat format)
    {
        if (Find(format) is not { SampledTexture: true } facts)
        {
            return TextureNumericClass.Unsupported;
        }

        if (facts.SintTexture)
        {
            return TextureNumericClass.Sint;
        }

        return facts.UintTexture ? TextureNumericClass.Uint : TextureNumericClass.Float;
    }

    public static GuestPixelFormat RemapTextureFormat(GuestPixelFormat format) =>
        format == GuestPixelFormat.Bits11_11_10UInt ? GuestPixelFormat.Bits32UInt : format;

    public static Format HostFormat(GuestPixelFormat format) =>
        (uint)format < LookupSize ? HostLookup[(int)format] : Format.Undefined;

    public static RenderTargetEncoding ResolveRenderTargetEncoding(ChannelLayout layout, ChannelType type)
    {
        var rawType = (uint)type;
        RenderTargetLayoutFacts? found = null;
        foreach (var facts in RenderTargetLayouts)
        {
            if (facts.Layout == layout)
            {
                found = facts;
                break;
            }
        }

        if (found is not { } info || rawType >= 8)
        {
            return default;
        }

        var format = GuestPixelFormat.Invalid;
        if (info.ExtraFormat != GuestPixelFormat.Invalid && type == info.ExtraType)
        {
            format = info.ExtraFormat;
        }
        else if ((info.LinearTypeMask & (1u << (int)rawType)) != 0)
        {
            var firstType = (uint)System.Numerics.BitOperations.TrailingZeroCount((uint)info.LinearTypeMask);
            format = (GuestPixelFormat)((uint)info.FirstLinearFormat + rawType - firstType);
        }

        return format == GuestPixelFormat.Invalid ? default : new RenderTargetEncoding(format, info.Components, info.OrderSupport);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// MTLPixelFormat raw values — only the formats the backend maps. Declared here
/// rather than pulled from a binding package: the Metal backend talks to the OS
/// exclusively through objc_msgSend, so ABI constants are owned locally.
/// </summary>
internal enum MtlPixelFormat : uint
{
    Invalid = 0,
    R8Unorm = 10,
    R8Snorm = 12,
    R8Uint = 13,
    R8Sint = 14,
    R16Unorm = 20,
    R16Snorm = 22,
    R16Uint = 23,
    R16Sint = 24,
    R16Float = 25,
    Rg8Unorm = 30,
    Rg8Snorm = 32,
    Rg8Uint = 33,
    Rg8Sint = 34,
    B5G6R5Unorm = 40,
    R32Uint = 53,
    R32Sint = 54,
    R32Float = 55,
    Rg16Unorm = 60,
    Rg16Uint = 63,
    Rg16Sint = 64,
    Rg16Float = 65,
    Rgba8Unorm = 70,
    Rgba8UnormSrgb = 71,
    Rgba8Uint = 73,
    Rgba8Sint = 74,
    Bgra8Unorm = 80,
    Bgra8UnormSrgb = 81,
    Rgb10A2Unorm = 90,
    Rg11B10Float = 92,
    Rgb9E5Float = 93,
    Bgr10A2Unorm = 94,
    Rg32Uint = 103,
    Rg32Sint = 104,
    Rg32Float = 105,
    Rgba16Unorm = 110,
    Rgba16Uint = 113,
    Rgba16Sint = 114,
    Rgba16Float = 115,
    Rgba32Uint = 123,
    Rgba32Sint = 124,
    Rgba32Float = 125,
    Bc1Rgba = 130,
    Bc1RgbaSrgb = 131,
    Bc2Rgba = 132,
    Bc2RgbaSrgb = 133,
    Bc3Rgba = 134,
    Bc3RgbaSrgb = 135,
    Bc4RUnorm = 140,
    Bc4RSnorm = 141,
    Bc5RgUnorm = 142,
    Bc5RgSnorm = 143,
    Bc6HRgbFloat = 150,
    Bc6HRgbUfloat = 151,
    Bc7RgbaUnorm = 152,
    Bc7RgbaUnormSrgb = 153,
    Depth32Float = 252,
}

/// <summary>A sampled-texture format: the Metal pixel format plus the byte
/// layout the upload path needs. <see cref="BlockBytes"/> is nonzero for
/// block-compressed formats (bytes per 4x4 block); otherwise
/// <see cref="BytesPerPixel"/> applies.</summary>
internal readonly record struct MetalTextureFormat(
    MtlPixelFormat Format,
    uint BytesPerPixel,
    uint BlockBytes)
{
    public bool IsBlockCompressed => BlockBytes != 0;
}

internal readonly record struct MetalRenderTargetFormat(
    MtlPixelFormat Format,
    Gen5PixelOutputKind OutputKind)
{
    public static uint GetBytesPerPixel(MtlPixelFormat format) =>
        format switch
        {
            MtlPixelFormat.R8Unorm or MtlPixelFormat.R8Uint => 1,
            MtlPixelFormat.Rg8Unorm => 2,
            MtlPixelFormat.Rg32Float => 8,
            MtlPixelFormat.Rgba16Unorm or MtlPixelFormat.Rgba16Uint or
                MtlPixelFormat.Rgba16Sint or MtlPixelFormat.Rgba16Float => 8,
            MtlPixelFormat.Rgba32Float => 16,
            _ => 4,
        };
}

/// <summary>
/// Guest texture-descriptor codes to Metal formats, mirroring the Vulkan
/// backend's table case for case so both backends accept the same guest
/// formats. Guest format 9 (2:10:10:10) maps to BGR10A2 — the bit layout that
/// matches Vulkan's A2R10G10B10 pack.
/// </summary>
internal static class MetalGuestFormats
{
    /// <summary>Guest sampled-texture format to Metal, mirroring the Vulkan
    /// backend's GetTextureFormat case for case (including its RGBA8 fallback
    /// for unmapped codes, so unknown formats render something rather than
    /// nothing). BC formats upload raw blocks — Mac-family GPUs decode them
    /// natively.</summary>
    public static MetalTextureFormat DecodeTextureFormat(uint dataFormat, uint numberType)
    {
        var format = (dataFormat, numberType) switch
        {
            (1, 0) => MtlPixelFormat.R8Unorm,
            (1, 1) => MtlPixelFormat.R8Snorm,
            (1, 4) => MtlPixelFormat.R8Uint,
            (1, 5) => MtlPixelFormat.R8Sint,
            (2, 0) => MtlPixelFormat.R16Unorm,
            (2, 1) => MtlPixelFormat.R16Snorm,
            (2, 4) => MtlPixelFormat.R16Uint,
            (2, 5) => MtlPixelFormat.R16Sint,
            (2, 7) => MtlPixelFormat.R16Float,
            (3, 0) => MtlPixelFormat.Rg8Unorm,
            (3, 1) => MtlPixelFormat.Rg8Snorm,
            (3, 4) => MtlPixelFormat.Rg8Uint,
            (3, 5) => MtlPixelFormat.Rg8Sint,
            (4, 4) => MtlPixelFormat.R32Uint,
            (4, 5) => MtlPixelFormat.R32Sint,
            (4, 7) => MtlPixelFormat.R32Float,
            (5, 0) => MtlPixelFormat.Rg16Unorm,
            (5, 4) => MtlPixelFormat.Rg16Uint,
            (5, 5) => MtlPixelFormat.Rg16Sint,
            (5, 7) => MtlPixelFormat.Rg16Float,
            (6, 7) or (7, 7) => MtlPixelFormat.Rg11B10Float,
            (8, _) or (9, _) => MtlPixelFormat.Bgr10A2Unorm,
            (10, 4) => MtlPixelFormat.Rgba8Uint,
            (10, 5) => MtlPixelFormat.Rgba8Sint,
            (10, 9) => MtlPixelFormat.Rgba8UnormSrgb,
            (11, 4) => MtlPixelFormat.Rg32Uint,
            (11, 5) => MtlPixelFormat.Rg32Sint,
            (11, 7) => MtlPixelFormat.Rg32Float,
            (12, 0) => MtlPixelFormat.Rgba16Unorm,
            (12, 4) => MtlPixelFormat.Rgba16Uint,
            (12, 5) => MtlPixelFormat.Rgba16Sint,
            (12, 7) => MtlPixelFormat.Rgba16Float,
            (13, 4) or (14, 4) => MtlPixelFormat.Rgba32Uint,
            (13, 5) or (14, 5) => MtlPixelFormat.Rgba32Sint,
            (13, _) or (14, _) => MtlPixelFormat.Rgba32Float,
            (16, 0) => MtlPixelFormat.B5G6R5Unorm,
            (34, 7) => MtlPixelFormat.Rgb9E5Float,
            (169, _) => MtlPixelFormat.Bc1Rgba,
            (170, _) => MtlPixelFormat.Bc1RgbaSrgb,
            (171, _) => MtlPixelFormat.Bc2Rgba,
            (172, _) => MtlPixelFormat.Bc2RgbaSrgb,
            (173, _) => MtlPixelFormat.Bc3Rgba,
            (174, _) => MtlPixelFormat.Bc3RgbaSrgb,
            (175, 1) or (176, _) => MtlPixelFormat.Bc4RSnorm,
            (175, _) => MtlPixelFormat.Bc4RUnorm,
            (177, 1) or (178, _) => MtlPixelFormat.Bc5RgSnorm,
            (177, _) => MtlPixelFormat.Bc5RgUnorm,
            (179, _) => MtlPixelFormat.Bc6HRgbUfloat,
            (180, _) => MtlPixelFormat.Bc6HRgbFloat,
            (181, _) => MtlPixelFormat.Bc7RgbaUnorm,
            (182, _) => MtlPixelFormat.Bc7RgbaUnormSrgb,
            _ => MtlPixelFormat.Rgba8Unorm,
        };

        var blockBytes = format switch
        {
            MtlPixelFormat.Bc1Rgba or MtlPixelFormat.Bc1RgbaSrgb or
                MtlPixelFormat.Bc4RUnorm or MtlPixelFormat.Bc4RSnorm => 8u,
            MtlPixelFormat.Bc2Rgba or MtlPixelFormat.Bc2RgbaSrgb or
                MtlPixelFormat.Bc3Rgba or MtlPixelFormat.Bc3RgbaSrgb or
                MtlPixelFormat.Bc5RgUnorm or MtlPixelFormat.Bc5RgSnorm or
                MtlPixelFormat.Bc6HRgbFloat or MtlPixelFormat.Bc6HRgbUfloat or
                MtlPixelFormat.Bc7RgbaUnorm or MtlPixelFormat.Bc7RgbaUnormSrgb => 16u,
            _ => 0u,
        };

        var bytesPerPixel = format switch
        {
            MtlPixelFormat.R8Unorm or MtlPixelFormat.R8Snorm or
                MtlPixelFormat.R8Uint or MtlPixelFormat.R8Sint => 1u,
            MtlPixelFormat.R16Unorm or MtlPixelFormat.R16Snorm or
                MtlPixelFormat.R16Uint or MtlPixelFormat.R16Sint or
                MtlPixelFormat.R16Float or MtlPixelFormat.Rg8Unorm or
                MtlPixelFormat.Rg8Snorm or MtlPixelFormat.Rg8Uint or
                MtlPixelFormat.Rg8Sint or MtlPixelFormat.B5G6R5Unorm => 2u,
            MtlPixelFormat.Rg32Uint or MtlPixelFormat.Rg32Sint or
                MtlPixelFormat.Rg32Float or MtlPixelFormat.Rgba16Unorm or
                MtlPixelFormat.Rgba16Uint or MtlPixelFormat.Rgba16Sint or
                MtlPixelFormat.Rgba16Float => 8u,
            MtlPixelFormat.Rgba32Uint or MtlPixelFormat.Rgba32Sint or
                MtlPixelFormat.Rgba32Float => 16u,
            _ => 4u,
        };

        return new MetalTextureFormat(format, bytesPerPixel, blockBytes);
    }

    /// <summary>Source byte footprint of a sampled texture, block-aware —
    /// the same math the AGC layer uses to size the texel copy it ships.</summary>
    public static ulong GetTextureByteCount(in MetalTextureFormat format, uint width, uint height) =>
        format.IsBlockCompressed
            ? checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * format.BlockBytes)
            : checked((ulong)width * height * format.BytesPerPixel);

    public static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        out MetalRenderTargetFormat result)
    {
        var format = (dataFormat, numberType) switch
        {
            (4, 4) => MtlPixelFormat.R32Uint,
            (4, 5) => MtlPixelFormat.R32Sint,
            (4, 7) => MtlPixelFormat.R32Float,
            (5, 4) => MtlPixelFormat.Rg16Uint,
            (5, 5) => MtlPixelFormat.Rg16Sint,
            (5, 7) => MtlPixelFormat.Rg16Float,
            (6, 7) or (7, 7) => MtlPixelFormat.Rg11B10Float,
            (9, _) => MtlPixelFormat.Bgr10A2Unorm,
            (10, 4) => MtlPixelFormat.Rgba8Uint,
            (10, 5) => MtlPixelFormat.Rgba8Sint,
            (10, 9) => MtlPixelFormat.Rgba8UnormSrgb,
            (10, _) => MtlPixelFormat.Rgba8Unorm,
            (11, 7) => MtlPixelFormat.Rg32Float,
            (12, 4) => MtlPixelFormat.Rgba16Uint,
            (12, 5) => MtlPixelFormat.Rgba16Sint,
            (12, 7) => MtlPixelFormat.Rgba16Float,
            (13, 7) or (14, 7) => MtlPixelFormat.Rgba32Float,
            (20, 0) => MtlPixelFormat.R32Uint,
            (29, 0) or (4, 0) => MtlPixelFormat.R32Float,
            (1, 0) or (36, 0) => MtlPixelFormat.R8Unorm,
            (49, 0) => MtlPixelFormat.R8Uint,
            (3, 0) => MtlPixelFormat.Rg8Unorm,
            (5, 0) => MtlPixelFormat.Rg16Unorm,
            (7, 0) => MtlPixelFormat.Rg11B10Float,
            (12, 0) => MtlPixelFormat.Rgba16Unorm,
            (13, 0) or (14, 0) => MtlPixelFormat.Rgba32Float,
            (22, 0) or (71, 0) => MtlPixelFormat.Rgba16Float,
            (56, 0) or (62, 0) or (64, 0) => MtlPixelFormat.Rgba8Unorm,
            (75, 0) => MtlPixelFormat.Rg32Float,
            _ => MtlPixelFormat.Invalid,
        };

        if (format == MtlPixelFormat.Invalid)
        {
            result = default;
            return false;
        }

        var outputKind = format switch
        {
            MtlPixelFormat.R8Uint or MtlPixelFormat.R32Uint or MtlPixelFormat.Rg16Uint or
                MtlPixelFormat.Rgba8Uint or MtlPixelFormat.Rgba16Uint => Gen5PixelOutputKind.Uint,
            MtlPixelFormat.R32Sint or MtlPixelFormat.Rg16Sint or MtlPixelFormat.Rgba8Sint or
                MtlPixelFormat.Rgba16Sint => Gen5PixelOutputKind.Sint,
            _ => Gen5PixelOutputKind.Float,
        };
        result = new MetalRenderTargetFormat(format, outputKind);
        return true;
    }
}

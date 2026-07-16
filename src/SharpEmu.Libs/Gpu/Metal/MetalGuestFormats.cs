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
    R8Uint = 13,
    Rg8Unorm = 30,
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
    Rg11B10Float = 92,
    Bgr10A2Unorm = 94,
    Rg32Float = 105,
    Rgba16Unorm = 110,
    Rgba16Uint = 113,
    Rgba16Sint = 114,
    Rgba16Float = 115,
    Rgba32Float = 125,
    Depth32Float = 252,
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

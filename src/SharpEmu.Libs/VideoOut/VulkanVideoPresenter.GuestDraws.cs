// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{

    private static long _perfDrawCount;
    private static long _perfDrawTicks;
    private static long _perfPipelineCreations;
    private static long _perfSpirvCompilations;

    internal static (long Draws, double DrawMs, long Pipelines, long SpirvCompilations)
        ReadAndResetPerfCounters()
    {
        var draws = Interlocked.Exchange(ref _perfDrawCount, 0);
        var ticks = Interlocked.Exchange(ref _perfDrawTicks, 0);
        var pipelines = Interlocked.Exchange(ref _perfPipelineCreations, 0);
        var spirv = Interlocked.Exchange(ref _perfSpirvCompilations, 0);
        return (draws, ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, pipelines, spirv);
    }

    internal static void CountSpirvCompilation() =>
        Interlocked.Increment(ref _perfSpirvCompilations);

    internal static ulong GetGuestImageByteCount(uint format, uint width, uint height)
    {
        var blockBytes = format switch
        {
            169 or 170 or 175 or 176 => 8UL,
            171 or 172 or 173 or 174 or
            177 or 178 or 179 or 180 or 181 or 182 => 16UL,
            _ => 0UL,
        };
        if (blockBytes != 0)
        {
            return checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
        }

        var bytesPerPixel = format switch
        {
            1 => 1UL,
            2 or 3 or 16 or 17 or 19 => 2UL,
            11 or 12 => 8UL,
            13 => 12UL,
            14 => 16UL,
            _ => 4UL,
        };
        return checked((ulong)width * height * bytesPerPixel);
    }

    internal static ulong GetGuestImageByteCount(
        uint format,
        uint width,
        uint height,
        uint depth) =>
        checked(GetGuestImageByteCount(format, width, height) * Math.Max(depth, 1u));

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        out VulkanRenderTargetFormat result) =>
        TryDecodeRenderTargetFormat(
            dataFormat,
            numberType,
            componentSwap: 0,
            out result);

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        uint componentSwap,
        out VulkanRenderTargetFormat result)
    {
        var format = (dataFormat, numberType, componentSwap) switch
        {
            // Early geometry and scene targets use these color formats.
            (2, 0, _) => Format.R16Unorm,
            (2, 1, _) => Format.R16SNorm,
            (2, 2, _) => Format.R16Uscaled,
            (2, 3, _) => Format.R16Sscaled,
            (2, 4, _) => Format.R16Uint,
            (2, 5, _) => Format.R16Sint,
            (2, 7, _) => Format.R16Sfloat,
            (4, 4, _) => Format.R32Uint,
            (4, 5, _) => Format.R32Sint,
            (4, 7, _) => Format.R32Sfloat,
            (5, 4, _) => Format.R16G16Uint,
            (5, 5, _) => Format.R16G16Sint,
            (5, 7, _) => Format.R16G16Sfloat,
            (6, 7, _) or (7, 7, _) => Format.B10G11R11UfloatPack32,
            (9, _, 1) => Format.A2R10G10B10UnormPack32,
            (9, _, _) => Format.A2B10G10R10UnormPack32,
            (10, 4, _) => Format.R8G8B8A8Uint,
            (10, 5, _) => Format.R8G8B8A8Sint,
            (10, 6 or 9, 1) => Format.B8G8R8A8Srgb,
            (10, 6 or 9, _) => Format.R8G8B8A8Srgb,
            (10, 0, 1) => Format.B8G8R8A8Unorm,
            (10, _, _) => Format.R8G8B8A8Unorm,
            (11, 4, _) => Format.R32G32Uint,
            (11, 5, _) => Format.R32G32Sint,
            (11, 7, _) => Format.R32G32Sfloat,
            (12, 4, _) => Format.R16G16B16A16Uint,
            (12, 5, _) => Format.R16G16B16A16Sint,
            (12, 7, _) => Format.R16G16B16A16Sfloat,
            (13, 7, _) or (14, 7, _) => Format.R32G32B32A32Sfloat,
            (20, 0, _) => Format.R32Uint,
            (29, 0, _) or (4, 0, _) => Format.R32Sfloat,
            (1, 0, _) or (36, 0, _) => Format.R8Unorm,
            (1, 4, _) or (49, 0, _) => Format.R8Uint,
            (3, 0, _) => Format.R8G8Unorm,
            (5, 0, _) => Format.R16G16Unorm,
            (7, 0, _) => Format.B10G11R11UfloatPack32,
            (12, 0, _) => Format.R16G16B16A16Unorm,
            (13, 0, _) or (14, 0, _) => Format.R32G32B32A32Sfloat,
            (22, 0, _) or (71, 0, _) => Format.R16G16B16A16Sfloat,
            (56, 0, _) or (62, 0, _) or (64, 0, _) => Format.R8G8B8A8Unorm,
            (75, 0, _) => Format.R32G32Sfloat,
            _ => Format.Undefined,
        };

        if (format == Format.Undefined ||
            !TryGetRenderTargetComponentCount(dataFormat, out var componentCount) ||
            !Gen5ColorComponentMapping.TryResolveRenderTarget(
                componentSwap,
                componentCount,
                out var orderMapping))
        {
            result = default;
            return false;
        }

        var outputKind = format switch
        {
            Format.R8Uint or Format.R16Uint or Format.R32Uint or Format.R16G16Uint or
                Format.R32G32Uint or Format.R8G8B8A8Uint or Format.R16G16B16A16Uint =>
                Gen5PixelOutputKind.Uint,
            Format.R16Sint or Format.R32Sint or Format.R16G16Sint or Format.R32G32Sint or
                Format.R8G8B8A8Sint or Format.R16G16B16A16Sint => Gen5PixelOutputKind.Sint,
            _ => Gen5PixelOutputKind.Float,
        };

        var hostToStorage = dataFormat switch
        {
            9 when componentSwap == 1 => new Gen5ColorComponentMapping(0xC6),
            10 when componentSwap == 1 && numberType is 0 or 6 or 9 =>
                new Gen5ColorComponentMapping(0xC6),
            _ => Gen5ColorComponentMapping.Identity,
        };
        result = new VulkanRenderTargetFormat(
            format,
            outputKind,
            hostToStorage.Then(orderMapping));
        return true;
    }

    private static bool TryGetRenderTargetComponentCount(
        uint dataFormat,
        out uint componentCount)
    {
        componentCount = dataFormat switch
        {
            1 or 2 or 4 or 20 or 29 or 36 or 49 => 1,
            3 or 5 or 11 or 75 => 2,
            6 or 7 => 3,
            9 or 10 or 12 or 13 or 14 or 22 or 56 or 62 or 64 or 71 => 4,
            _ => 0,
        };
        return componentCount != 0;
    }

}

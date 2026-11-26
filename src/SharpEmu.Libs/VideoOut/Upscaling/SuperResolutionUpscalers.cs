// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Numerics;
using Silk.NET.Vulkan;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.VideoOut.Upscaling.Fsr;
using SharpEmu.Libs.VideoOut.Upscaling.Streamline;

namespace SharpEmu.Libs.VideoOut.Upscaling;

internal readonly record struct SuperResolutionImage(
    ulong Image,
    ulong View,
    uint Format,
    uint Width,
    uint Height,
    uint MipLevels,
    uint ArrayLayers,
    uint Usage,
    uint CreateFlags,
    bool IsDepth,
    bool FsrCompatibleView)
{
    public bool IsValid => Image != 0 && View != 0;
}

internal sealed class SuperResolutionFrame
{
    public required SuperResolutionDispatch Dispatch { get; init; }

    public SuperResolutionImage Output { get; init; }

    public SuperResolutionImage Color { get; init; }

    public SuperResolutionImage Depth { get; init; }

    public SuperResolutionImage MotionVectors { get; init; }

    public SuperResolutionImage Exposure { get; init; }

    public SuperResolutionImage Reactive { get; init; }

    public uint RenderX { get; init; }

    public uint RenderY { get; init; }

    public uint RenderWidth { get; init; }

    public uint RenderHeight { get; init; }

    public uint OutputWidth { get; init; }

    public uint OutputHeight { get; init; }
}

internal static class SuperResolutionTuning
{
    public static readonly float JitterSignX = Sign("SHARPEMU_PSSR_JITTER_SIGN", 0);
    public static readonly float JitterSignY = Sign("SHARPEMU_PSSR_JITTER_SIGN", 1);
    public static readonly float MotionSignX = Sign("SHARPEMU_PSSR_MV_SIGN", 0);
    public static readonly float MotionSignY = Sign("SHARPEMU_PSSR_MV_SIGN", 1);
    public static readonly string? Exposure = Environment.GetEnvironmentVariable("SHARPEMU_PSSR_EXPOSURE")?.Trim().ToLowerInvariant();
    public static readonly string? Hdr = Environment.GetEnvironmentVariable("SHARPEMU_PSSR_HDR")?.Trim();

    public static bool IsHdrFormat(uint format) => (Format)format is
        Format.R16G16B16A16Sfloat or Format.R32G32B32A32Sfloat or Format.B10G11R11UfloatPack32 or
        Format.E5B9G9R9UfloatPack32 or Format.R16G16B16Sfloat or Format.R32G32B32Sfloat;

    public static Matrix4x4 Projection(SuperResolutionDispatch dispatch) => new(
        dispatch.Projection(0, 0), dispatch.Projection(0, 1), dispatch.Projection(0, 2), dispatch.Projection(0, 3),
        dispatch.Projection(1, 0), dispatch.Projection(1, 1), dispatch.Projection(1, 2), dispatch.Projection(1, 3),
        dispatch.Projection(2, 0), dispatch.Projection(2, 1), dispatch.Projection(2, 2), dispatch.Projection(2, 3),
        dispatch.Projection(3, 0), dispatch.Projection(3, 1), dispatch.Projection(3, 2), dispatch.Projection(3, 3));

    public static bool? InferReverseDepth(in Matrix4x4 projection)
    {
        bool? result = null;
        var columnDistance = MathF.Abs(MathF.Abs(projection.M43) - 1f);
        var rowDistance = MathF.Abs(MathF.Abs(projection.M34) - 1f);
        ReadOnlySpan<bool> conventions = columnDistance < rowDistance ? [true] : rowDistance < columnDistance ? [false] : [true, false];
        foreach (var column in conventions)
        {
            foreach (var forward in (ReadOnlySpan<float>)[1f, -1f])
            {
                var near = ClipDepth(projection, column, forward, out var nearW);
                var far = ClipDepth(projection, column, forward * 1000f, out var farW);
                if (nearW <= 0 || farW <= 0 || !float.IsFinite(near) || !float.IsFinite(far) || near == far)
                {
                    continue;
                }

                var reverse = near > far;
                if (result is { } previous && previous != reverse)
                {
                    return null;
                }

                result = reverse;
            }
        }

        return result;
    }

    private static float ClipDepth(in Matrix4x4 projection, bool column, float z, out float w)
    {
        var clipZ = column ? projection.M33 * z + projection.M34 : projection.M33 * z + projection.M43;
        w = column ? projection.M43 * z + projection.M44 : projection.M34 * z + projection.M44;
        return clipZ / w;
    }

    private static float Sign(string variable, int component)
    {
        var parts = Environment.GetEnvironmentVariable(variable)?.Split(',', StringSplitOptions.TrimEntries);
        return parts is { Length: > 0 } &&
               float.TryParse(parts[Math.Min(component, parts.Length - 1)], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
               value < 0
            ? -1f
            : 1f;
    }
}

internal sealed unsafe class BlitUpscaler(Vk vk, PhysicalDevice physicalDevice)
{
    private readonly Dictionary<Format, FormatFeatureFlags> _features = [];
    private int _reported;

    public bool TryUpscale(CommandBuffer command, SuperResolutionFrame frame)
    {
        var source = frame.Color;
        var destination = frame.Output;
        if (!source.IsValid || !destination.IsValid || frame.RenderWidth == 0 || frame.RenderHeight == 0)
        {
            return false;
        }

        var sourceFeatures = Features((Format)source.Format);
        var destinationFeatures = Features((Format)destination.Format);
        if ((sourceFeatures & FormatFeatureFlags.BlitSrcBit) == 0 || (destinationFeatures & FormatFeatureFlags.BlitDstBit) == 0)
        {
            if (Interlocked.Increment(ref _reported) <= 4)
            {
                Console.Error.WriteLine(
                    $"[PSSR][WARN] The basic upscaler cannot blit {(Format)source.Format} to {(Format)destination.Format}; the output keeps its previous contents.");
            }

            return false;
        }

        var filter = (sourceFeatures & FormatFeatureFlags.SampledImageFilterLinearBit) != 0 ? Filter.Linear : Filter.Nearest;
        var region = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        region.SrcOffsets[0] = new Offset3D((int)frame.RenderX, (int)frame.RenderY, 0);
        region.SrcOffsets[1] = new Offset3D(
            (int)Math.Min(frame.RenderX + frame.RenderWidth, source.Width),
            (int)Math.Min(frame.RenderY + frame.RenderHeight, source.Height),
            1);
        region.DstOffsets[0] = new Offset3D(0, 0, 0);
        region.DstOffsets[1] = new Offset3D((int)Math.Min(frame.OutputWidth, destination.Width), (int)Math.Min(frame.OutputHeight, destination.Height), 1);
        vk.CmdBlitImage(command, new Image(source.Image), ImageLayout.General, new Image(destination.Image), ImageLayout.General, 1, &region, filter);
        return true;
    }

    private FormatFeatureFlags Features(Format format)
    {
        if (!_features.TryGetValue(format, out var features))
        {
            vk.GetPhysicalDeviceFormatProperties(physicalDevice, format, out var properties);
            features = properties.OptimalTilingFeatures;
            _features[format] = features;
        }

        return features;
    }
}

internal sealed class SuperResolutionService(Vk vk, PhysicalDevice physicalDevice, StreamlineRuntime? streamline, FsrRuntime? fsr) : IDisposable
{
    private const int IdleFramesBeforeRelease = 120;

    private readonly BlitUpscaler _basic = new(vk, physicalDevice);
    private long _dlssFrames;
    private long _basicFrames;
    private int _idleFrames;

    public StreamlineRuntime? Streamline => streamline;

    public FsrRuntime? Fsr => fsr;

    public bool PrefersDlss => streamline is { IsActive: true } &&
        SuperResolutionSettings.Requested is SuperResolutionUpscaler.Auto or SuperResolutionUpscaler.Dlss;

    public bool TryDlss(CommandBuffer command, SuperResolutionFrame frame)
    {
        if (!PrefersDlss || streamline is null || !streamline.Evaluate(command, frame))
        {
            return false;
        }

        Volatile.Write(ref _idleFrames, 0);
        if (Interlocked.Increment(ref _dlssFrames) == 1)
        {
            Console.Error.WriteLine("[PSSR][INFO] Upscaling with DLSS.");
        }

        return true;
    }

    public bool TryFsr(CommandBuffer command, SuperResolutionFrame frame, ulong tick, ulong completedTick)
    {
        if (fsr is null || !fsr.Evaluate(command, frame, tick, completedTick))
        {
            return false;
        }

        Volatile.Write(ref _idleFrames, 0);
        return true;
    }

    public bool WentIdle()
    {
        if (streamline is not { HoldsResources: true } && fsr is not { HoldsResources: true })
        {
            Volatile.Write(ref _idleFrames, 0);
            return false;
        }

        return Interlocked.Increment(ref _idleFrames) == IdleFramesBeforeRelease;
    }

    public void ReleaseResources()
    {
        if (streamline is { HoldsResources: true })
        {
            streamline.FreeResources();
            Console.Error.WriteLine("[DLSS][INFO] The game stopped using PSSR; DLSS resources are released until it upscales again.");
        }

        if (fsr is { HoldsResources: true })
        {
            fsr.ReleaseResources();
            Console.Error.WriteLine("[FSR][INFO] The game stopped using PSSR; FSR resources are released until it upscales again.");
        }
    }

    public bool TryBasic(CommandBuffer command, SuperResolutionFrame frame)
    {
        if (!_basic.TryUpscale(command, frame))
        {
            return false;
        }

        if (Interlocked.Increment(ref _basicFrames) == 1)
        {
            Console.Error.WriteLine("[PSSR][INFO] Upscaling with the basic upscaler.");
        }

        return true;
    }

    public void Dispose()
    {
        fsr?.Dispose();
        streamline?.Dispose();
    }
}

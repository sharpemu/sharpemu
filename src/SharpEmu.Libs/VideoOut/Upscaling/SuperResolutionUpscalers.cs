// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using Silk.NET.Vulkan;
using SharpEmu.Libs.Gpu.Upscaling;
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
    bool IsDepth)
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

internal sealed class SuperResolutionService(Vk vk, PhysicalDevice physicalDevice, StreamlineRuntime? streamline) : IDisposable
{
    private const int IdleFramesBeforeRelease = 120;

    private readonly BlitUpscaler _basic = new(vk, physicalDevice);
    private long _dlssFrames;
    private long _basicFrames;
    private int _framesWithoutDlss;

    public StreamlineRuntime? Streamline => streamline;

    public bool PrefersDlss => streamline is { IsActive: true } &&
        SuperResolutionSettings.Requested is SuperResolutionUpscaler.Auto or SuperResolutionUpscaler.Dlss;

    public bool TryDlss(CommandBuffer command, SuperResolutionFrame frame)
    {
        if (!PrefersDlss || streamline is null || !streamline.Evaluate(command, frame))
        {
            return false;
        }

        Volatile.Write(ref _framesWithoutDlss, 0);
        if (Interlocked.Increment(ref _dlssFrames) == 1)
        {
            Console.Error.WriteLine("[PSSR][INFO] Upscaling with DLSS.");
        }

        return true;
    }

    public bool DlssWentIdle()
    {
        if (streamline is not { HoldsResources: true })
        {
            Volatile.Write(ref _framesWithoutDlss, 0);
            return false;
        }

        return Interlocked.Increment(ref _framesWithoutDlss) == IdleFramesBeforeRelease;
    }

    public void ReleaseDlss()
    {
        if (streamline is not { HoldsResources: true })
        {
            return;
        }

        streamline.FreeResources();
        Console.Error.WriteLine("[DLSS][INFO] The game stopped using PSSR; DLSS resources are released until it upscales again.");
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

    public void Dispose() => streamline?.Dispose();
}

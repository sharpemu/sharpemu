// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Metal;
using SharpEmu.Libs.Gpu;

public enum HostWindowMode
{
    Windowed,
    Borderless,
    ExclusiveFullscreen,
}

public enum HostScalingMode
{
    Fit,
    Cover,
    Stretch,
    Integer,
}

public enum HostHdrMode
{
    Auto,
    On,
    Off,
}

public enum PerformanceOverlayCorner
{
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

public enum PerformanceOverlayMode
{
    Full,
    Minimal,
    TitleBar,
}

public sealed record HostVideoOptions
{
    public static HostVideoOptions Default { get; } = new();

    public HostWindowMode WindowMode { get; init; } = HostWindowMode.Windowed;

    public HostScalingMode ScalingMode { get; init; } = HostScalingMode.Fit;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int DisplayIndex { get; init; }

    public int RefreshRate { get; init; }

    public bool VSync { get; init; } = true;

    public HostHdrMode HdrMode { get; init; } = HostHdrMode.Auto;

    public bool OverlayEnabled { get; init; } = true;
    public PerformanceOverlayCorner OverlayCorner { get; init; } = PerformanceOverlayCorner.TopRight;
    public PerformanceOverlayMode OverlayMode { get; init; } = PerformanceOverlayMode.TitleBar;

    // On macOS, Auto keeps guests on SDR output: reporting HDR moves titles onto render paths
    // MoltenVK cannot build yet (Silent Hill's HDR switch fails a pipeline compile and then
    // crashes inside MoltenVK on every run). --hdr=on still opts in.
    internal static bool AutoHdrAllowed => !OperatingSystem.IsMacOS();

    internal bool CanUseHdr(bool displayHdrEnabled, bool surfaceSupportsHdr) =>
        CanUseHdr(displayHdrEnabled, surfaceSupportsHdr, AutoHdrAllowed);

    internal bool CanUseHdr(bool displayHdrEnabled, bool surfaceSupportsHdr, bool autoAllowed) =>
        surfaceSupportsHdr && (HdrMode == HostHdrMode.On || HdrMode == HostHdrMode.Auto && autoAllowed && displayHdrEnabled);

    public HostVideoOptions Normalize() => this with
    {
        Width = Math.Clamp(Width, 640, 16384),
        Height = Math.Clamp(Height, 360, 16384),
        DisplayIndex = Math.Max(0, DisplayIndex),
        RefreshRate = Math.Clamp(RefreshRate, 0, 1000),
        HdrMode = Enum.IsDefined(HdrMode) ? HdrMode : HostHdrMode.Auto,
        OverlayCorner = Enum.IsDefined(OverlayCorner) ? OverlayCorner : PerformanceOverlayCorner.TopRight,
        OverlayMode = Enum.IsDefined(OverlayMode) ? OverlayMode : PerformanceOverlayMode.TitleBar,
    };
}

public static class HostVideoHost
{
    private static HostVideoOptions _currentOptions = HostVideoOptions.Default;
    private static bool _videoConfigured;

    public static HostVideoOptions CurrentOptions => Volatile.Read(ref _currentOptions);

    internal static bool IsHdrOutputSupported =>
        Volatile.Read(ref _videoConfigured) && CurrentOptions.HdrMode != HostHdrMode.Off &&
        GuestGpu.Current.BackendName == "Vulkan" && VulkanVideoPresenter.QueryHdrOutputSupport();

    public static bool TryConfigureVideo(HostVideoOptions options)
    {
        var normalized = options.Normalize();
        Volatile.Write(ref _currentOptions, normalized);
        var configured = VulkanVideoPresenter.TryConfigureVideo(normalized) &
                         MetalVideoPresenter.TryConfigureVideo(normalized);
        if (configured)
            Volatile.Write(ref _videoConfigured, true);
        return configured;
    }
}

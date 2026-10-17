// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct OverlayRectangle(int Left, int Top, int Width, int Height);

internal sealed class PerformanceOverlayState
{
    public bool Enabled { get; private set; }
    public PerformanceOverlayCorner Corner { get; private set; }
    public PerformanceOverlayMode Mode { get; private set; }
    public bool DrawOnScreen => Enabled && Mode != PerformanceOverlayMode.TitleBar;
    public int DisplayWidth => Mode == PerformanceOverlayMode.Full ? PerfOverlay.PanelWidth : PerfOverlay.MinimalPanelWidth;
    public int DisplayHeight => Mode == PerformanceOverlayMode.Full ? PerfOverlay.PanelHeight : PerfOverlay.MinimalPanelHeight;

    public void Configure(HostVideoOptions options, bool hiddenByEnvironment)
    {
        var normalized = options.Normalize();
        Enabled = normalized.OverlayEnabled && !hiddenByEnvironment;
        Corner = normalized.OverlayCorner;
        Mode = normalized.OverlayMode;
    }

    public void Toggle() => Enabled = !Enabled;

    public void CycleCorner() => Corner = Corner switch
    {
        PerformanceOverlayCorner.TopLeft => PerformanceOverlayCorner.TopRight,
        PerformanceOverlayCorner.TopRight => PerformanceOverlayCorner.BottomRight,
        PerformanceOverlayCorner.BottomRight => PerformanceOverlayCorner.BottomLeft,
        _ => PerformanceOverlayCorner.TopLeft,
    };

    public OverlayRectangle GetRectangle(int surfaceWidth, int surfaceHeight)
    {
        var horizontalMargin = Math.Min(12, Math.Max(0, surfaceWidth) / 2);
        var verticalMargin = Math.Min(12, Math.Max(0, surfaceHeight) / 2);
        var width = Math.Clamp(surfaceWidth - 2 * horizontalMargin, 0, DisplayWidth);
        var height = Math.Clamp(surfaceHeight - 2 * verticalMargin, 0, DisplayHeight);
        var left = Corner is PerformanceOverlayCorner.TopRight or PerformanceOverlayCorner.BottomRight
            ? surfaceWidth - horizontalMargin - width : horizontalMargin;
        var top = Corner is PerformanceOverlayCorner.BottomLeft or PerformanceOverlayCorner.BottomRight
            ? surfaceHeight - verticalMargin - height : verticalMargin;
        return new(Math.Max(0, left), Math.Max(0, top), width, height);
    }
}

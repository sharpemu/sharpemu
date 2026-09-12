// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PerformanceOverlayTests
{
    [Theory]
    [InlineData(PerformanceOverlayCorner.TopLeft, 12, 12)]
    [InlineData(PerformanceOverlayCorner.TopRight, 1280 - 12 - PerfOverlay.PanelWidth, 12)]
    [InlineData(PerformanceOverlayCorner.BottomRight, 1280 - 12 - PerfOverlay.PanelWidth, 720 - 12 - PerfOverlay.PanelHeight)]
    [InlineData(PerformanceOverlayCorner.BottomLeft, 12, 720 - 12 - PerfOverlay.PanelHeight)]
    public void FullPanelUsesSelectedCorner(PerformanceOverlayCorner corner, int left, int top)
    {
        var state = new PerformanceOverlayState();
        state.Configure(new HostVideoOptions { OverlayCorner = corner }, false);

        Assert.Equal(
            new OverlayRectangle(left, top, PerfOverlay.PanelWidth, PerfOverlay.PanelHeight),
            state.GetRectangle(1280, 720));
    }

    [Theory]
    [InlineData(PerformanceOverlayCorner.TopLeft)]
    [InlineData(PerformanceOverlayCorner.TopRight)]
    [InlineData(PerformanceOverlayCorner.BottomRight)]
    [InlineData(PerformanceOverlayCorner.BottomLeft)]
    public void SmallSurfaceClipsPanelInsideItsBounds(PerformanceOverlayCorner corner)
    {
        var state = new PerformanceOverlayState();
        state.Configure(new HostVideoOptions { OverlayCorner = corner }, false);
        foreach (var size in new[] { 0, 1, 12, 24, 100, 4096 })
        {
            var rectangle = state.GetRectangle(size, size);
            Assert.InRange(rectangle.Left, 0, size);
            Assert.InRange(rectangle.Top, 0, size);
            Assert.InRange(rectangle.Left + rectangle.Width, 0, size);
            Assert.InRange(rectangle.Top + rectangle.Height, 0, size);
        }
    }

    [Fact]
    public void HotkeysChangeOnlyRuntimeState()
    {
        var options = new HostVideoOptions { OverlayCorner = PerformanceOverlayCorner.TopRight };
        var state = new PerformanceOverlayState();
        state.Configure(options, false);
        state.Toggle();
        state.CycleCorner();

        Assert.False(state.Enabled);
        Assert.Equal(PerformanceOverlayCorner.BottomRight, state.Corner);
        Assert.True(options.OverlayEnabled);
        Assert.Equal(PerformanceOverlayCorner.TopRight, options.OverlayCorner);

        state.Configure(options, false);
        Assert.True(state.Enabled);
        Assert.Equal(PerformanceOverlayCorner.TopRight, state.Corner);
        for (var index = 0; index < 4; index++)
        {
            state.CycleCorner();
        }
        Assert.Equal(PerformanceOverlayCorner.TopRight, state.Corner);
    }

    [Fact]
    public void EnvironmentCanStartHiddenWithoutDisablingTheHotkey()
    {
        var state = new PerformanceOverlayState();
        state.Configure(HostVideoOptions.Default, true);
        Assert.False(state.DrawOnScreen);
        state.Toggle();
        Assert.True(state.DrawOnScreen);
    }

    [Theory]
    [InlineData(PerformanceOverlayMode.Full, true, PerfOverlay.PanelHeight)]
    [InlineData(PerformanceOverlayMode.Minimal, true, PerfOverlay.MinimalPanelHeight)]
    [InlineData(PerformanceOverlayMode.TitleBar, false, PerfOverlay.MinimalPanelHeight)]
    public void ModeSelectsPanelOrTitleBar(PerformanceOverlayMode mode, bool onScreen, int height)
    {
        var state = new PerformanceOverlayState();
        state.Configure(new HostVideoOptions { OverlayMode = mode }, false);
        Assert.Equal(onScreen, state.DrawOnScreen);
        Assert.Equal(height, state.DisplayHeight);
        state.Toggle();
        Assert.False(state.DrawOnScreen);
    }

    [Fact]
    public void MinimalBottomPanelUsesItsOwnDimensions()
    {
        var state = new PerformanceOverlayState();
        state.Configure(new HostVideoOptions
        {
            OverlayCorner = PerformanceOverlayCorner.BottomLeft,
            OverlayMode = PerformanceOverlayMode.Minimal,
        }, false);
        Assert.Equal(
            new OverlayRectangle(12, 720 - 12 - PerfOverlay.MinimalPanelHeight,
                PerfOverlay.MinimalPanelWidth, PerfOverlay.MinimalPanelHeight),
            state.GetRectangle(1280, 720));
    }

    [Theory]
    [InlineData(PerformanceOverlayCorner.TopLeft, false, false)]
    [InlineData(PerformanceOverlayCorner.TopRight, true, false)]
    [InlineData(PerformanceOverlayCorner.BottomRight, true, true)]
    [InlineData(PerformanceOverlayCorner.BottomLeft, false, true)]
    public void MinimalWidthKeepsThePanelAtItsSelectedCorner(
        PerformanceOverlayCorner corner, bool rightAligned, bool bottomAligned)
    {
        var state = new PerformanceOverlayState();
        state.Configure(new HostVideoOptions
        {
            OverlayCorner = corner,
            OverlayMode = PerformanceOverlayMode.Minimal,
        }, false);
        var rectangle = state.GetRectangle(1280, 720);
        Assert.Equal(PerfOverlay.MinimalPanelWidth, state.DisplayWidth);
        Assert.Equal(PerfOverlay.MinimalPanelWidth, rectangle.Width);
        Assert.Equal(rightAligned ? 1280 - 12 - rectangle.Width : 12, rectangle.Left);
        Assert.Equal(bottomAligned ? 720 - 12 - rectangle.Height : 12, rectangle.Top);

        var clipped = state.GetRectangle(100, 100);
        Assert.Equal(76, clipped.Width);
        Assert.Equal(12, clipped.Left);
    }

    [Fact]
    public void SharedPixelBufferCanHoldBothPanelSizes()
    {
        Assert.True(PerfOverlay.PixelBufferWidth >= PerfOverlay.PanelWidth);
        Assert.True(PerfOverlay.PixelBufferWidth >= PerfOverlay.MinimalPanelWidth);
        Assert.True(PerfOverlay.PixelBufferHeight >= PerfOverlay.PanelHeight);
        Assert.True(PerfOverlay.PixelBufferHeight >= PerfOverlay.MinimalPanelHeight);
    }

    [Fact]
    public void InvalidOptionsUseDisplayDefaults()
    {
        var options = new HostVideoOptions
        {
            OverlayCorner = (PerformanceOverlayCorner)100,
            OverlayMode = (PerformanceOverlayMode)100,
            OverlayEnabled = false,
        }.Normalize();
        Assert.Equal(PerformanceOverlayCorner.TopRight, options.OverlayCorner);
        Assert.Equal(PerformanceOverlayMode.Full, options.OverlayMode);
        Assert.False(options.OverlayEnabled);
    }

    [Fact]
    public void TitleBarKeepsPerformanceAndCaptureHint()
    {
        const string summary = "FPS 60.0 | CPU 10% | GPU 25% | TIME 00:00:10";
        Assert.Equal($"Application · {summary} · Press F12 for capture",
            SdlHostWindow.FormatWindowTitle("Application", true, summary));
        Assert.Equal($"Application · {summary}", SdlHostWindow.FormatWindowTitle("Application", false, summary));
        Assert.Equal("Application", SdlHostWindow.FormatWindowTitle("Application", false, ""));
    }

    [Fact]
    public void GpuUsageUsesBusiestValidEngine()
    {
        var usage = WindowsGpuUsage.IncludeEngine(double.NaN, 0, 30);
        usage = WindowsGpuUsage.IncludeEngine(usage, 1, 70);
        usage = WindowsGpuUsage.IncludeEngine(usage, 0, 50);
        usage = WindowsGpuUsage.IncludeEngine(usage, 0xC0000BC6, 100);
        usage = WindowsGpuUsage.IncludeEngine(usage, 0, double.NaN);
        usage = WindowsGpuUsage.IncludeEngine(usage, 0, -1);
        Assert.Equal(70, usage);
        Assert.Equal(100, WindowsGpuUsage.IncludeEngine(usage, 0, 105));
    }

    [Theory]
    [InlineData(double.NaN, "N/A")]
    [InlineData(double.PositiveInfinity, "N/A")]
    [InlineData(0, "0%")]
    [InlineData(100, "100%")]
    public void MissingGpuCounterDoesNotReportZero(double value, string label)
    {
        Assert.Equal(label, PerfOverlay.FormatUsage(value));
    }

    [Fact]
    public void GpuSamplerCanCloseWithAQueuedSample()
    {
        var sampler = new WindowsGpuUsage();
        sampler.RequestSample();
        sampler.Dispose();
        sampler.RequestSample();
        sampler.Dispose();
    }
}

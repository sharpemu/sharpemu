// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.GUI;
using SkiaSharp;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ShaderCacheProgressOverlayTests
{
    static ShaderCacheProgressOverlayTests() => GuiLauncher.ConfigureSplashFont();

    [Fact]
    public void SplashUsesTheBundledGuiFont()
    {
        var typeface = (SKTypeface)typeof(ShaderCacheProgressOverlay)
            .GetField("_typeface", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal("Inter", typeface.FamilyName);
    }

    [Theory]
    [InlineData((int)ShaderCachePhase.Loading)]
    [InlineData((int)ShaderCachePhase.Recovering)]
    [InlineData((int)ShaderCachePhase.Scanning)]
    [InlineData((int)ShaderCachePhase.Preparing)]
    [InlineData((int)ShaderCachePhase.Compiling)]
    [InlineData((int)ShaderCachePhase.Finalizing)]
    public void EveryStageDrawsOverTheSplashWithoutChangingItsSource(int phase)
    {
        var original = Enumerable.Repeat((byte)120, 640 * 360 * 4).ToArray();
        var pixels = original.ToArray();
        var snapshot = new ShaderCacheProgressSnapshot(new((ShaderCachePhase)phase, 50, 100),
            20, 100, 50, 200, 1, 4, 10, 60, 2, 4, 1, 12);

        ShaderCacheProgressOverlay.Draw(pixels, 640, 360, snapshot);

        Assert.All(original, value => Assert.Equal(120, value));
        Assert.Equal(30, pixels[0]);
        Assert.Equal(120, pixels[3]);
        Assert.Contains((byte)255, pixels);
    }

    [Fact]
    public void AChangedCounterRepaintsTheProgressBars()
    {
        var snapshot = new ShaderCacheProgressSnapshot(new(ShaderCachePhase.Compiling),
            0, 100, 0, 100, 0, 2, 0, 0, 0, 2, 1, 1);
        var first = new byte[640 * 360 * 4];
        var next = new byte[first.Length];
        ShaderCacheProgressOverlay.Draw(first, 640, 360, snapshot);
        ShaderCacheProgressOverlay.Draw(next, 640, 360, snapshot with { ComputeDone = 100, GraphicsDone = 50, Merged = 1 });
        Assert.False(first.AsSpan().SequenceEqual(next));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(200, 100)]
    [InlineData(1920, 1080)]
    public void UnknownTotalsAndSmallWindowsAreClippedSafely(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var snapshot = new ShaderCacheProgressSnapshot(new(ShaderCachePhase.Preparing),
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 42);
        ShaderCacheProgressOverlay.Draw(pixels, width, height, snapshot);
    }

    [Fact]
    public void AnIncorrectPixelBufferIsRejected()
    {
        var snapshot = default(ShaderCacheProgressSnapshot);
        Assert.Throws<ArgumentException>(() => ShaderCacheProgressOverlay.Draw(new byte[4], 2, 2, snapshot));
    }

    [Fact]
    public void ProgressRefreshesAnUnchangedSplashAndRequestsOneFinalCleanFrame()
    {
        var type = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;
        var presenter = RuntimeHelpers.GetUninitializedObject(type);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var progress = type.GetField("_shaderCacheProgress", flags)!;
        var visible = type.GetField("_shaderCacheSplashVisible", flags)!;
        var lastDraw = type.GetField("_shaderCacheSplashLastDraw", flags)!;
        var refresh = type.GetProperty("ShaderCacheSplashNeedsRefresh", flags)!;
        progress.SetValue(presenter, new ShaderCacheProgress(ShaderCachePhase.Compiling));
        Assert.Equal(true, refresh.GetValue(presenter));
        lastDraw.SetValue(presenter, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60);
        Assert.Equal(false, refresh.GetValue(presenter));
        progress.SetValue(presenter, null);
        visible.SetValue(presenter, true);
        Assert.Equal(true, refresh.GetValue(presenter));
        visible.SetValue(presenter, false);
        Assert.Equal(false, refresh.GetValue(presenter));
    }
}

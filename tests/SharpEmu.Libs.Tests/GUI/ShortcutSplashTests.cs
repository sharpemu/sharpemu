// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Avalonia;
using SkiaSharp;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class ShortcutSplashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharpemu-splash-{Guid.NewGuid():N}");

    public ShortcutSplashTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(5120, 2160)]
    [InlineData(1200, 2000)]
    [InlineData(400, 300)]
    public void PrepareKeepsTheOriginalPngAndSurvivesSourceRemoval(int width, int height)
    {
        var source = Path.Combine(_root, "pic0.png");
        using (var image = new SKBitmap(width, height))
        {
            image.Erase(SKColors.CornflowerBlue);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(source, encoded.ToArray());
        }

        var game = Path.Combine(_root, "game", "eboot.bin");
        var cache = Path.Combine(_root, "cache");
        var path = ShortcutSplashCache.Prepare(game, source, cache);
        Assert.NotNull(path);
        Assert.Equal(ShortcutSplashCache.PathForGame(game, cache), path);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(path));
        File.Delete(source);
        using var cached = SKBitmap.Decode(path);
        Assert.NotNull(cached);
        Assert.Equal(width, cached.Width);
        Assert.Equal(height, cached.Height);
        Assert.Equal(SKColors.CornflowerBlue, cached.GetPixel(cached.Width / 2, cached.Height / 2));
    }

    [Fact]
    public void PrepareUpgradesExistingLowResolutionCaches()
    {
        var game = Path.Combine(_root, "eboot.bin");
        var source = Path.Combine(_root, "pic0.png");
        var path = ShortcutSplashCache.PathForGame(game, _root);
        using (var original = new SKBitmap(1920, 1080))
        using (var small = new SKBitmap(889, 500))
        {
            original.Erase(SKColors.CornflowerBlue);
            small.Erase(SKColors.CornflowerBlue);
            using var originalPng = original.Encode(SKEncodedImageFormat.Png, 100);
            using var smallPng = small.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(source, originalPng.ToArray());
            File.WriteAllBytes(path, smallPng.ToArray());
        }

        Assert.Equal(path, ShortcutSplashCache.Prepare(game, source, _root));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 1.0, 889, 500)]
    [InlineData(1920, 1080, 1920, 1080, 1.25, 1111, 625)]
    [InlineData(3840, 2160, 3840, 2160, 2.0, 1778, 1000)]
    [InlineData(1200, 2000, 1920, 1080, 1.0, 300, 500)]
    [InlineData(400, 300, 3840, 2160, 2.0, 400, 300)]
    [InlineData(1920, 1080, 640, 360, 1.0, 640, 360)]
    public void FitToScreenRespectsDpiAndNeverEnlargesCachedPixels(int width, int height,
        int availableWidth, int availableHeight, double scaling, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), ShortcutSplashWindow.FitToScreen(
            new PixelSize(width, height), new PixelSize(availableWidth, availableHeight), scaling));
    }

    [Fact]
    public void PrepareSkipsMissingOrCorruptArtWithoutBlockingTheGame()
    {
        var game = Path.Combine(_root, "eboot.bin");
        Assert.Null(ShortcutSplashCache.Prepare(game, null, _root));
        Assert.Null(ShortcutSplashCache.Prepare(game, Path.Combine(_root, "missing.png"), _root));
        var corrupt = Path.Combine(_root, "pic0.png");
        File.WriteAllText(corrupt, "not a PNG");

        Assert.Null(ShortcutSplashCache.Prepare(game, corrupt, _root));
        Assert.False(File.Exists(ShortcutSplashCache.PathForGame(game, _root)));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void EachInstallationHasItsOwnCachedSplash()
    {
        var first = ShortcutSplashCache.PathForGame(Path.Combine(_root, "v1", "eboot.bin"), _root);
        var second = ShortcutSplashCache.PathForGame(Path.Combine(_root, "v2", "eboot.bin"), _root);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("[LOADER][INFO] SDL3 window ready: mode=Windowed size=1280x720", true)]
    [InlineData("[LOADER][INFO] Vulkan VideoOut initialized", false)]
    [InlineData("[LOADER][ERROR] SDL3 window ready: failed", false)]
    public void SplashClosesOnlyWhenTheGameWindowIsShown(string line, bool ready)
    {
        Assert.Equal(ready, EmulatorProcess.IsWindowReadyMessage(line));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

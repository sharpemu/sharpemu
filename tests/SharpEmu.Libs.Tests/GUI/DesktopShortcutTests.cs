// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using SkiaSharp;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class DesktopShortcutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"SharpEmu shortcut {Guid.NewGuid():N}");
    private readonly GameEntry _game;
    private readonly string _emulator;
    private readonly string _desktop;

    public DesktopShortcutTests()
    {
        var gameDirectory = Path.Combine(_root, "Games 100% & saves");
        Directory.CreateDirectory(gameDirectory);
        var gamePath = Path.Combine(gameDirectory, "eboot.bin");
        File.WriteAllText(gamePath, "game fixture");
        _game = new GameEntry("Test game", "PPSA00001", "01.003.000", gamePath, 0, null, null);
        _emulator = Path.Combine(_root, OperatingSystem.IsWindows() ? "SharpEmu.exe" : "SharpEmu");
        File.WriteAllText(_emulator, "emulator fixture");
        _desktop = Path.Combine(_root, "Desktop");
    }

    [Fact]
    public void ShortcutArgumentsContainOnlyTheGameAndSilentMode()
    {
        Assert.Equal(new[] { "--shortcut", _game.Path }, LaunchConfiguration.ShortcutArguments(_game.Path));
    }

    [Fact]
    public void CreateUsesNativeDesktopFilesWithoutOverwritingExistingShortcuts()
    {
        var first = DesktopShortcut.Create(_game, _emulator, _desktop);
        var original = File.ReadAllBytes(first);
        var second = DesktopShortcut.Create(_game, _emulator, _desktop);

        Assert.Equal(_desktop, Path.GetDirectoryName(first));
        Assert.Equal(OperatingSystem.IsWindows() ? ".lnk" : ".desktop", Path.GetExtension(first));
        Assert.Equal("Test game" + Path.GetExtension(first), Path.GetFileName(first));
        Assert.NotEmpty(original);
        Assert.NotEqual(first, second);
        Assert.Equal(original, File.ReadAllBytes(first));
        Assert.Equal(2, Directory.GetFiles(_desktop).Length);
        if (OperatingSystem.IsLinux())
            Assert.True(File.GetUnixFileMode(first).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void CreateAddsTheVersionOnlyForGamesWithMultipleInstallations()
    {
        _game.SetVersionCount(2);

        var shortcut = DesktopShortcut.Create(_game, _emulator, _desktop);

        Assert.Equal("Test game (v01.003.000)" + Path.GetExtension(shortcut), Path.GetFileName(shortcut));
        Assert.Contains("Name=Test game (v01.003.000)\n", DesktopShortcut.LinuxEntry(_game, _emulator));
        _game.SetVersionCount(1);
        Assert.Contains("Name=Test game\n", DesktopShortcut.LinuxEntry(_game, _emulator));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CachedIconKeepsGamePixelsAndSurvivesSourceRemoval(bool windows)
    {
        var cover = Path.Combine(_root, "icon0.png");
        using (var bitmap = new SKBitmap(new SKImageInfo(512, 512, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                    bitmap.SetPixel(x, y, SKColors.Transparent);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(cover, encoded.ToArray());
        }

        var directory = Path.Combine(_root, "shortcut-icons");
        var icon = DesktopShortcut.CacheIcon(cover, directory, windows);
        Assert.Equal(icon, DesktopShortcut.CacheIcon(cover, directory, windows));
        var bytes = File.ReadAllBytes(icon);
        var png = windows ? bytes[22..] : bytes;
        if (windows)
        {
            Assert.Equal(new byte[] { 0, 0, 1, 0, 1, 0 }, bytes[..6]);
            Assert.Equal(6, png[25]); // RGBA, including the icon's transparent pixels.
        }

        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        Assert.Equal(windows ? 256 : 512, decoded.Width);
        Assert.Equal(windows ? 256 : 512, decoded.Height);
        Assert.Equal(SKColors.CornflowerBlue, decoded.GetPixel(decoded.Width / 2, decoded.Height / 2));
        Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
        File.Delete(cover);
        Assert.True(File.Exists(icon));

        if (windows && OperatingSystem.IsWindows())
        {
            var nativeIcon = LoadImageW(0, icon, 1, 256, 256, 0x10);
            try { Assert.NotEqual(0, nativeIcon); }
            finally { if (nativeIcon != 0) DestroyIcon(nativeIcon); }
        }
    }

    [Fact]
    public void CreateRejectsAnUnavailableGame()
    {
        File.Delete(_game.Path);

        Assert.Throws<FileNotFoundException>(() => DesktopShortcut.Create(_game, _emulator, _desktop));
        Assert.False(Directory.Exists(_desktop));
    }

    [Fact]
    public void LinuxEntryEscapesFieldCodesAndPreventsAdditionalDesktopKeys()
    {
        var game = new GameEntry("Game\nExec=unwanted", "PPSA00001", null,
            Path.Combine(_root, "100%f $game `test` \\\".zar"), 0, null, null);

        var entry = DesktopShortcut.LinuxEntry(game, _emulator);

        Assert.Contains("Name=Game\\nExec=unwanted\n", entry);
        Assert.DoesNotContain("\nExec=unwanted\n", entry);
        Assert.Contains("100%%f", entry);
        Assert.Contains("\\\\$game", entry);
        Assert.Contains("\\\\`test\\\\`", entry);
        Assert.Contains("\"--shortcut\"", entry);
        Assert.Contains("Terminal=false\n", entry);
        Assert.DoesNotContain("--resolution", entry);
        Assert.DoesNotContain("--window-mode", entry);
        Assert.Equal(1, entry.Split('\n').Count(line => line.StartsWith("Exec=")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint instance, string path, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SkiaSharp;

namespace SharpEmu.GUI;

internal static class ShortcutSplashCache
{
    public const int Width = 900;
    public const int Height = 500;

    public static string PathForGame(string gamePath, string? directory = null) =>
        Path.Combine(directory ?? Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "game-art", "shortcut-splashes"),
            PerGameSettings.KeyFor(null, gamePath) + ".png");

    public static string? Prepare(string gamePath, string? backgroundPath, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(backgroundPath) || !File.Exists(backgroundPath))
            return null;

        var path = PathForGame(gamePath, directory);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var sourceInfo = new FileInfo(backgroundPath);
            var cachedInfo = new FileInfo(path);
            if (cachedInfo.Exists && cachedInfo.Length == sourceInfo.Length &&
                cachedInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
                return path;

            using var source = SKBitmap.Decode(backgroundPath) ?? throw new InvalidDataException("The game splash could not be decoded.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Keep the original pixels; the display DPI is only known when the shortcut opens.
            File.Copy(backgroundPath, temporary);
            File.SetAttributes(temporary, FileAttributes.Normal);
            File.SetLastWriteTimeUtc(temporary, sourceInfo.LastWriteTimeUtc);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Console.Error.WriteLine($"[GUI][WARN] Could not cache the shortcut splash: {exception.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}

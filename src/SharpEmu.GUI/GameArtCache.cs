// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace SharpEmu.GUI;

internal static class GameArtCache
{
    private const int PhotoWidth = 512;

    private static readonly string Root = Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "game-art");

    public static Bitmap? Load(string path, int width)
    {
        var source = new FileInfo(path);
        if (!source.Exists)
        {
            return null;
        }

        var cached = CachePath(source, width);
        if (File.Exists(cached))
        {
            try
            {
                return new Bitmap(cached);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                TryDelete(cached);
            }
        }

        Bitmap decoded;
        using (var stream = source.OpenRead())
        {
            decoded = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }

        Store(decoded, cached, width);
        return decoded;
    }

    private static void Store(Bitmap bitmap, string cached, int width)
    {
        var temporary = cached + ".tmp";
        try
        {
            Directory.CreateDirectory(Root);
            using (var output = File.Create(temporary))
            {
                if (width >= PhotoWidth)
                {
                    bitmap.Save(output, new JpegBitmapEncoderOptions { Quality = 90 });
                }
                else
                {
                    bitmap.Save(output, new PngBitmapEncoderOptions());
                }
            }

            File.Move(temporary, cached, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            TryDelete(temporary);
        }
    }

    private static string CachePath(FileInfo source, int width)
    {
        var identity = $"{source.FullName}|{source.Length}|{source.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Root, $"{hash}-{width}{(width >= PhotoWidth ? ".jpg" : ".png")}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

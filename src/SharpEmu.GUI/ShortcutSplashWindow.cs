// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace SharpEmu.GUI;

internal sealed class ShortcutSplashWindow : Window
{
    public ShortcutSplashWindow(string cachedImage)
    {
        var screen = Screens.Primary;
        var scaling = screen?.Scaling ?? 1;
        using var codec = SKCodec.Create(cachedImage) ?? throw new InvalidDataException("The cached splash could not be decoded.");
        var size = FitToScreen(new PixelSize(codec.Info.Width, codec.Info.Height),
            screen?.WorkingArea.Size ?? new PixelSize(ShortcutSplashCache.Width, ShortcutSplashCache.Height), scaling);
        using var stream = File.OpenRead(cachedImage);
        var bitmap = Bitmap.DecodeToWidth(stream, size.Width, BitmapInterpolationMode.HighQuality);
        var image = new Image { Source = bitmap, Stretch = Stretch.UniformToFill, ClipToBounds = true };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        Title = string.Empty;
        Width = bitmap.PixelSize.Width / scaling;
        Height = bitmap.PixelSize.Height / scaling;
        WindowDecorations = WindowDecorations.None;
        Padding = default;
        BorderThickness = default;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Black;
        Content = image;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (screen is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(
                screen.Bounds.X + (screen.Bounds.Width - bitmap.PixelSize.Width) / 2,
                screen.Bounds.Y + (screen.Bounds.Height - bitmap.PixelSize.Height) / 2);
        }
        Closed += (_, _) =>
        {
            image.Source = null;
            bitmap.Dispose();
        };
    }

    internal static PixelSize FitToScreen(PixelSize image, PixelSize available, double scaling)
    {
        var scale = Math.Min(1.0, Math.Min(
            Math.Min(ShortcutSplashCache.Width * scaling, available.Width) / image.Width,
            Math.Min(ShortcutSplashCache.Height * scaling, available.Height) / image.Height));
        return new PixelSize(Math.Max(1, (int)Math.Round(image.Width * scale)),
            Math.Max(1, (int)Math.Round(image.Height * scale)));
    }
}

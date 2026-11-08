// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SkiaSharp;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct ShaderCacheProgressRow(string Label, long Completed, long Total, bool ShowCounts = true);

internal sealed record ShaderCacheProgressView(
    string Title,
    string Subtitle,
    IReadOnlyList<ShaderCacheProgressRow> Rows,
    IReadOnlyList<string> Footer,
    double Seconds);

internal static class ShaderCacheProgressOverlay
{
    private static readonly byte[] FooterShades = [200, 160];
    private static SKTypeface _typeface = SKTypeface.FromFamilyName(null);

    internal static void SetFont(Stream stream) => Volatile.Write(ref _typeface,
        SKTypeface.FromStream(stream) ?? throw new ArgumentException("The splash font is invalid.", nameof(stream)));

    internal static unsafe void Draw(Span<byte> pixels, int width, int height, ShaderCacheProgressView view)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
            throw new ArgumentException("The splash pixel buffer must match its dimensions.", nameof(pixels));

        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)(pixels[offset] / 4);
            pixels[offset + 1] = (byte)(pixels[offset + 1] / 4);
            pixels[offset + 2] = (byte)(pixels[offset + 2] / 4);
        }

        fixed (byte* address = pixels)
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), (nint)address, width * 4))
                throw new InvalidOperationException("The splash pixel buffer cannot be drawn.");
            using var canvas = new SKCanvas(bitmap);
            DrawPanel(canvas, width, height, view);
        }
    }

    private static void DrawPanel(SKCanvas canvas, int width, int height, ShaderCacheProgressView view)
    {
        var scale = Math.Clamp(Math.Min(width / 600, height / 320), 1, 3);
        var line = 14 * scale;
        var barHeight = 2 * scale;
        var margin = Math.Min(24 * scale, width / 8);
        var barWidth = Math.Min(960, width - margin * 2);
        var left = (width - barWidth) / 2;
        var y = Math.Max(0, (height - (view.Rows.Count * 3 + 3 + view.Footer.Count) * line) / 2);

        using var font = new SKFont(Volatile.Read(ref _typeface), 13 * scale) { Edging = SKFontEdging.Antialias, Subpixel = true };
        using var paint = new SKPaint { IsAntialias = true };
        canvas.ClipRect(new SKRect(left, 0, width - left, height));
        Text(canvas, font, paint, width, view.Title, left, ref y, scale, 255);
        font.Size = 10 * scale;
        Text(canvas, font, paint, width, view.Subtitle, left, ref y, scale, 220);
        y += line;

        foreach (var row in view.Rows)
        {
            Row(canvas, font, paint, width, row, left, barWidth, ref y, scale, barHeight, view.Seconds);
        }

        y += line;
        for (var index = 0; index < view.Footer.Count; index++)
        {
            Text(canvas, font, paint, width, view.Footer[index], left, ref y, scale,
                FooterShades[Math.Min(index, FooterShades.Length - 1)]);
        }
    }

    private static void Row(SKCanvas canvas, SKFont font, SKPaint paint, int width, in ShaderCacheProgressRow row,
        int left, int barWidth, ref int y, int scale, int barHeight, double seconds)
    {
        var text = !row.ShowCounts ? row.Label : row.Total > 0
            ? $"{row.Label}: {Math.Min(row.Completed, row.Total)} / {row.Total}"
            : $"{row.Label}: none";
        Text(canvas, font, paint, width, text, left, ref y, scale, 230);
        Rectangle(canvas, paint, left, y, barWidth, barHeight, 80);
        if (row.Total > 0)
        {
            var filled = (int)(barWidth * Math.Clamp(row.Completed / (double)row.Total, 0, 1));
            Rectangle(canvas, paint, left, y, filled, barHeight, 255);
        }
        else if (!row.ShowCounts)
        {
            var segment = Math.Max(1, barWidth / 5);
            var position = (int)((Math.Sin(seconds * 2) + 1) * 0.5 * (barWidth - segment));
            Rectangle(canvas, paint, left + position, y, segment, barHeight, 255);
        }

        y += 18 * scale;
    }

    private static void Text(SKCanvas canvas, SKFont font, SKPaint paint, int width, string text, int left, ref int y, int scale, byte color)
    {
        var size = font.Size;
        var measured = font.MeasureText(text);
        if (measured > width - left * 2)
            font.Size *= (width - left * 2) / measured;
        paint.Color = new SKColor(color, color, color);
        canvas.DrawText(text, width / 2f, y - font.Metrics.Ascent, SKTextAlign.Center, font, paint);
        font.Size = size;
        y += 14 * scale;
    }

    private static void Rectangle(SKCanvas canvas, SKPaint paint, int x, int y, int w, int h, byte color)
    {
        paint.Color = new SKColor(color, color, color);
        canvas.DrawRect(x, y, w, h, paint);
    }
}

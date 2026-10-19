// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SkiaSharp;

namespace SharpEmu.Libs.VideoOut;

internal enum ShaderCachePhase
{
    Loading,
    Recovering,
    Scanning,
    Preparing,
    Compiling,
    Finalizing,
}

internal sealed record ShaderCacheProgress(ShaderCachePhase Phase, long Completed = 0, long Total = 0);

internal readonly record struct ShaderCacheProgressSnapshot(
    ShaderCacheProgress Stage, int ComputeDone, int ComputeTotal, int GraphicsDone, int GraphicsTotal,
    int Merged, int Batches, int Compiled, int Stored, int Skipped, int Workers, int Pass, double Seconds);

internal static class ShaderCacheProgressOverlay
{
    private static SKTypeface _typeface = SKTypeface.FromFamilyName(null);

    internal static void SetFont(Stream stream) => Volatile.Write(ref _typeface,
        SKTypeface.FromStream(stream) ?? throw new ArgumentException("The splash font is invalid.", nameof(stream)));

    internal static unsafe void Draw(Span<byte> pixels, int width, int height, in ShaderCacheProgressSnapshot progress)
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
            DrawPanel(canvas, width, height, progress);
        }
    }

    private static void DrawPanel(SKCanvas canvas, int width, int height, in ShaderCacheProgressSnapshot progress)
    {
        var scale = Math.Clamp(Math.Min(width / 600, height / 320), 1, 3);
        var line = 14 * scale;
        var barHeight = 2 * scale;
        var margin = Math.Min(24 * scale, width / 8);
        var barWidth = Math.Min(960, width - margin * 2);
        var left = (width - barWidth) / 2;
        var rows = progress.Stage.Phase == ShaderCachePhase.Compiling ? 3 : 1;
        var y = Math.Max(0, (height - (rows * 3 + 5) * line) / 2);

        using var font = new SKFont(Volatile.Read(ref _typeface), 13 * scale) { Edging = SKFontEdging.Antialias, Subpixel = true };
        using var paint = new SKPaint { IsAntialias = true };
        canvas.ClipRect(new SKRect(left, 0, width - left, height));
        Text(canvas, font, paint, width, "Preparing pipeline cache", left, ref y, scale, 255);
        font.Size = 10 * scale;
        var phase = progress.Stage.Phase switch
        {
            ShaderCachePhase.Loading => "Loading shader cache from disk",
            ShaderCachePhase.Recovering => "Recovering previous worker results",
            ShaderCachePhase.Scanning => "Scanning game files for shaders",
            ShaderCachePhase.Preparing => "Preparing shader and pipeline work list",
            ShaderCachePhase.Compiling => $"Compiling pipelines - pass {progress.Pass}",
            _ => "Finalizing pipeline cache",
        };
        Text(canvas, font, paint, width, phase, left, ref y, scale, 220);
        y += line;

        if (progress.Stage.Phase == ShaderCachePhase.Compiling)
        {
            Row(canvas, font, paint, width, "Compute pipelines", progress.ComputeDone, progress.ComputeTotal,
                left, barWidth, ref y, scale, barHeight, progress.Seconds);
            Row(canvas, font, paint, width, "Graphics pipelines", progress.GraphicsDone, progress.GraphicsTotal,
                left, barWidth, ref y, scale, barHeight, progress.Seconds);
            Row(canvas, font, paint, width, "Merged worker results", progress.Merged, progress.Batches,
                left, barWidth, ref y, scale, barHeight, progress.Seconds);
        }
        else
        {
            var stage = progress.Stage;
            var label = stage.Total > 0 && stage.Phase is (ShaderCachePhase.Loading or ShaderCachePhase.Scanning)
                ? string.Create(CultureInfo.InvariantCulture, $"{stage.Completed / (1024.0 * 1024):F1} / {stage.Total / (1024.0 * 1024):F1} MB")
                : stage.Total > 0 ? $"{stage.Completed} / {stage.Total}" : "Working...";
            Row(canvas, font, paint, width, label, stage.Completed, stage.Total,
                left, barWidth, ref y, scale, barHeight, progress.Seconds, showCounts: false);
        }

        y += line;
        Text(canvas, font, paint, width, $"Compiled {progress.Compiled} / Cached {progress.Stored} / Skipped {progress.Skipped}",
            left, ref y, scale, 200);
        Text(canvas, font, paint, width, string.Create(CultureInfo.InvariantCulture,
            $"{progress.Workers} workers / {progress.Seconds:F0} seconds"), left, ref y, scale, 160);
    }

    private static void Row(SKCanvas canvas, SKFont font, SKPaint paint, int width, string label, long done, long total,
        int left, int barWidth, ref int y, int scale, int barHeight, double seconds, bool showCounts = true)
    {
        var text = !showCounts ? label : total > 0
            ? $"{label}: {Math.Min(done, total)} / {total}"
            : $"{label}: none";
        Text(canvas, font, paint, width, text, left, ref y, scale, 230);
        Rectangle(canvas, paint, left, y, barWidth, barHeight, 80);
        if (total > 0)
        {
            var filled = (int)(barWidth * Math.Clamp(done / (double)total, 0, 1));
            Rectangle(canvas, paint, left, y, filled, barHeight, 255);
        }
        else if (!showCounts)
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

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SkiaSharp;

namespace SharpEmu.Libs.Overlay;

public sealed record OverlayPalette(
    SKColor Background,
    SKColor Menu,
    SKColor Card,
    SKColor Elevated,
    SKColor Raised,
    SKColor Text,
    SKColor Secondary,
    SKColor Muted,
    SKColor Faint,
    SKColor Accent,
    SKColor AccentForeground)
{
    public const string EnvironmentVariable = "SHARPEMU_UI_PALETTE";

    private static readonly Lazy<OverlayPalette> Configured =
        new(() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)) ?? Graphite);

    public static OverlayPalette Graphite { get; } = new(
        new SKColor(0x11, 0x11, 0x11),
        new SKColor(0x19, 0x19, 0x19),
        new SKColor(0x22, 0x22, 0x22),
        new SKColor(0x2A, 0x2A, 0x2A),
        new SKColor(0x31, 0x31, 0x31),
        new SKColor(0xF5, 0xF5, 0xF5),
        new SKColor(0xC8, 0xC8, 0xC8),
        new SKColor(0x98, 0x98, 0x98),
        new SKColor(0x60, 0x60, 0x60),
        new SKColor(0xF2, 0xF2, 0xF2),
        new SKColor(0x11, 0x11, 0x11));

    public static OverlayPalette Current => Configured.Value;

    public static OverlayPalette? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 11)
        {
            return null;
        }

        var colors = new SKColor[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!SKColor.TryParse(parts[index], out colors[index]))
            {
                return null;
            }

            colors[index] = colors[index].WithAlpha(0xFF);
        }

        return new OverlayPalette(colors[0], colors[1], colors[2], colors[3], colors[4], colors[5], colors[6],
            colors[7], colors[8], colors[9], colors[10]);
    }
}

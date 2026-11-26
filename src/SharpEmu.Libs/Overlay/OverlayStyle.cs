// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SkiaSharp;

namespace SharpEmu.Libs.Overlay;

public enum OverlayWeight
{
    Light,
    Regular,
    Bold,
}

public readonly record struct OverlayFont(float Size, OverlayWeight Weight);

public static class OverlayStyle
{
    public const float ReferenceHeight = 1080f;
    public const float Density = 0.84f;

    public static readonly OverlayFont Display = new(38, OverlayWeight.Light);
    public static readonly OverlayFont Headline = new(31, OverlayWeight.Bold);
    public static readonly OverlayFont Title = new(21, OverlayWeight.Bold);
    public static readonly OverlayFont Body = new(18, OverlayWeight.Regular);
    public static readonly OverlayFont Detail = new(16, OverlayWeight.Regular);
    public static readonly OverlayFont Label = new(17, OverlayWeight.Regular);
    public static readonly OverlayFont Caption = new(15, OverlayWeight.Regular);
    public static readonly OverlayFont CaptionStrong = new(15, OverlayWeight.Bold);
    public static readonly OverlayFont Overline = new(14, OverlayWeight.Bold);
    public static readonly OverlayFont KeyCap = new(14, OverlayWeight.Bold);

    public const float Padding = 34;
    public const float Gap = 10;
    public const float CardRadius = 10;
    public const float ImageRadius = 6;
    public const float HeroImageRadius = 14;
    public const float ModalRadius = 16;
    public const float FocusRingWidth = 2.5f;
    public const float FocusGlow = 9;
    public const float HeaderHeight = 112;
    public const float FooterHeight = 70;
    public const float ListRowHeight = 112;
    public const float PromptSpacing = 26;
    public const float PromptGlyphRadius = 15;

    public static readonly SKColor Notice = new(0xF5, 0xC4, 0x6E);

    public static float UnitFor(float surfaceHeight) =>
        Math.Clamp(surfaceHeight / ReferenceHeight * Density, 0.7f, 3f);
}

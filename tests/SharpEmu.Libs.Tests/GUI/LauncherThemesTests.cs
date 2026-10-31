// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class LauncherThemesTests
{
    [Fact]
    public void Derive_KeepsADarkBackgroundAsPicked()
    {
        var palette = LauncherThemes.Derive("#0b1020", "#2E7CF6");

        Assert.Equal(LauncherThemes.CustomId, palette.Id);
        Assert.Equal("#0B1020", palette.Background);
    }

    [Fact]
    public void Derive_DarkensABrightBackgroundButKeepsItsHue()
    {
        var palette = LauncherThemes.Derive("#FF0000", "#2E7CF6");

        Assert.Equal("#400000", palette.Background);
    }

    [Theory]
    [InlineData("#F2F2F2", "#111111")]
    [InlineData("#FFD60A", "#111111")]
    [InlineData("#2E7CF6", "#FFFFFF")]
    [InlineData("#7C5CFC", "#FFFFFF")]
    [InlineData("#30C26B", "#111111")]
    public void Derive_PicksAReadableColourOnTheAccent(string accent, string foreground)
    {
        Assert.Equal(foreground, LauncherThemes.Derive("#121212", accent).AccentForeground);
    }

    [Fact]
    public void Derive_FallsBackToDefaultsForInvalidColours()
    {
        var palette = LauncherThemes.Derive("not a colour", "#12345");

        Assert.Equal(LauncherThemes.DefaultCustomBackground, palette.Background);
        Assert.Equal(LauncherThemes.DefaultCustomAccent, palette.Accent);
    }

    [Theory]
    [InlineData("#1a2b3c", true)]
    [InlineData("1A2B3C", true)]
    [InlineData("#1A2B3", false)]
    [InlineData("#1A2B3CFF", false)]
    [InlineData("#GG0000", false)]
    [InlineData(null, false)]
    public void TryParseHex_AcceptsOnlySixDigitColours(string? text, bool valid)
    {
        Assert.Equal(valid, LauncherThemes.TryParseHex(text, out _));
    }

    [Fact]
    public void AdjustContrast_AtZeroReturnsTheThemeAsDesigned()
    {
        var graphite = LauncherThemes.Find("Graphite");

        Assert.Same(graphite, LauncherThemes.AdjustContrast(graphite, 0));
    }

    [Fact]
    public void AdjustContrast_PositiveSeparatesSurfacesAndSharpensSecondaryText()
    {
        var graphite = LauncherThemes.Find("Graphite");

        var stronger = LauncherThemes.AdjustContrast(graphite, 50);
        var softer = LauncherThemes.AdjustContrast(graphite, -50);

        // Graphite is neutral grey, so the red channel stands for the whole colour.
        static int Red(string hex) => Convert.ToInt32(hex.Substring(1, 2), 16);
        Assert.True(Red(stronger.Card) > Red(graphite.Card));
        Assert.True(Red(softer.Card) < Red(graphite.Card));
        Assert.True(Red(stronger.SecondaryText) > Red(graphite.SecondaryText));
        Assert.True(Red(softer.SecondaryText) < Red(graphite.SecondaryText));
        Assert.Equal(graphite.Background, stronger.Background);
        Assert.Equal(graphite.Text, stronger.Text);
    }

    [Fact]
    public void Find_FallsBackToGraphiteForUnknownIds()
    {
        Assert.Equal(LauncherThemes.DefaultId, LauncherThemes.Find("Nope").Id);
    }
}

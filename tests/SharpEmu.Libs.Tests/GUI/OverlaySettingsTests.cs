// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class OverlaySettingsTests
{
    [Fact]
    public void MissingSettingsUseTitleBarOverlay()
    {
        var settings = EffectiveLaunchSettings.Resolve(PerGameSettings.NormalizeFromJson("{}"));
        Assert.True(settings.OverlayEnabled);
        Assert.Equal("TitleBar", settings.OverlayMode);
        Assert.Equal("TopRight", settings.OverlayCorner);
    }

    [Fact]
    public void LegacyGlobalSettingsNormalizeChoicesAndRoundTrip()
    {
        var settings = SettingsSplitMigration.CarriedValues("""
            {"OverlayEnabled":false,"OverlayMode":"full","OverlayCorner":"bottomleft"}
            """);
        settings = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(settings))!;
        Assert.False(settings.OverlayEnabled);
        Assert.Equal("Full", settings.OverlayMode);
        Assert.Equal("BottomLeft", settings.OverlayCorner);
    }

    [Fact]
    public void InvalidChoicesUseDefaults()
    {
        var settings = SettingsSplitMigration.CarriedValues("""
            {"OverlayMode":null,"OverlayCorner":"somewhere"}
            """);
        Assert.Null(settings.OverlayMode);
        Assert.Null(settings.OverlayCorner);
    }

    [Fact]
    public void GameOptionsUseDefaultsUnlessSet()
    {
        var defaults = EffectiveLaunchSettings.Resolve(new PerGameSettings());
        Assert.True(defaults.OverlayEnabled);
        Assert.Equal("TitleBar", defaults.OverlayMode);
        Assert.Equal("TopRight", defaults.OverlayCorner);

        var overrides = PerGameSettings.NormalizeFromJson("""
            {"OverlayEnabled":false,"OverlayMode":"TitleBar","OverlayCorner":"TopLeft"}
            """);
        Assert.NotNull(overrides);
        overrides.RemoveDefaultValues();
        var effective = EffectiveLaunchSettings.Resolve(overrides);
        Assert.False(effective.OverlayEnabled);
        Assert.Equal("TitleBar", effective.OverlayMode);
        Assert.Equal("TopLeft", effective.OverlayCorner);
        Assert.Null(overrides.OverlayMode);
        Assert.False(overrides.IsEmpty);
    }

    [Fact]
    public void GameOptionsMatchingDefaultsDoNotLeaveASetting()
    {
        var settings = new PerGameSettings { OverlayEnabled = true, OverlayMode = "titlebar", OverlayCorner = "TopRight" };
        settings.RemoveDefaultValues();
        Assert.True(settings.IsEmpty);
    }
}

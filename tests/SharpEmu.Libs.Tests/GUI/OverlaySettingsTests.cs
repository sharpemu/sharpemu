// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class OverlaySettingsTests
{
    [Fact]
    public void MissingSettingsKeepTheExistingFullTopRightOverlay()
    {
        var settings = GuiSettings.NormalizeFromJson("{}");
        Assert.True(settings.OverlayEnabled);
        Assert.Equal("Full", settings.OverlayMode);
        Assert.Equal("TopRight", settings.OverlayCorner);
    }

    [Fact]
    public void GlobalSettingsRoundTripAndNormalizeChoices()
    {
        var settings = GuiSettings.NormalizeFromJson("""
            {"OverlayEnabled":false,"OverlayMode":"titlebar","OverlayCorner":"bottomleft"}
            """);
        settings = GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(settings));
        Assert.False(settings.OverlayEnabled);
        Assert.Equal("TitleBar", settings.OverlayMode);
        Assert.Equal("BottomLeft", settings.OverlayCorner);
    }

    [Fact]
    public void InvalidChoicesUseDefaults()
    {
        var settings = GuiSettings.NormalizeFromJson("""
            {"OverlayMode":null,"OverlayCorner":"somewhere"}
            """);
        Assert.Equal("Full", settings.OverlayMode);
        Assert.Equal("TopRight", settings.OverlayCorner);
    }

    [Fact]
    public void GameOptionsInheritUnlessOverridden()
    {
        var global = new GuiSettings { OverlayEnabled = false, OverlayMode = "Minimal", OverlayCorner = "BottomRight" };
        var inherited = EffectiveLaunchSettings.Resolve(global, new PerGameSettings());
        Assert.False(inherited.OverlayEnabled);
        Assert.Equal("Minimal", inherited.OverlayMode);
        Assert.Equal("BottomRight", inherited.OverlayCorner);

        var overrides = PerGameSettings.NormalizeFromJson("""
            {"OverlayEnabled":true,"OverlayMode":"TitleBar","OverlayCorner":"TopLeft"}
            """);
        Assert.NotNull(overrides);
        overrides.RemoveInheritedValues(global);
        var effective = EffectiveLaunchSettings.Resolve(global, overrides);
        Assert.True(effective.OverlayEnabled);
        Assert.Equal("TitleBar", effective.OverlayMode);
        Assert.Equal("TopLeft", effective.OverlayCorner);
        Assert.False(overrides.IsEmpty);
    }

    [Fact]
    public void MatchingGameOptionsDoNotLeaveAnOverride()
    {
        var global = new GuiSettings { OverlayEnabled = false, OverlayMode = "Minimal", OverlayCorner = "BottomLeft" };
        var settings = new PerGameSettings { OverlayEnabled = false, OverlayMode = "Minimal", OverlayCorner = "BottomLeft" };
        settings.RemoveInheritedValues(global);
        Assert.True(settings.IsEmpty);
    }
}

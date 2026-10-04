// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class RayTracingSkipSettingsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("SHARPEMU_SKIP_RT", true)]
    [InlineData("SHARPEMU_SKIP_RT=1", true)]
    [InlineData("SHARPEMU_SKIP_RT=0", false)]
    [InlineData(" sharpemu_skip_rt = 1 ", true)]
    [InlineData("SHARPEMU_SKIP_RT=invalid", false)]
    public void SavedEntryControlsDisplayAndLaunch(string? entry, bool enabled)
    {
        var settings = new GuiSettings();
        if (entry is not null) settings.EnvironmentToggles.Add(entry);
        var restored = GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(settings));
        var effective = EffectiveLaunchSettings.Resolve(restored, new PerGameSettings());
        Assert.Equal(enabled, RayTracingSkipSettings.IsEnabled(effective.EnvironmentToggles));
        Assert.Equal(enabled ? "1" : "0", RayTracingSkipSettings.GetLaunchValue(effective.EnvironmentToggles));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GameOverrideSurvivesReloadWithoutChangingGlobalChoice(bool globalEnabled, bool gameEnabled)
    {
        var global = new GuiSettings();
        global.EnvironmentToggles.AddRange(["sharpemu_skip_rt=1", "SHARPEMU_SKIP_RT=0"]);
        Assert.False(RayTracingSkipSettings.IsEnabled(global.EnvironmentToggles));
        RayTracingSkipSettings.SetEnabled(global.EnvironmentToggles, globalEnabled);
        var game = new PerGameSettings { EnvironmentToggles = [.. global.EnvironmentToggles] };
        RayTracingSkipSettings.SetEnabled(game.EnvironmentToggles, gameEnabled);
        game.RemoveInheritedValues(global);
        Assert.Equal(globalEnabled == gameEnabled, game.IsEmpty);
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(global, restored);
        Assert.Equal(gameEnabled ? "1" : "0", RayTracingSkipSettings.GetLaunchValue(effective.EnvironmentToggles));
        Assert.Equal(globalEnabled, RayTracingSkipSettings.IsEnabled(global.EnvironmentToggles));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
        Assert.DoesNotContain("sharpemu_skip_rt=1", global.EnvironmentToggles);
        Assert.DoesNotContain("SHARPEMU_SKIP_RT=0", global.EnvironmentToggles);
    }
}

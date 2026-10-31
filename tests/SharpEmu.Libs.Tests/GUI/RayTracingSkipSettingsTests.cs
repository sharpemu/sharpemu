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
        var settings = new PerGameSettings { EnvironmentToggles = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles] };
        if (entry is not null) settings.EnvironmentToggles.Add(entry);
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(settings));
        var effective = EffectiveLaunchSettings.Resolve(restored);
        Assert.Equal(enabled, RayTracingSkipSettings.IsEnabled(effective.EnvironmentToggles));
        Assert.Equal(enabled ? "1" : "0", RayTracingSkipSettings.GetLaunchValue(effective.EnvironmentToggles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameChoiceSurvivesReloadAndReplacesPreviousEntries(bool gameEnabled)
    {
        var game = new PerGameSettings { EnvironmentToggles = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles] };
        game.EnvironmentToggles.AddRange(["sharpemu_skip_rt=1", "SHARPEMU_SKIP_RT=0"]);
        Assert.False(RayTracingSkipSettings.IsEnabled(game.EnvironmentToggles));
        RayTracingSkipSettings.SetEnabled(game.EnvironmentToggles, gameEnabled);
        Assert.DoesNotContain("sharpemu_skip_rt=1", game.EnvironmentToggles);
        Assert.DoesNotContain("SHARPEMU_SKIP_RT=0", game.EnvironmentToggles);
        game.RemoveDefaultValues();
        Assert.Equal(!gameEnabled, game.IsEmpty);
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(restored);
        Assert.Equal(gameEnabled ? "1" : "0", RayTracingSkipSettings.GetLaunchValue(effective.EnvironmentToggles));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class StrictComputeSettingsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"EnvironmentToggles\":[]}")]
    [InlineData("{\"EnvironmentToggles\":[\"SHARPEMU_PROFILE_PERFORMANCE\"]}")]
    public void NewAndExistingSettingsUseStrictModeByDefault(string json)
    {
        var settings = GuiSettings.NormalizeFromJson(json);
        var effective = EffectiveLaunchSettings.Resolve(settings, new PerGameSettings());
        Assert.True(StrictComputeSettings.IsEnabled(effective.EnvironmentToggles));
        Assert.Equal("1", StrictComputeSettings.GetLaunchValue(effective.EnvironmentToggles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalChoiceSurvivesReloadAndOverridesPreviousEntries(bool enabled)
    {
        var settings = new GuiSettings();
        settings.EnvironmentToggles.AddRange([StrictComputeSettings.VariableName, "SHARPEMU_STRICT_COMPUTE=0"]);
        StrictComputeSettings.SetEnabled(settings.EnvironmentToggles, enabled);
        var restored = GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(settings));
        var effective = EffectiveLaunchSettings.Resolve(restored, new PerGameSettings());
        Assert.Equal(enabled, StrictComputeSettings.IsEnabled(effective.EnvironmentToggles));
        Assert.Equal(enabled ? "1" : "0", StrictComputeSettings.GetLaunchValue(effective.EnvironmentToggles));
        Assert.Single(restored.EnvironmentToggles, entry => entry.StartsWith(StrictComputeSettings.VariableName));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", restored.EnvironmentToggles);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GameOverrideSurvivesReloadWithoutChangingGlobalChoice(bool globalEnabled, bool gameEnabled)
    {
        var global = new GuiSettings();
        StrictComputeSettings.SetEnabled(global.EnvironmentToggles, globalEnabled);
        var game = new PerGameSettings { EnvironmentToggles = [.. global.EnvironmentToggles] };
        StrictComputeSettings.SetEnabled(game.EnvironmentToggles, gameEnabled);
        game.RemoveInheritedValues(global);
        Assert.Equal(globalEnabled == gameEnabled, game.IsEmpty);
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(global, restored);
        Assert.Equal(gameEnabled ? "1" : "0", StrictComputeSettings.GetLaunchValue(effective.EnvironmentToggles));
        Assert.Equal(globalEnabled, StrictComputeSettings.IsEnabled(global.EnvironmentToggles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitGameChoiceIsComparedWithTheEnabledDefault(bool gameEnabled)
    {
        var global = new GuiSettings();
        var game = new PerGameSettings { EnvironmentToggles = [.. global.EnvironmentToggles] };
        StrictComputeSettings.SetEnabled(game.EnvironmentToggles, gameEnabled);
        game.RemoveInheritedValues(global);
        Assert.Equal(gameEnabled, game.IsEmpty);
        Assert.Equal(gameEnabled, StrictComputeSettings.IsEnabled(
            EffectiveLaunchSettings.Resolve(global, game).EnvironmentToggles));
    }
}

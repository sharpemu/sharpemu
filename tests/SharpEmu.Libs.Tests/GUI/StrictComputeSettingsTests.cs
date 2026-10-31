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
    [InlineData("{\"EnvironmentChanges\":[]}")]
    [InlineData("{\"EnvironmentChanges\":[\"SHARPEMU_PROFILE_PERFORMANCE\"]}")]
    public void NewAndExistingGamesUseStrictModeByDefault(string json)
    {
        var effective = EffectiveLaunchSettings.Resolve(PerGameSettings.NormalizeFromJson(json));
        Assert.True(StrictComputeSettings.IsEnabled(effective.EnvironmentToggles));
        Assert.Equal("1", StrictComputeSettings.GetLaunchValue(effective.EnvironmentToggles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameChoiceSurvivesReloadAndOverridesPreviousEntries(bool enabled)
    {
        var settings = new PerGameSettings { EnvironmentToggles = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles] };
        settings.EnvironmentToggles.AddRange([StrictComputeSettings.VariableName, "SHARPEMU_STRICT_COMPUTE=0"]);
        StrictComputeSettings.SetEnabled(settings.EnvironmentToggles, enabled);
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(settings))!;
        var effective = EffectiveLaunchSettings.Resolve(restored);
        Assert.Equal(enabled, StrictComputeSettings.IsEnabled(effective.EnvironmentToggles));
        Assert.Equal(enabled ? "1" : "0", StrictComputeSettings.GetLaunchValue(effective.EnvironmentToggles));
        Assert.Single(restored.EnvironmentToggles!, entry => entry.StartsWith(StrictComputeSettings.VariableName));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LegacyGlobalChoiceMovesToGamesWithoutTheirOwn(bool globalEnabled, bool? gameEnabled)
    {
        List<string> global = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles];
        StrictComputeSettings.SetEnabled(global, globalEnabled);
        var game = new PerGameSettings
        {
            EnvironmentChanges = gameEnabled is { } own
                ? [own ? StrictComputeSettings.VariableName : StrictComputeSettings.VariableName + "=0"]
                : null,
        };

        SettingsSplitMigration.Fill(
            game,
            SettingsSplitMigration.CarriedValues(JsonSerializer.Serialize(new { EnvironmentToggles = global })));
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(restored);

        var expected = gameEnabled ?? globalEnabled;
        Assert.Equal(expected ? "1" : "0", StrictComputeSettings.GetLaunchValue(effective.EnvironmentToggles));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitGameChoiceIsComparedWithTheEnabledDefault(bool gameEnabled)
    {
        var game = new PerGameSettings { EnvironmentToggles = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles] };
        StrictComputeSettings.SetEnabled(game.EnvironmentToggles, gameEnabled);
        game.RemoveDefaultValues();
        Assert.Equal(gameEnabled, game.IsEmpty);
        Assert.Equal(gameEnabled, StrictComputeSettings.IsEnabled(
            EffectiveLaunchSettings.Resolve(game).EnvironmentToggles));
    }
}

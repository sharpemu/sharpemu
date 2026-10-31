// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class PerformanceProfileSettingsTests
{
    private const string PerformanceProfileVariable = "SHARPEMU_PROFILE_PERFORMANCE";
    private const string PerformanceFrameTraceVariable = "SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE";

    [Theory]
    [InlineData(PerformanceProfileVariable)]
    [InlineData(PerformanceFrameTraceVariable)]
    public void PerformanceProfilingIsDisabledByDefault(string variable)
    {
        var effective = EffectiveLaunchSettings.Resolve(PerGameSettings.NormalizeFromJson("{}"));

        Assert.DoesNotContain(variable, EffectiveLaunchSettings.Defaults.EnvironmentToggles);
        Assert.DoesNotContain(variable, effective.EnvironmentToggles);
    }

    [Theory]
    [InlineData(PerformanceProfileVariable, false)]
    [InlineData(PerformanceProfileVariable, true)]
    [InlineData(PerformanceFrameTraceVariable, false)]
    [InlineData(PerformanceFrameTraceVariable, true)]
    public void LegacyGlobalProfileChoiceMovesToGames(string variable, bool enabled)
    {
        List<string> toggles = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles];
        if (enabled)
            toggles.Add(variable);

        var game = new PerGameSettings();
        SettingsSplitMigration.Fill(
            game,
            SettingsSplitMigration.CarriedValues(JsonSerializer.Serialize(new { EnvironmentToggles = toggles })));
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(restored);

        Assert.Equal(enabled, effective.EnvironmentToggles.Contains(variable));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
        Assert.Equal(!enabled, game.IsEmpty);
    }

    [Theory]
    [InlineData(PerformanceProfileVariable, false)]
    [InlineData(PerformanceProfileVariable, true)]
    [InlineData(PerformanceFrameTraceVariable, false)]
    [InlineData(PerformanceFrameTraceVariable, true)]
    public void GameProfileChoiceSurvivesReload(string variable, bool gameEnabled)
    {
        var game = new PerGameSettings { EnvironmentToggles = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles] };
        if (gameEnabled)
            game.EnvironmentToggles.Add(variable);

        game.RemoveDefaultValues();
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(restored);

        Assert.Equal(gameEnabled, effective.EnvironmentToggles.Contains(variable));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
        Assert.Equal(!gameEnabled, game.IsEmpty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProfileAndFrameTraceChoicesRemainSeparate(bool profileEnabled, bool traceEnabled)
    {
        var settings = new PerGameSettings { EnvironmentChanges = [] };
        if (profileEnabled)
            settings.EnvironmentChanges.Add(PerformanceProfileVariable);
        if (traceEnabled)
            settings.EnvironmentChanges.Add(PerformanceFrameTraceVariable);

        var restored = EffectiveLaunchSettings.Resolve(
            PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(settings)));

        Assert.Equal(profileEnabled, restored.EnvironmentToggles.Contains(PerformanceProfileVariable));
        Assert.Equal(traceEnabled, restored.EnvironmentToggles.Contains(PerformanceFrameTraceVariable));
    }
}

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
        var settings = GuiSettings.NormalizeFromJson("{}");
        var effective = EffectiveLaunchSettings.Resolve(settings, new PerGameSettings());

        Assert.DoesNotContain(variable, settings.EnvironmentToggles);
        Assert.DoesNotContain(variable, effective.EnvironmentToggles);
    }

    [Theory]
    [InlineData(PerformanceProfileVariable, false)]
    [InlineData(PerformanceProfileVariable, true)]
    [InlineData(PerformanceFrameTraceVariable, false)]
    [InlineData(PerformanceFrameTraceVariable, true)]
    public void GlobalProfileChoiceSurvivesReloadAndIsInherited(string variable, bool enabled)
    {
        var settings = new GuiSettings();
        if (enabled)
            settings.EnvironmentToggles.Add(variable);

        var restored = GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(settings));
        var effective = EffectiveLaunchSettings.Resolve(restored, new PerGameSettings());

        Assert.Equal(enabled, restored.EnvironmentToggles.Contains(variable));
        Assert.Equal(enabled, effective.EnvironmentToggles.Contains(variable));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
    }

    [Theory]
    [InlineData(PerformanceProfileVariable, false, false)]
    [InlineData(PerformanceProfileVariable, false, true)]
    [InlineData(PerformanceProfileVariable, true, false)]
    [InlineData(PerformanceProfileVariable, true, true)]
    [InlineData(PerformanceFrameTraceVariable, false, false)]
    [InlineData(PerformanceFrameTraceVariable, false, true)]
    [InlineData(PerformanceFrameTraceVariable, true, false)]
    [InlineData(PerformanceFrameTraceVariable, true, true)]
    public void GameProfileChoiceSurvivesReloadWithoutChangingGlobalSettings(string variable, bool globalEnabled, bool gameEnabled)
    {
        var global = new GuiSettings();
        if (globalEnabled)
            global.EnvironmentToggles.Add(variable);
        var game = new PerGameSettings { EnvironmentToggles = ["SHARPEMU_WRITABLE_APP0"] };
        if (gameEnabled)
            game.EnvironmentToggles.Add(variable);

        game.RemoveInheritedValues(global);
        var restored = PerGameSettings.NormalizeFromJson(JsonSerializer.Serialize(game));
        var effective = EffectiveLaunchSettings.Resolve(global, restored);

        Assert.Equal(gameEnabled, effective.EnvironmentToggles.Contains(variable));
        Assert.Equal(globalEnabled, global.EnvironmentToggles.Contains(variable));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
        Assert.Equal(globalEnabled == gameEnabled, game.IsEmpty);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProfileAndFrameTraceChoicesRemainSeparate(bool profileEnabled, bool traceEnabled)
    {
        var settings = new GuiSettings();
        if (profileEnabled)
            settings.EnvironmentToggles.Add(PerformanceProfileVariable);
        if (traceEnabled)
            settings.EnvironmentToggles.Add(PerformanceFrameTraceVariable);

        var restored = GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(settings));

        Assert.Equal(profileEnabled, restored.EnvironmentToggles.Contains(PerformanceProfileVariable));
        Assert.Equal(traceEnabled, restored.EnvironmentToggles.Contains(PerformanceFrameTraceVariable));
    }
}

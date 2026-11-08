// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class EnvironmentSettingsTests
{
    private static EnvironmentSetting Setting(string id) => EnvironmentSettings.All.Single(s => s.Id == id);

    [Fact]
    public void EverySettingNamesAnEmulatorVariableOnce()
    {
        Assert.All(EnvironmentSettings.All, setting => Assert.StartsWith("SHARPEMU_", setting.Variable));
        Assert.Equal(EnvironmentSettings.All.Count, EnvironmentSettings.All.Select(s => s.Variable).Distinct().Count());
        Assert.Equal(EnvironmentSettings.All.Count, EnvironmentSettings.All.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void EveryChoiceSettingListsItsDefault()
    {
        foreach (var setting in EnvironmentSettings.All.Where(s => s.Kind == EnvironmentSettingKind.Choice))
        {
            Assert.Contains(setting.Choices!, choice => choice.Value == setting.DefaultChoice);
        }
    }

    [Fact]
    public void DefaultStateStoresNothing()
    {
        var entries = new List<string>();
        foreach (var setting in EnvironmentSettings.All)
        {
            if (setting.Kind == EnvironmentSettingKind.Toggle)
                EnvironmentSettings.SetEnabled(entries, setting, setting.DefaultEnabled);
            else
                EnvironmentSettings.SetChoice(entries, setting, setting.DefaultChoice);
        }

        Assert.Empty(entries);
    }

    [Fact]
    public void DefaultOnToggleStoresOnlyItsOffValue()
    {
        var entries = new List<string> { "SHARPEMU_WRITABLE_APP0" };
        var prewarm = Setting("ShaderPrewarm");
        var tessellation = Setting("Tessellation");

        EnvironmentSettings.SetEnabled(entries, prewarm, enabled: false);
        EnvironmentSettings.SetEnabled(entries, tessellation, enabled: false);

        Assert.Equal(["SHARPEMU_WRITABLE_APP0", "SHARPEMU_SHADER_PREWARM=0", "SHARPEMU_GNM_TESSELLATION=0"], entries);
        Assert.False(EnvironmentSettings.IsEnabled(entries, prewarm));
        Assert.False(EnvironmentSettings.IsEnabled(entries, tessellation));

        EnvironmentSettings.SetEnabled(entries, prewarm, enabled: true);
        Assert.Equal(["SHARPEMU_WRITABLE_APP0", "SHARPEMU_GNM_TESSELLATION=0"], entries);
        Assert.True(EnvironmentSettings.IsEnabled(entries, prewarm));
    }

    [Fact]
    public void DefaultOffToggleIsStoredAsBareNameAndReadBack()
    {
        var entries = new List<string>();
        var pacing = Setting("NoFlipPacing");

        Assert.False(EnvironmentSettings.IsEnabled(entries, pacing));
        EnvironmentSettings.SetEnabled(entries, pacing, enabled: true);

        Assert.Equal(["SHARPEMU_NO_FLIP_PACING"], entries);
        Assert.True(EnvironmentSettings.IsEnabled(entries, pacing));
        Assert.True(EnvironmentSettings.IsEnabled(["SHARPEMU_NO_FLIP_PACING=1"], pacing));
        Assert.False(EnvironmentSettings.IsEnabled(["SHARPEMU_NO_FLIP_PACING=0"], pacing));
    }

    [Fact]
    public void ChoiceStoresOnlyNonDefaultValues()
    {
        var entries = new List<string>();
        var latency = Setting("AudioLatency");

        Assert.Equal("60", EnvironmentSettings.GetChoice(entries, latency));
        EnvironmentSettings.SetChoice(entries, latency, "120");
        Assert.Equal(["SHARPEMU_AUDIO_LATENCY_MS=120"], entries);
        Assert.Equal("120", EnvironmentSettings.GetChoice(entries, latency));

        EnvironmentSettings.SetChoice(entries, latency, "60");
        Assert.Empty(entries);
    }

    [Fact]
    public void ManagedEntriesAreRecognizedWithOrWithoutValue()
    {
        Assert.True(EnvironmentSettings.IsManagedEntry("SHARPEMU_VK_PIPELINE_CACHE=0"));
        Assert.True(EnvironmentSettings.IsManagedEntry("sharpemu_no_flip_pacing"));
        Assert.False(EnvironmentSettings.IsManagedEntry("SHARPEMU_WRITABLE_APP0"));
    }

    [Fact]
    public void PerGameOverrideOfADefaultOnSettingSurvivesDefaultCleanup()
    {
        var perGame = new PerGameSettings
        {
            EnvironmentToggles = [.. EffectiveLaunchSettings.Defaults.EnvironmentToggles, "SHARPEMU_GNM_TESSELLATION=0"],
        };

        perGame.RemoveDefaultValues();

        Assert.NotNull(perGame.EnvironmentToggles);
        Assert.False(EnvironmentSettings.IsEnabled(
            EffectiveLaunchSettings.Resolve(perGame).EnvironmentToggles, Setting("Tessellation")));
    }

    [Fact]
    public void PerGameListMatchingDefaultsOnlyInSpellingIsDropped()
    {
        var perGame = new PerGameSettings
        {
            EnvironmentToggles = ["SHARPEMU_WRITABLE_APP0=1", "sharpemu_disable_import_loop_guard"],
        };

        perGame.RemoveDefaultValues();

        Assert.Null(perGame.EnvironmentToggles);
    }

    [Fact]
    public void LegacyDefaultProfileAndConsoleFieldsAreTolerated()
    {
        const string json =
            """{ "DefaultProfile": "Player", "ConsoleType": "PS5", "RenderResolutionScale": 0.5, "EnvironmentToggles": ["SHARPEMU_DEFAULT_PROFILE=Player", "SHARPEMU_WRITABLE_APP0"] }""";

        GuiSettings.NormalizeFromJson(json);

        Assert.Equal(["SHARPEMU_WRITABLE_APP0"], SettingsSplitMigration.CarriedValues(json).EnvironmentToggles);
    }
}

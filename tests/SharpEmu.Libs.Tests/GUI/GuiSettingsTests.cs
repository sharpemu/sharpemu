// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GuiSettingsTests
{
    [Fact]
    public void ConsoleGeometrySurvivesSettingsSerialization()
    {
        var original = new GuiSettings
        {
            EmbeddedConsoleHeight = 360,
            ConsoleWindowWidth = 1100,
            ConsoleWindowHeight = 740,
            ConsoleWindowLeft = -1800,
            ConsoleWindowTop = 120,
            ConsoleWindowMaximized = true
        };
        var restored = GuiSettings.NormalizeFromJson(System.Text.Json.JsonSerializer.Serialize(original));
        Assert.Equal(original.EmbeddedConsoleHeight, restored.EmbeddedConsoleHeight);
        Assert.Equal(original.ConsoleWindowWidth, restored.ConsoleWindowWidth);
        Assert.Equal(original.ConsoleWindowHeight, restored.ConsoleWindowHeight);
        Assert.Equal(original.ConsoleWindowLeft, restored.ConsoleWindowLeft);
        Assert.Equal(original.ConsoleWindowTop, restored.ConsoleWindowTop);
        Assert.True(restored.ConsoleWindowMaximized);
    }

    [Fact]
    public void InvalidConsoleGeometryUsesDefaults()
    {
        var settings = GuiSettings.NormalizeFromJson("""
            { "EmbeddedConsoleHeight": -1, "ConsoleWindowWidth": 0,
              "ConsoleWindowHeight": 10, "ConsoleWindowLeft": 100 }
            """);
        Assert.Equal(240, settings.EmbeddedConsoleHeight);
        Assert.Equal(980, settings.ConsoleWindowWidth);
        Assert.Equal(620, settings.ConsoleWindowHeight);
        Assert.Null(settings.ConsoleWindowLeft);
        Assert.Null(settings.ConsoleWindowTop);
    }

    [Fact]
    public void NormalizeFromJson_AllPropertiesNull_FallsBackToDefaults()
    {
        const string json = """
            {
              "LogLevel": null,
              "GameFolders": null,
              "ExcludedGames": null,
              "EnvironmentToggles": null,
              "Language": null,
              "DiscordClientId": null
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Info", settings.LogLevel);
        Assert.Equal("en", settings.Language);
        Assert.Equal("1525606762248540221", settings.DiscordClientId);
        Assert.Empty(settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Empty(settings.EnvironmentToggles);
        Assert.Equal("Windowed", settings.WindowMode);
        Assert.Equal("1920x1080", settings.Resolution);
        Assert.Equal("Fit", settings.ScalingMode);
        Assert.Equal("Auto", settings.HdrMode);
        Assert.True(settings.VSync);
    }

    [Fact]
    public void NormalizeFromJson_InvalidVideoValues_FallBackAndClamp()
    {
        const string json = """
            {
              "WindowMode": "not-a-mode",
              "Resolution": "not-a-resolution",
              "ScalingMode": "nearest-ish",
              "HdrMode": "maybe",
              "DisplayIndex": -4,
              "RefreshRate": 5000
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Windowed", settings.WindowMode);
        Assert.Equal("1920x1080", settings.Resolution);
        Assert.Equal("Fit", settings.ScalingMode);
        Assert.Equal("Auto", settings.HdrMode);
        Assert.Equal(0, settings.DisplayIndex);
        Assert.Equal(1000, settings.RefreshRate);
    }

    [Theory]
    [InlineData("""{ }""", "Carousel")]
    [InlineData("""{ "LibraryLayout": null }""", "Carousel")]
    [InlineData("""{ "LibraryLayout": "sideways" }""", "Carousel")]
    [InlineData("""{ "LibraryLayout": "grid" }""", "Grid")]
    [InlineData("""{ "LibraryLayout": "Grid" }""", "Grid")]
    public void NormalizeFromJson_LibraryLayout_FallsBackToCarousel(string json, string expected)
    {
        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal(expected, settings.LibraryLayout);
    }

    [Fact]
    public void NormalizeFromJson_CustomResolution_IsPreserved()
    {
        const string json = """{ "Resolution": "3440x1440" }""";

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("3440x1440", settings.Resolution);
    }

    [Fact]
    public void NormalizeFromJson_ValidValues_ArePreserved()
    {
        const string json = """
            {
              "LogLevel": "Debug",
              "GameFolders": ["C:\\Games"],
              "ExcludedGames": ["C:\\Games\\skip.bin"],
              "EnvironmentToggles": ["SHARPEMU_TRACE"],
              "Language": "pt-BR",
              "DiscordClientId": "999"
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Debug", settings.LogLevel);
        Assert.Equal("pt-BR", settings.Language);
        Assert.Equal("999", settings.DiscordClientId);
        Assert.Equal(["C:\\Games"], settings.GameFolders);
        Assert.Equal(["C:\\Games\\skip.bin"], settings.ExcludedGames);
        Assert.Equal(["SHARPEMU_TRACE"], settings.EnvironmentToggles);
    }

    [Fact]
    public void NormalizeFromJson_AllLauncherOptions_ArePreserved()
    {
        const string json = """
            {
              "LogLevel": "Debug",
              "ImportTraceLimit": 96,
              "StrictDynlibResolution": true,
              "LogToFile": true,
              "LogFilePath": "C:\\Logs\\sharpemu.log",
              "OverrideLogFile": true,
              "PlayTitleMusic": false,
              "EmulatorPath": "C:\\SharpEmu\\SharpEmu.exe",
              "Language": "ru",
              "DefaultProfile": "Player",
              "DiscordRichPresence": false,
              "CheckForUpdatesOnStartup": false,
              "WindowMode": "Borderless",
              "Resolution": "2560x1440",
              "DisplayIndex": 2,
              "RefreshRate": 144,
              "ScalingMode": "Integer",
              "VSync": false,
              "HdrMode": "On",
              "EnvironmentToggles": [
                "SHARPEMU_VK_VALIDATION",
                "SHARPEMU_DUMP_SPIRV"
              ],
              "DiscordClientId": "999"
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Debug", settings.LogLevel);
        Assert.Equal(96, settings.ImportTraceLimit);
        Assert.True(settings.StrictDynlibResolution);
        Assert.True(settings.LogToFile);
        Assert.Equal("C:\\Logs\\sharpemu.log", settings.LogFilePath);
        Assert.True(settings.OverrideLogFile);
        Assert.False(settings.PlayTitleMusic);
        Assert.Equal("C:\\SharpEmu\\SharpEmu.exe", settings.EmulatorPath);
        Assert.Equal("ru", settings.Language);
        Assert.Equal("Player", settings.DefaultProfile);
        Assert.False(settings.DiscordRichPresence);
        Assert.False(settings.CheckForUpdatesOnStartup);
        Assert.Equal("Borderless", settings.WindowMode);
        Assert.Equal("2560x1440", settings.Resolution);
        Assert.Equal(2, settings.DisplayIndex);
        Assert.Equal(144, settings.RefreshRate);
        Assert.Equal("Integer", settings.ScalingMode);
        Assert.False(settings.VSync);
        Assert.Equal("On", settings.HdrMode);
        Assert.Equal(
            ["SHARPEMU_VK_VALIDATION", "SHARPEMU_DUMP_SPIRV"],
            settings.EnvironmentToggles);
        Assert.Equal("999", settings.DiscordClientId);
    }

    // An empty Discord client ID intentionally disables Rich Presence.
    [Fact]
    public void NormalizeFromJson_EmptyDiscordClientId_IsPreservedNotNormalized()
    {
        const string json = """{ "DiscordClientId": "" }""";

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal(string.Empty, settings.DiscordClientId);
    }

    [Fact]
    public void NormalizeFromJson_NullOrEmptyListEntries_AreFilteredOut()
    {
        const string json = """
            {
              "GameFolders": ["C:\\Games", null, ""],
              "ExcludedGames": [null],
              "EnvironmentToggles": [null, "SHARPEMU_TRACE", ""]
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal(["C:\\Games"], settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Equal(["SHARPEMU_TRACE"], settings.EnvironmentToggles);
    }

    [Fact]
    public void NormalizeFromJson_EmptyObject_UsesConstructorDefaults()
    {
        var settings = GuiSettings.NormalizeFromJson("{}");

        Assert.Equal("Info", settings.LogLevel);
        Assert.Equal("en", settings.Language);
        Assert.Equal("1525606762248540221", settings.DiscordClientId);
        Assert.Empty(settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Empty(settings.EnvironmentToggles);
    }

    [Fact]
    public void EffectiveLaunchSettings_PerGameVideoValuesOverrideOnlySelectedFields()
    {
        var global = new GuiSettings
        {
            WindowMode = "Windowed",
            Resolution = "1920x1080",
            DisplayIndex = 1,
            RefreshRate = 60,
            ScalingMode = "Fit",
            HdrMode = "Auto",
            VSync = true,
        };
        var perGame = new PerGameSettings
        {
            Resolution = "2560x1440",
            DisplayIndex = 2,
            HdrMode = "On",
            VSync = false,
        };

        var effective = EffectiveLaunchSettings.Resolve(global, perGame);

        Assert.Equal("Windowed", effective.WindowMode);
        Assert.Equal("2560x1440", effective.Resolution);
        Assert.Equal(2, effective.DisplayIndex);
        Assert.Equal(60, effective.RefreshRate);
        Assert.Equal("Fit", effective.ScalingMode);
        Assert.Equal("On", effective.HdrMode);
        Assert.False(effective.VSync);
    }

    [Fact]
    public void PerGameSettings_VideoOverridesParticipateInEmptyCheck()
    {
        Assert.True(new PerGameSettings().IsEmpty);
        Assert.False(new PerGameSettings { ScalingMode = "Integer" }.IsEmpty);
        Assert.False(new PerGameSettings { HdrMode = "Off" }.IsEmpty);
    }
}

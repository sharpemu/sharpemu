// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class PerGameSettingsTests
{
    // Invalid entries must not reach Environment.SetEnvironmentVariable.
    [Fact]
    public void NormalizeFromJson_NullOrEmptyToggleEntries_AreFilteredOut()
    {
        const string json = """
            { "EnvironmentToggles": [null, "SHARPEMU_TRACE", ""] }
            """;

        var settings = PerGameSettings.NormalizeFromJson(json);

        Assert.NotNull(settings);
        Assert.Equal(["SHARPEMU_TRACE"], settings.EnvironmentToggles);
    }

    // A null list means that the global setting should be inherited.
    [Fact]
    public void NormalizeFromJson_NullToggleList_StaysNull()
    {
        const string json = """{ "EnvironmentToggles": null }""";

        var settings = PerGameSettings.NormalizeFromJson(json);

        Assert.NotNull(settings);
        Assert.Null(settings.EnvironmentToggles);
    }

    [Fact]
    public void NormalizeFromJson_EmptyToggleList_StaysEmpty()
    {
        const string json = """{ "EnvironmentToggles": [] }""";

        var settings = PerGameSettings.NormalizeFromJson(json);

        Assert.NotNull(settings);
        Assert.Empty(Assert.IsType<List<string>>(settings.EnvironmentToggles));
    }

    [Fact]
    public void NormalizeFromJson_ValidToggles_ArePreserved()
    {
        const string json = """
            { "EnvironmentToggles": ["SHARPEMU_TRACE", "SHARPEMU_NO_JIT"] }
            """;

        var settings = PerGameSettings.NormalizeFromJson(json);

        Assert.NotNull(settings);
        Assert.Equal(["SHARPEMU_TRACE", "SHARPEMU_NO_JIT"], settings.EnvironmentToggles);
    }

    [Fact]
    public void RemoveInheritedValues_AllMatchingValues_ProducesEmptySettings()
    {
        var global = new GuiSettings
        {
            LogLevel = "Info",
            ImportTraceLimit = 32,
            StrictDynlibResolution = true,
            LogToFile = false,
            WindowMode = "Borderless",
            Resolution = "2560x1440",
            DisplayIndex = 1,
            RefreshRate = 144,
            ScalingMode = "Fit",
            VSync = true,
            HdrMode = "Auto",
            EnvironmentToggles = ["SHARPEMU_LOG_IO", "SHARPEMU_VK_VALIDATION"],
        };
        var perGame = new PerGameSettings
        {
            LogLevel = "info",
            ImportTraceLimit = 32,
            StrictDynlibResolution = true,
            LogToFile = false,
            WindowMode = "borderless",
            Resolution = "2560x1440",
            DisplayIndex = 1,
            RefreshRate = 144,
            ScalingMode = "fit",
            VSync = true,
            HdrMode = "auto",
            EnvironmentToggles = ["SHARPEMU_VK_VALIDATION=1", "sharpemu_log_io"],
        };

        perGame.RemoveInheritedValues(global);

        Assert.True(perGame.IsEmpty);
    }

    [Fact]
    public void RemoveInheritedValues_DifferentValues_RemainOverrides()
    {
        var global = new GuiSettings
        {
            LogLevel = "Info",
            Resolution = "1920x1080",
            VSync = true,
            EnvironmentToggles = ["SHARPEMU_LOG_IO"],
        };
        var perGame = new PerGameSettings
        {
            LogLevel = "Debug",
            Resolution = "2560x1440",
            VSync = false,
            EnvironmentToggles = ["SHARPEMU_VK_VALIDATION"],
        };

        perGame.RemoveInheritedValues(global);

        Assert.Equal("Debug", perGame.LogLevel);
        Assert.Equal("2560x1440", perGame.Resolution);
        Assert.False(perGame.VSync);
        Assert.Equal(["SHARPEMU_VK_VALIDATION"], perGame.EnvironmentToggles);
    }

    [Fact]
    public void RemoveInheritedValues_DisabledEnvironmentEntry_MatchesMissingEntry()
    {
        var global = new GuiSettings
        {
            EnvironmentToggles = ["SHARPEMU_LOG_IO=0"],
        };
        var perGame = new PerGameSettings
        {
            EnvironmentToggles = [],
        };

        perGame.RemoveInheritedValues(global);

        Assert.Null(perGame.EnvironmentToggles);
    }
}

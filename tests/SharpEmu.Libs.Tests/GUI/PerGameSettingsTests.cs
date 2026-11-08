// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class PerGameSettingsTests
{
    [Fact]
    public void NewGameGetsWritableApp0AndNoCrashCapture()
    {
        var effective = EffectiveLaunchSettings.Resolve(new PerGameSettings());
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
        Assert.DoesNotContain("SHARPEMU_CRASH_CAPTURE", effective.EnvironmentToggles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameCrashDumpSettingSurvivesReload(bool enabled)
    {
        var perGame = new PerGameSettings { EnvironmentToggles = ["SHARPEMU_WRITABLE_APP0"] };
        if (enabled)
            perGame.EnvironmentToggles.Add("SHARPEMU_CRASH_CAPTURE");
        perGame.RemoveDefaultValues();
        var restored = PerGameSettings.NormalizeFromJson(System.Text.Json.JsonSerializer.Serialize(perGame));
        var effective = EffectiveLaunchSettings.Resolve(restored);
        Assert.Equal(enabled, effective.EnvironmentToggles.Contains("SHARPEMU_CRASH_CAPTURE"));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
    }

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

    // A null list means the defaults apply.
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
    public void RemoveDefaultValues_AllMatchingValues_ProducesEmptySettings()
    {
        var perGame = new PerGameSettings
        {
            LogLevel = "info",
            ImportTraceLimit = 0,
            StrictDynlibResolution = false,
            LogToFile = false,
            WindowMode = "windowed",
            Resolution = "1920x1080",
            DisplayIndex = 0,
            RefreshRate = 0,
            ScalingMode = "fit",
            VSync = true,
            HdrMode = "auto",
            GpuReadbacks = "full",
            OverlayEnabled = true,
            OverlayCorner = "topright",
            OverlayMode = "titlebar",
            BinkPlaybackMode = "guest",
            EnvironmentToggles = ["SHARPEMU_WRITABLE_APP0=1", "sharpemu_disable_import_loop_guard"],
            EnvironmentChanges = ["SHARPEMU_LOG_IO=0", "SHARPEMU_WRITABLE_APP0"],
        };

        perGame.RemoveDefaultValues();

        Assert.True(perGame.IsEmpty);
    }

    [Fact]
    public void RemoveDefaultValues_DifferentValues_Remain()
    {
        var perGame = new PerGameSettings
        {
            LogLevel = "Debug",
            Resolution = "2560x1440",
            VSync = false,
            BinkPlaybackMode = "Skip",
            EnvironmentToggles = ["SHARPEMU_VK_VALIDATION"],
        };

        perGame.RemoveDefaultValues();

        Assert.Equal("Debug", perGame.LogLevel);
        Assert.Equal("2560x1440", perGame.Resolution);
        Assert.False(perGame.VSync);
        Assert.Equal("Skip", perGame.BinkPlaybackMode);
        Assert.Equal(["SHARPEMU_VK_VALIDATION"], perGame.EnvironmentToggles);
    }

    [Fact]
    public void RemoveDefaultValues_DisabledEnvironmentEntry_MatchesMissingEntry()
    {
        var perGame = new PerGameSettings
        {
            EnvironmentToggles = ["SHARPEMU_WRITABLE_APP0", "SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", "SHARPEMU_LOG_IO=0"],
        };

        perGame.RemoveDefaultValues();

        Assert.Null(perGame.EnvironmentToggles);
    }

    [Fact]
    public void Resolve_UsesBuiltInDefaultsForEveryUnsetValue()
    {
        var defaults = EffectiveLaunchSettings.Defaults;

        Assert.Equal(defaults, EffectiveLaunchSettings.Resolve(null) with { EnvironmentToggles = defaults.EnvironmentToggles });
        Assert.Equal(defaults.EnvironmentToggles, EffectiveLaunchSettings.Resolve(null).EnvironmentToggles);
        Assert.Equal(defaults.EnvironmentToggles, EffectiveLaunchSettings.Resolve(new PerGameSettings()).EnvironmentToggles);
        Assert.Equal("Info", defaults.LogLevel);
        Assert.Equal("Windowed", defaults.WindowMode);
        Assert.Equal("1920x1080", defaults.Resolution);
        Assert.True(defaults.VSync);
        Assert.Equal("Full", defaults.GpuReadbacks);
        Assert.Equal("Guest", defaults.BinkPlaybackMode);

        var effective = EffectiveLaunchSettings.Resolve(new PerGameSettings
        {
            LogLevel = "Trace",
            ScalingMode = "Integer",
            BinkPlaybackMode = "Host",
            DisplayIndex = -3,
            RefreshRate = 9000,
        });
        Assert.Equal("Trace", effective.LogLevel);
        Assert.Equal("Integer", effective.ScalingMode);
        Assert.Equal("Host", effective.BinkPlaybackMode);
        Assert.Equal(0, effective.DisplayIndex);
        Assert.Equal(1000, effective.RefreshRate);
        Assert.Equal(defaults.WindowMode, effective.WindowMode);
        Assert.Equal(defaults.HdrMode, effective.HdrMode);
    }

    // Every game setting takes part in the empty check, the default cleanup and Resolve, so a
    // new one cannot be saved but then ignored, or kept as a redundant default.
    [Fact]
    public void EverySettingTakesPartInEmptyCheckCleanupAndResolve()
    {
        var properties = typeof(PerGameSettings).GetProperties()
            .Where(property => property.CanWrite && property.Name != nameof(PerGameSettings.CustomEnvironmentVariables))
            .ToList();
        var resolved = typeof(EffectiveLaunchSettings).GetProperties().Select(property => property.Name).ToHashSet();

        foreach (var property in properties)
        {
            var settings = new PerGameSettings();
            property.SetValue(settings, NonDefaultValue(property));
            Assert.False(settings.IsEmpty, property.Name);
            settings.RemoveDefaultValues();
            Assert.False(settings.IsEmpty, property.Name);

            if (property.Name != nameof(PerGameSettings.EnvironmentChanges))
            {
                Assert.Contains(property.Name, resolved);
                var value = typeof(EffectiveLaunchSettings).GetProperty(property.Name)!.GetValue(EffectiveLaunchSettings.Resolve(settings));
                var fallback = typeof(EffectiveLaunchSettings).GetProperty(property.Name)!.GetValue(EffectiveLaunchSettings.Defaults);
                Assert.NotEqual(System.Text.Json.JsonSerializer.Serialize(fallback), System.Text.Json.JsonSerializer.Serialize(value));
            }

            var defaultValue = typeof(EffectiveLaunchSettings).GetProperty(property.Name)?.GetValue(EffectiveLaunchSettings.Defaults);
            if (defaultValue is not null)
            {
                var matching = new PerGameSettings();
                property.SetValue(matching, defaultValue is IReadOnlyList<string> list ? list.ToList() : defaultValue);
                matching.RemoveDefaultValues();
                Assert.True(matching.IsEmpty, property.Name);
            }
        }

        Assert.True(new PerGameSettings { CustomEnvironmentVariables = ["A=1"] } is { IsEmpty: false });
    }

    private static object NonDefaultValue(System.Reflection.PropertyInfo property)
    {
        var type = property.PropertyType;
        return type == typeof(string) ? "Exclusive"
            : type == typeof(int?) ? 7
            : type == typeof(bool?) ? !(bool)typeof(EffectiveLaunchSettings).GetProperty(property.Name)!.GetValue(EffectiveLaunchSettings.Defaults)!
            : type == typeof(List<string>) ? new List<string> { "SHARPEMU_LOG_IO" }
            : throw new NotSupportedException(type.Name);
    }

    [Fact]
    public void EnvironmentChanges_OverrideOneVariableAndKeepDefaultsForTheRest()
    {
        var game = new PerGameSettings { EnvironmentChanges = ["SHARPEMU_CRASH_CAPTURE"] };

        var effective = EffectiveLaunchSettings.Resolve(game);

        Assert.Contains("SHARPEMU_CRASH_CAPTURE", effective.EnvironmentToggles);
        Assert.Contains("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
        Assert.Contains("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", effective.EnvironmentToggles);
    }

    [Fact]
    public void EnvironmentChanges_ExplicitOffBeatsDefaultOnAndNeverReachesTheEnvironment()
    {
        var game = new PerGameSettings { EnvironmentChanges = ["SHARPEMU_WRITABLE_APP0=0"] };

        var effective = EffectiveLaunchSettings.Resolve(game);

        Assert.DoesNotContain(effective.EnvironmentToggles, entry => entry.StartsWith("SHARPEMU_WRITABLE_APP0"));
    }

    [Fact]
    public void EnvironmentChanges_ManagedChoiceAtItsDefaultIsLeftUnset()
    {
        var game = new PerGameSettings { EnvironmentChanges = ["SHARPEMU_AUDIO_LATENCY_MS=60"] };

        var effective = EffectiveLaunchSettings.Resolve(game);

        // 60 ms is the emulator default, so the variable is left unset.
        Assert.DoesNotContain(effective.EnvironmentToggles, entry => entry.StartsWith("SHARPEMU_AUDIO_LATENCY_MS"));
    }

    [Fact]
    public void RemoveDefaultValues_DropsChangesThatMatchDefaults()
    {
        var game = new PerGameSettings { EnvironmentChanges = ["SHARPEMU_WRITABLE_APP0", "SHARPEMU_LOG_NP=0"] };

        game.RemoveDefaultValues();

        Assert.Null(game.EnvironmentChanges);
        Assert.True(game.IsEmpty);
    }

    [Fact]
    public void LegacyWholeListStillReplacesDefaults_AndConvertsToTheSameChanges()
    {
        var defaults = EffectiveLaunchSettings.Defaults.EnvironmentToggles;
        List<string> legacy = ["SHARPEMU_WRITABLE_APP0", "SHARPEMU_LOG_IO"];

        var fromLegacy = EffectiveLaunchSettings.Resolve(new PerGameSettings { EnvironmentToggles = legacy });
        var changes = EnvironmentOverrides.Between(defaults, legacy);
        var fromChanges = EffectiveLaunchSettings.Resolve(new PerGameSettings { EnvironmentChanges = changes });

        Assert.DoesNotContain("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", fromLegacy.EnvironmentToggles);
        Assert.Equal(
            fromLegacy.EnvironmentToggles.Select(EnvironmentOverrides.Name).Order(),
            fromChanges.EnvironmentToggles.Select(EnvironmentOverrides.Name).Order());
    }

    [Fact]
    public void PickChanged_KeepsStoredOverridesForUntouchedVariables()
    {
        List<string> baseline = ["SHARPEMU_WRITABLE_APP0", "SHARPEMU_LOG_IO"];
        List<string> current = ["SHARPEMU_WRITABLE_APP0", "SHARPEMU_LOG_IO", "SHARPEMU_LOG_NP"];

        var changes = EnvironmentOverrides.PickChanged(current, baseline, ["SHARPEMU_LOG_IO"]);

        Assert.Equal(["SHARPEMU_LOG_IO", "SHARPEMU_LOG_NP"], changes);
    }

    // Built-in defaults, then the settings for every game, then the game's own.
    [Fact]
    public void GameSettingsLayerOverTheSettingsForEveryGame()
    {
        var global = new PerGameSettings
        {
            VSync = true,
            OverlayMode = "Full",
            EnvironmentChanges = ["SHARPEMU_LOG_IO"],
        };
        var game = new PerGameSettings
        {
            OverlayMode = "Minimal",
            EnvironmentChanges = ["SHARPEMU_WRITABLE_APP0=0"],
        };

        var effective = EffectiveLaunchSettings.Resolve(global, game);

        Assert.True(effective.VSync);
        Assert.Equal("Minimal", effective.OverlayMode);
        Assert.Equal("Fit", effective.ScalingMode);
        Assert.Contains("SHARPEMU_LOG_IO", effective.EnvironmentToggles);
        Assert.DoesNotContain("SHARPEMU_WRITABLE_APP0", effective.EnvironmentToggles);
        var globalOnly = EffectiveLaunchSettings.Resolve(global, null);
        Assert.Equal("Full", globalOnly.OverlayMode);
        Assert.Equal(EffectiveLaunchSettings.Resolve(global).EnvironmentToggles, globalOnly.EnvironmentToggles);
    }

    // A game value equal to the one it would inherit is not its own, so it follows later
    // changes to the settings for every game.
    [Fact]
    public void ValuesEqualToTheInheritedOnesAreNotKept()
    {
        var inherited = EffectiveLaunchSettings.Over(
            EffectiveLaunchSettings.Defaults,
            new PerGameSettings { VSync = true, EnvironmentChanges = ["SHARPEMU_LOG_IO"] });
        var game = new PerGameSettings
        {
            VSync = true,
            OverlayMode = "Full",
            EnvironmentChanges = ["SHARPEMU_LOG_IO"],
        };

        game.RemoveInheritedValues(inherited);

        Assert.Null(game.VSync);
        Assert.Equal("Full", game.OverlayMode);
        Assert.True(game.EnvironmentChanges is null or { Count: 0 });
    }
}

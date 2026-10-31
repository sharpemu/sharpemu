// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class SettingsSplitMigrationTests : IDisposable
{
    // The shape of a real pre-split gui-settings.json, with a few game settings changed.
    private const string LegacyGlobal = """
        {
          "GameFolders": ["/Volumes/Games"],
          "ExcludedGames": [],
          "LogLevel": "Info",
          "ImportTraceLimit": 0,
          "StrictDynlibResolution": false,
          "LogToFile": true,
          "LogFilePath": null,
          "OverrideLogFile": false,
          "PlayTitleMusic": true,
          "TitleMusicVolume": 0,
          "LibraryLayout": "Carousel",
          "LibraryTileScale": 0.9,
          "Language": "en",
          "LauncherTheme": "Forest",
          "GlassOpacity": 30,
          "ThemeContrast": 50,
          "ConsoleLanguage": "EnglishUS",
          "WindowMode": "Windowed",
          "Resolution": "1920x1080",
          "DisplayIndex": 0,
          "RefreshRate": 0,
          "ScalingMode": "Fit",
          "VSync": false,
          "HdrMode": "Auto",
          "GpuReadbacks": "Off",
          "OverlayEnabled": true,
          "OverlayCorner": "BottomRight",
          "OverlayMode": "Full",
          "EnvironmentToggles": [
            "SHARPEMU_WRITABLE_APP0",
            "SHARPEMU_DISABLE_IMPORT_LOOP_GUARD",
            "SHARPEMU_CRASH_CAPTURE"
          ],
          "BinkPlaybackMode": "Host",
          "DiscordClientId": "1525606762248540221"
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharpemu-split-{Guid.NewGuid():N}");

    public SettingsSplitMigrationTests() => Directory.CreateDirectory(_root);

    private string SettingsPath => Path.Combine(_root, "gui-settings.json");

    private string SharedPath => Path.Combine(_root, "user", "game_defaults.json");

    private bool Migrate() => SettingsSplitMigration.Migrate(SettingsPath, SharedPath);

    private PerGameSettings Shared() =>
        PerGameSettings.NormalizeFromJson(File.ReadAllText(SharedPath))!;

    private void WriteShared(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SharedPath)!);
        File.WriteAllText(SharedPath, json);
    }

    [Fact]
    public void GlobalFileNoLongerSerializesGameSettings()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new GuiSettings()));
        var keys = json.RootElement.EnumerateObject().Select(property => property.Name).ToList();

        Assert.Empty(keys.Intersect(SettingsSplitMigration.LegacyGameKeys));
        Assert.Contains(nameof(GuiSettings.LauncherTheme), keys);
        Assert.Contains(nameof(GuiSettings.ConsoleLanguage), keys);
        Assert.Contains(nameof(GuiSettings.LogFilePath), keys);
        foreach (var key in SettingsSplitMigration.LegacyGameKeys)
        {
            Assert.Null(typeof(GuiSettings).GetProperty(key));
            Assert.NotNull(typeof(PerGameSettings).GetProperty(key));
        }
    }

    [Fact]
    public void UnmigratedGameSettingsSurviveAGlobalSave()
    {
        var settings = GuiSettings.NormalizeFromJson(LegacyGlobal);
        settings.LauncherTheme = "Graphite";

        var saved = JsonNode.Parse(JsonSerializer.Serialize(settings))!.AsObject();

        Assert.Equal("Graphite", (string?)saved["LauncherTheme"]);
        Assert.False((bool)saved["VSync"]!);
        Assert.Equal("Full", (string?)saved["OverlayMode"]);
    }

    [Fact]
    public void MovesChangedValuesToTheSharedFileAndCleansTheLauncherFile()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);

        Assert.True(Migrate());

        Assert.Equal(LegacyGlobal, File.ReadAllText(SettingsPath + SettingsSplitMigration.BackupSuffix));
        var global = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Empty(global.Select(pair => pair.Key).Intersect(SettingsSplitMigration.LegacyGameKeys));
        Assert.Equal("Forest", (string?)global["LauncherTheme"]);
        Assert.Equal("/Volumes/Games", (string?)global["GameFolders"]![0]);
        Assert.Equal(0.9, (double)global["LibraryTileScale"]!);

        var shared = Shared();
        Assert.Equal("BottomRight", shared.OverlayCorner);
        Assert.Equal("Full", shared.OverlayMode);
        Assert.False(shared.VSync);
        Assert.True(shared.LogToFile);
        Assert.Equal("Off", shared.GpuReadbacks);
        Assert.Equal("Host", shared.BinkPlaybackMode);
        Assert.Contains("SHARPEMU_CRASH_CAPTURE", EffectiveLaunchSettings.Resolve(shared).EnvironmentToggles);
        // Values the old file held at their defaults are not written out.
        Assert.Null(shared.LogLevel);
        Assert.Null(shared.WindowMode);
        Assert.Null(shared.Resolution);
        Assert.Null(shared.OverlayEnabled);
        Assert.Null(shared.EnvironmentToggles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "VSync": true, "Resolution": "2560x1440", "EnvironmentChanges": ["SHARPEMU_LOG_NP"] }""")]
    [InlineData("""{ "OverlayCorner": "TopLeft", "EnvironmentChanges": ["SHARPEMU_CRASH_CAPTURE=0", "SHARPEMU_LOG_IO"] }""")]
    [InlineData("""{ "EnvironmentToggles": ["SHARPEMU_WRITABLE_APP0"] }""")]
    public void EveryGameKeepsTheEffectiveSettingsItHadBefore(string? gameJson)
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        var game = gameJson is null ? null : PerGameSettings.NormalizeFromJson(gameJson);

        Migrate();

        var legacy = SettingsSplitMigration.CarriedValues(LegacyGlobal);
        var after = EffectiveLaunchSettings.Resolve(Shared(), game);
        // The pre-split rule: a game's value, else the global one, with its environment
        // changes laid over its own whole list or the global list.
        Assert.Equal(game?.VSync ?? false, after.VSync);
        Assert.Equal(game?.Resolution ?? "1920x1080", after.Resolution);
        Assert.Equal(game?.OverlayCorner ?? "BottomRight", after.OverlayCorner);
        Assert.Equal("Host", after.BinkPlaybackMode);
        Assert.Equal(
            EnvironmentOverrides.Merge(game?.EnvironmentToggles ?? legacy.EnvironmentToggles!, game?.EnvironmentChanges)
                .Select(EnvironmentOverrides.Name).Order(StringComparer.OrdinalIgnoreCase),
            after.EnvironmentToggles.Select(EnvironmentOverrides.Name).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void SecondRunChangesNothing()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        Assert.True(Migrate());
        var global = File.ReadAllBytes(SettingsPath);
        var shared = File.ReadAllBytes(SharedPath);
        var backup = File.ReadAllBytes(SettingsPath + SettingsSplitMigration.BackupSuffix);

        Assert.False(Migrate());

        Assert.Equal(global, File.ReadAllBytes(SettingsPath));
        Assert.Equal(shared, File.ReadAllBytes(SharedPath));
        Assert.Equal(backup, File.ReadAllBytes(SettingsPath + SettingsSplitMigration.BackupSuffix));
    }

    [Fact]
    public void SharedValuesAlreadySetAreKept()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        WriteShared("""{ "OverlayMode": "Minimal" }""");

        Assert.True(Migrate());

        Assert.Equal("Minimal", Shared().OverlayMode);
        Assert.Equal("BottomRight", Shared().OverlayCorner);
    }

    [Fact]
    public void FailedRunKeepsTheLauncherKeysAndTheNextRunFinishes()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        // A directory where the shared file goes makes its write fail.
        Directory.CreateDirectory(SharedPath);

        Assert.ThrowsAny<IOException>(() => Migrate());
        Assert.Equal(LegacyGlobal, File.ReadAllText(SettingsPath));
        Assert.Equal(LegacyGlobal, File.ReadAllText(SettingsPath + SettingsSplitMigration.BackupSuffix));

        Directory.Delete(SharedPath);
        Assert.True(Migrate());

        Assert.Equal("Full", Shared().OverlayMode);
        Assert.DoesNotContain("OverlayMode", File.ReadAllText(SettingsPath));
        Assert.Equal(LegacyGlobal, File.ReadAllText(SettingsPath + SettingsSplitMigration.BackupSuffix));
    }

    [Fact]
    public void DefaultValuedGlobalOnlyLosesItsKeys()
    {
        var json = JsonSerializer.Serialize(new
        {
            LauncherTheme = "Midnight",
            LogLevel = "info",
            VSync = true,
            OverlayMode = "TitleBar",
            EnvironmentToggles = new[] { "SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", "SHARPEMU_WRITABLE_APP0=1" },
        });
        File.WriteAllText(SettingsPath, json);

        Assert.True(Migrate());

        Assert.False(File.Exists(SharedPath));
        Assert.Equal("""{"LauncherTheme":"Midnight"}""", JsonNode.Parse(File.ReadAllText(SettingsPath))!.ToJsonString());
        Assert.True(File.Exists(SettingsPath + SettingsSplitMigration.BackupSuffix));
    }

    [Fact]
    public void UnreadableSharedFileIsLeftAlone()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        WriteShared("{ not json");

        Assert.True(Migrate());

        Assert.Equal("{ not json", File.ReadAllText(SharedPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ broken")]
    [InlineData("""{ "LauncherTheme": "Forest" }""")]
    public void NothingToMoveWritesNothing(string? json)
    {
        if (json is not null)
        {
            File.WriteAllText(SettingsPath, json);
        }

        Assert.False(Migrate());

        Assert.Equal(json is not null, File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + SettingsSplitMigration.BackupSuffix));
        Assert.False(File.Exists(SharedPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

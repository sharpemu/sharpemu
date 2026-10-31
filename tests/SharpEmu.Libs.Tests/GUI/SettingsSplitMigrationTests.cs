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

    private string GamesPath => Path.Combine(_root, "user", "custom_configs");

    private string GamePath(string titleId) => Path.Combine(GamesPath, titleId + ".json");

    private bool Migrate(params string[] library) =>
        SettingsSplitMigration.Migrate(SettingsPath, GamesPath, _ => library);

    private PerGameSettings Game(string titleId) =>
        PerGameSettings.NormalizeFromJson(File.ReadAllText(GamePath(titleId)))!;

    private void WriteGame(string titleId, string json)
    {
        Directory.CreateDirectory(GamesPath);
        File.WriteAllText(GamePath(titleId), json);
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
    public void MovesChangedValuesToKnownGamesAndCleansTheGlobalFile()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        // A game with settings of its own keeps them; everything else follows the old global.
        WriteGame("PPSA00001", """{ "OverlayCorner": "TopLeft", "EnvironmentChanges": ["SHARPEMU_CRASH_CAPTURE=0", "SHARPEMU_LOG_IO"] }""");

        Assert.True(Migrate("PPSA00002", "ppsa00001", ""));

        Assert.Equal(LegacyGlobal, File.ReadAllText(SettingsPath + SettingsSplitMigration.BackupSuffix));
        var global = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Empty(global.Select(pair => pair.Key).Intersect(SettingsSplitMigration.LegacyGameKeys));
        Assert.Equal("Forest", (string?)global["LauncherTheme"]);
        Assert.Equal("/Volumes/Games", (string?)global["GameFolders"]![0]);
        Assert.Equal(0.9, (double)global["LibraryTileScale"]!);

        var own = EffectiveLaunchSettings.Resolve(Game("PPSA00001"));
        Assert.Equal("TopLeft", own.OverlayCorner);
        Assert.Equal("Full", own.OverlayMode);
        Assert.False(own.VSync);
        Assert.True(own.LogToFile);
        Assert.Equal("Off", own.GpuReadbacks);
        Assert.Equal("Host", own.BinkPlaybackMode);
        Assert.Contains("SHARPEMU_LOG_IO", own.EnvironmentToggles);
        Assert.DoesNotContain(own.EnvironmentToggles, entry => entry.StartsWith("SHARPEMU_CRASH_CAPTURE"));
        Assert.Contains("SHARPEMU_WRITABLE_APP0", own.EnvironmentToggles);

        var library = Game("PPSA00002");
        Assert.Equal("BottomRight", library.OverlayCorner);
        Assert.Equal("Full", library.OverlayMode);
        Assert.False(library.VSync);
        Assert.Equal("Host", library.BinkPlaybackMode);
        Assert.Contains("SHARPEMU_CRASH_CAPTURE", EffectiveLaunchSettings.Resolve(library).EnvironmentToggles);
        // Values the old file held at their defaults are not written out.
        Assert.Null(library.LogLevel);
        Assert.Null(library.WindowMode);
        Assert.Null(library.Resolution);
        Assert.Null(library.OverlayEnabled);

        // A game the launcher has never seen starts from the built-in defaults.
        Assert.False(File.Exists(GamePath("PPSA00003")));
        Assert.Equal(EffectiveLaunchSettings.Defaults, EffectiveLaunchSettings.Resolve(null));
    }

    [Fact]
    public void EachKnownGameKeepsTheEffectiveSettingsItHadBefore()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        WriteGame("PPSA00001", """{ "VSync": true, "Resolution": "2560x1440", "EnvironmentChanges": ["SHARPEMU_LOG_NP"] }""");
        WriteGame("PPSA00004", """{ "EnvironmentToggles": ["SHARPEMU_WRITABLE_APP0"] }""");
        var before = new Dictionary<string, PerGameSettings?>
        {
            ["PPSA00001"] = Game("PPSA00001"),
            ["PPSA00002"] = null,
            ["PPSA00004"] = Game("PPSA00004"),
        };

        Migrate("PPSA00002");

        var legacy = SettingsSplitMigration.CarriedValues(LegacyGlobal);
        foreach (var (titleId, game) in before)
        {
            var after = EffectiveLaunchSettings.Resolve(Game(titleId));
            // The pre-split rule: a game's value, else the global one, with its environment
            // changes laid over its own whole list or the global list.
            Assert.Equal(game?.VSync ?? false, after.VSync);
            Assert.Equal(game?.Resolution ?? "1920x1080", after.Resolution);
            Assert.Equal("BottomRight", after.OverlayCorner);
            Assert.Equal(
                EnvironmentOverrides.Merge(game?.EnvironmentToggles ?? legacy.EnvironmentToggles!, game?.EnvironmentChanges)
                    .Select(EnvironmentOverrides.Name).Order(StringComparer.OrdinalIgnoreCase),
                after.EnvironmentToggles.Select(EnvironmentOverrides.Name).Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SecondRunChangesNothing()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        WriteGame("PPSA00001", """{ "OverlayCorner": "TopLeft" }""");
        Assert.True(Migrate("PPSA00002"));
        var global = File.ReadAllBytes(SettingsPath);
        var first = File.ReadAllBytes(GamePath("PPSA00001"));
        var second = File.ReadAllBytes(GamePath("PPSA00002"));
        var backup = File.ReadAllBytes(SettingsPath + SettingsSplitMigration.BackupSuffix);

        Assert.False(Migrate("PPSA00002", "PPSA00005"));

        Assert.Equal(global, File.ReadAllBytes(SettingsPath));
        Assert.Equal(first, File.ReadAllBytes(GamePath("PPSA00001")));
        Assert.Equal(second, File.ReadAllBytes(GamePath("PPSA00002")));
        Assert.Equal(backup, File.ReadAllBytes(SettingsPath + SettingsSplitMigration.BackupSuffix));
        Assert.False(File.Exists(GamePath("PPSA00005")));
    }

    [Fact]
    public void FailedRunKeepsTheGlobalKeysAndTheNextRunFinishes()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        WriteGame("PPSA00001", "{}");
        // A directory where a game's file goes makes that write fail part way through.
        Directory.CreateDirectory(GamePath("PPSA00002"));

        Assert.ThrowsAny<IOException>(() => Migrate("PPSA00002"));
        Assert.Equal(LegacyGlobal, File.ReadAllText(SettingsPath));
        Assert.Equal(LegacyGlobal, File.ReadAllText(SettingsPath + SettingsSplitMigration.BackupSuffix));

        // The user changes a value in the meantime; the retry must not undo it.
        WriteGame("PPSA00001", """{ "OverlayMode": "Minimal" }""");
        Directory.Delete(GamePath("PPSA00002"));
        Assert.True(Migrate("PPSA00002"));

        Assert.Equal("Minimal", Game("PPSA00001").OverlayMode);
        Assert.Equal("Full", Game("PPSA00002").OverlayMode);
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

        Assert.True(Migrate("PPSA00002"));

        Assert.False(Directory.Exists(GamesPath));
        Assert.Equal("""{"LauncherTheme":"Midnight"}""", JsonNode.Parse(File.ReadAllText(SettingsPath))!.ToJsonString());
        Assert.True(File.Exists(SettingsPath + SettingsSplitMigration.BackupSuffix));
    }

    [Fact]
    public void UnreadableGameFileIsLeftAlone()
    {
        File.WriteAllText(SettingsPath, LegacyGlobal);
        WriteGame("PPSA00001", "{ not json");

        Assert.True(Migrate());

        Assert.Equal("{ not json", File.ReadAllText(GamePath("PPSA00001")));
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

        Assert.False(Migrate("PPSA00002"));

        Assert.Equal(json is not null, File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + SettingsSplitMigration.BackupSuffix));
        Assert.False(Directory.Exists(GamesPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

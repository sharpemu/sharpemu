// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GameSettingsKeyTests
{
    [Fact]
    public void TitleIdKeepsExistingSettingsFile()
    {
        Assert.Equal("PPSA01234", PerGameSettings.KeyFor("PPSA01234", "unused.zar"));
    }

    [Fact]
    public void MissingTitleIdUsesNormalizedFullPathAndSeparatesGames()
    {
        var path = Path.Combine(Path.GetTempPath(), "first.zar");
        var key = PerGameSettings.KeyFor(null, path);
        Assert.StartsWith("PATH-", key);
        Assert.Equal(key, PerGameSettings.KeyFor("", Path.Combine(Path.GetTempPath(), "sub", "..", "first.zar")));
        Assert.NotEqual(key, PerGameSettings.KeyFor(null, Path.Combine(Path.GetTempPath(), "second.zar")));
        Assert.Equal(key + ".json", Path.GetFileName(PerGameSettings.PathFor(key)));
    }

    [Fact]
    public void MigrationIncludesGamesWithoutMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpemu-key-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "eboot.bin");
            File.WriteAllBytes(executable, [0]);
            var settings = Path.Combine(root, "gui-settings.json");
            File.WriteAllText(settings, "{\"LogLevel\":\"Debug\",\"VSync\":false}");
            var game = Assert.Single(MainWindow.ScanFolders([root], new HashSet<string>()));
            var key = PerGameSettings.KeyFor(game.TitleId, game.Path);
            var configs = Path.Combine(root, "custom_configs");
            Assert.True(SettingsSplitMigration.Migrate(settings, configs, _ => [key]));
            var migrated = PerGameSettings.NormalizeFromJson(File.ReadAllText(Path.Combine(configs, key + ".json")))!;
            Assert.Equal("Debug", migrated.LogLevel);
            Assert.False(migrated.VSync);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

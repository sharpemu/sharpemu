// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GameVersionsTests
{
    [Fact]
    public void Group_CombinesTitleIdsAndOrdersVersionsNumerically()
    {
        var older = Game("old", "09.000.000", " ppsa00001 ");
        var latest = Game("latest", "10.000.000");
        var unknown = Game("unknown", null);

        var group = Assert.Single(GameVersions.Group([older, unknown, latest]));

        Assert.Equal(new[] { latest, older, unknown }, group);
    }

    [Fact]
    public void Group_KeepsDifferentIdentitiesAndUnidentifiedGamesSeparate()
    {
        var games = new[]
        {
            Game("region-one", "01.000.000"),
            Game("region-two", "01.000.000", "PPSA00002"),
            Game("compatibility", "01.000.000", compatibilityMode: true),
            Game("no-id-one", "01.000.000", null),
            Game("no-id-two", "01.000.000", null),
        };

        Assert.Equal(5, GameVersions.Group(games).Count());
    }

    [Fact]
    public void ForGame_KeepsBothInstallationsOfTheSameVersion()
    {
        var first = Game("first", "01.000.000");
        var second = Game("second", "01.000.000");
        var other = Game("other", "02.000.000", "PPSA00002");

        var versions = GameVersions.ForGame([first, second, other], second);

        Assert.Equal(new[] { first, second }, versions);
        Assert.Contains(second, versions);
    }

    [Fact]
    public void PackableForGame_FindsTheFolderWhenTheVisibleVersionIsAnArchive()
    {
        var archive = new GameEntry("Same game", "PPSA00001", "02.000.000", "/games/latest.ZAR", 1024, null, null);
        var folder = Game("older", "01.000.000");

        var packable = GameVersions.PackableForGame([archive, folder], archive);

        Assert.Same(folder, Assert.Single(packable));
        Assert.Equal("PPSA00001-1.000", MainWindow.SuggestedArchiveName("older", packable[0].TitleId, packable[0].Version));
    }

    [Fact]
    public void PackableForGame_KeepsAllFolderVersionsAndExcludesOtherGames()
    {
        var archive = new GameEntry("Same game", "PPSA00001", "03.000.000", "/games/latest.zar", 1024, null, null);
        var first = Game("first", "01.000.000");
        var second = Game("second", "02.000.000");
        var other = Game("other", "04.000.000", "PPSA00002");

        Assert.Equal(new[] { second, first }, GameVersions.PackableForGame([first, archive, other, second], archive));
        Assert.Equal(new[] { archive, second, first }, GameVersions.ForGame([first, archive, other, second], archive));
    }

    [Fact]
    public void PackableForGame_ExcludesArchivesEvenWhenTheirFilesAreUnavailable()
    {
        var archive = new GameEntry("Same game", "PPSA00001", "01.000.000", "/games/game.zar", 1024, null, null);

        Assert.Empty(GameVersions.PackableForGame([archive], archive));
    }

    [Fact]
    public void SetVersionCount_UpdatesTheCardAndResetsWhenAnInstallationIsRemoved()
    {
        var game = Game("game", null);
        var changed = new List<string?>();
        game.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        game.SetVersionCount(2);

        Assert.True(game.HasMultipleVersions);
        Assert.True(game.HasVersion);
        Assert.Contains(nameof(GameEntry.VersionText), changed);
        Assert.Contains(nameof(GameEntry.HasMultipleVersions), changed);

        game.SetVersionCount(1);

        Assert.False(game.HasMultipleVersions);
        Assert.False(game.HasVersion);
        Assert.Null(game.VersionText);
    }

    private static GameEntry Game(string folder, string? version, string? titleId = "PPSA00001", bool compatibilityMode = false) =>
        new("Same game", titleId, version, $"/games/{folder}/eboot.bin", 1024, null, null, compatibilityMode);
}

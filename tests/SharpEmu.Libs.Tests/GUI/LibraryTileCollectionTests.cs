// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class LibraryTileCollectionTests
{
    [Fact]
    public void KeepsAddFolderTileAfterVisibleGames()
    {
        var games = new ObservableCollection<GameEntry>();
        var tiles = new LibraryTileCollection(games);
        var first = CreateGame("First");
        var second = CreateGame("Second");

        games.Add(first);
        games.Add(second);

        Assert.Equal(3, tiles.Count);
        Assert.True(tiles is IList { IsReadOnly: true });
        Assert.Same(first, tiles[0]);
        Assert.Same(second, tiles[1]);
        Assert.Same(AddFolderTile.Instance, tiles[2]);
    }

    [Fact]
    public void ForwardsGameChangesAtTheirOriginalIndices()
    {
        var games = new ObservableCollection<GameEntry>();
        var tiles = new LibraryTileCollection(games);
        NotifyCollectionChangedEventArgs? change = null;
        tiles.CollectionChanged += (_, args) => change = args;

        var game = CreateGame("Game");
        games.Add(game);

        Assert.NotNull(change);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal(0, change.NewStartingIndex);
        Assert.Same(game, Assert.Single(change.NewItems!.Cast<GameEntry>()));
        Assert.Same(AddFolderTile.Instance, tiles[1]);
    }

    private static GameEntry CreateGame(string name) =>
        new(name, null, null, $"/games/{name}/eboot.bin", 0, null, null);
}

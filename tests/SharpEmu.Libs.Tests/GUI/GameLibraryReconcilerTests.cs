// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.ObjectModel;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GameLibraryReconcilerTests
{
    [Fact]
    public void Reconcile_PreservesExistingEntriesAndAppliesMetadataChanges()
    {
        var retained = CreateGame("retained", name: "Old name", version: "01.000");
        var removed = CreateGame("removed");
        var scannedRetained = CreateGame(
            "retained",
            name: "New name",
            version: "02.000");
        var added = CreateGame("added");

        var result = GameLibraryReconciler.Reconcile(
            [retained, removed],
            [scannedRetained, added]);

        Assert.Equal(2, result.Games.Count);
        Assert.Same(retained, result.Games[0]);
        Assert.Same(added, result.Games[1]);
        Assert.Equal("New name", retained.Name);
        Assert.Equal("02.000", retained.Version);
        Assert.Contains(retained, result.SizesToMeasure);
        Assert.Contains(added, result.SizesToMeasure);
        Assert.DoesNotContain(removed, result.Games);
    }

    [Fact]
    public void Reconcile_UnchangedCachedEntryKeepsSizeWithoutRemeasuringInstall()
    {
        var retained = CreateGame("retained", sizeBytes: 8L << 30);
        var scanned = CreateGame("retained", sizeBytes: 64L << 20);

        var result = GameLibraryReconciler.Reconcile([retained], [scanned]);

        Assert.Same(retained, Assert.Single(result.Games));
        Assert.Equal(8L << 30, retained.SizeBytes);
        Assert.Empty(result.SizesToMeasure);
    }

    [Fact]
    public void Reconcile_ChangedCoverAndBackgroundReloadsExistingEntry()
    {
        var retained = CreateGame(
            "retained",
            coverPath: AssetPath("old-cover.png"),
            backgroundPath: AssetPath("old-background.png"));
        var scanned = CreateGame(
            "retained",
            coverPath: AssetPath("new-cover.png"),
            backgroundPath: AssetPath("new-background.png"));

        var result = GameLibraryReconciler.Reconcile([retained], [scanned]);

        Assert.Same(retained, Assert.Single(result.Games));
        Assert.Same(retained, Assert.Single(result.CoversToLoad));
        Assert.Contains(retained, result.BackgroundsChanged);
        Assert.Equal(scanned.CoverPath, retained.CoverPath);
        Assert.Equal(scanned.BackgroundPath, retained.BackgroundPath);
    }

    [Fact]
    public void Reconcile_UnchangedCachedCoverWithoutBitmapQueuesReload()
    {
        var coverPath = AssetPath("cover.png");
        var retained = CreateGame("retained", coverPath: coverPath);
        var scanned = CreateGame("retained", coverPath: coverPath);

        Assert.Null(retained.Cover);

        var result = GameLibraryReconciler.Reconcile([retained], [scanned]);

        Assert.Same(retained, Assert.Single(result.Games));
        Assert.Same(retained, Assert.Single(result.CoversToLoad));
    }

    [Fact]
    public void ReconcileVisibleGames_UsesMinimalIdentityPreservingChanges()
    {
        var first = CreateGame("first");
        var second = CreateGame("second");
        var removed = CreateGame("removed");
        var added = CreateGame("added");
        var visible = new ObservableCollection<GameEntry>
        {
            first,
            second,
            removed,
        };

        GameLibraryReconciler.ReconcileVisibleGames(
            visible,
            [second, first, added]);

        Assert.Equal([second, first, added], visible);
        Assert.Same(second, visible[0]);
        Assert.Same(first, visible[1]);
        Assert.Same(added, visible[2]);
    }

    private static GameEntry CreateGame(
        string id,
        string? name = null,
        string? version = null,
        string? coverPath = null,
        string? backgroundPath = null,
        long sizeBytes = 1)
        => new(
            name ?? id,
            $"PPSA-{id}",
            version,
            GamePath(id),
            sizeBytes,
            coverPath,
            backgroundPath);

    private static string GamePath(string id)
        => Path.GetFullPath(Path.Combine("library-tests", id, "eboot.bin"));

    private static string AssetPath(string name)
        => Path.GetFullPath(Path.Combine("library-tests", "assets", name));
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal sealed record GameLibraryReconciliation(
    IReadOnlyList<GameEntry> Games,
    IReadOnlyList<GameEntry> CoversToLoad,
    IReadOnlySet<GameEntry> BackgroundsChanged);

/// <summary>
/// Applies filesystem scan results while preserving presentation object
/// identity for games that remain in the library
/// </summary>
internal static class GameLibraryReconciler
{
    public static GameLibraryReconciliation Reconcile(
        IReadOnlyList<GameEntry> current,
        IReadOnlyList<GameEntry> scanned)
    {
        var existingByPath = current.ToDictionary(game => game.Path, GameLibraryPath.Comparer);
        var merged = new List<GameEntry>(scanned.Count);
        var coversToLoad = new List<GameEntry>();
        var backgroundsChanged = new HashSet<GameEntry>();

        foreach (var scannedGame in scanned)
        {
            if (!existingByPath.TryGetValue(scannedGame.Path, out var existing))
            {
                merged.Add(scannedGame);
                if (scannedGame.CoverPath is not null)
                {
                    coversToLoad.Add(scannedGame);
                }

                continue;
            }

            var changes = existing.UpdateFrom(scannedGame);
            merged.Add(existing);
            if ((changes & GameEntryChanges.Cover) != 0 && existing.CoverPath is not null)
            {
                coversToLoad.Add(existing);
            }

            if ((changes & GameEntryChanges.Background) != 0)
            {
                backgroundsChanged.Add(existing);
            }
        }

        return new GameLibraryReconciliation(merged, coversToLoad, backgroundsChanged);
    }

    /// <summary>
    /// Reorders, inserts and removes only the items needed to reach the desired
    /// visible sequence
    /// </summary>
    public static void ReconcileVisibleGames(
        IList<GameEntry> visible,
        IReadOnlyList<GameEntry> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var game = desired[index];
            if (index < visible.Count && ReferenceEquals(visible[index], game))
            {
                continue;
            }

            var existingIndex = -1;
            for (var candidate = index + 1; candidate < visible.Count; candidate++)
            {
                if (ReferenceEquals(visible[candidate], game))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existing = visible[existingIndex];
                visible.RemoveAt(existingIndex);
                visible.Insert(index, existing);
            }
            else
            {
                visible.Insert(index, game);
            }
        }

        while (visible.Count > desired.Count)
        {
            visible.RemoveAt(visible.Count - 1);
        }
    }
}

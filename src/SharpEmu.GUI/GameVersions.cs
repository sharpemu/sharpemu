// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal static class GameVersions
{
    public static IEnumerable<IReadOnlyList<GameEntry>> Group(IEnumerable<GameEntry> games) =>
        games.GroupBy(Identity).Select(group => Order(group));

    public static IReadOnlyList<GameEntry> ForGame(IEnumerable<GameEntry> games, GameEntry game) =>
        Order(games.Where(candidate => SameGame(candidate, game)));

    public static IReadOnlyList<GameEntry> PackableForGame(IEnumerable<GameEntry> games, GameEntry game) =>
        ForGame(games, game).Where(version => !version.IsArchive).ToArray();

    public static bool SameGame(GameEntry left, GameEntry right) => Identity(left) == Identity(right);

    public static Version? VersionNumber(GameEntry game) =>
        Version.TryParse(game.Version?.Trim(), out var version) ? version : null;

    private static (bool CompatibilityMode, string Key) Identity(GameEntry game) =>
        (game.CompatibilityMode, PerGameSettings.KeyFor(game.TitleId, game.Path).ToUpperInvariant());

    private static IReadOnlyList<GameEntry> Order(IEnumerable<GameEntry> games) =>
        games.OrderByDescending(VersionNumber)
            .ThenBy(game => game.Path, GameLibraryPath.Comparer)
            .ToArray();
}

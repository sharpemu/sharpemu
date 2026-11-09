// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

internal static class TrophyCache
{
    private sealed record CachedTrophy(int Id, int Grade, string Name, string Description, bool Hidden);

    private sealed record CachedGame(long PackageSize, Dictionary<string, List<CachedTrophy>> Languages, string? ContentStamp = null);

    private static readonly string FilePath = Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "trophy_cache.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly object Gate = new();
    private static Dictionary<string, CachedGame>? _games;
    private static bool _dirty;

    public static IReadOnlyList<TrophyEntry> Load(string gameDirectory, string titleId, string language)
    {
        var package = new FileInfo(TrophyCatalog.PackagePath(gameDirectory));
        if (!package.Exists)
        {
            return [];
        }

        var key = Path.GetFullPath(gameDirectory);
        var contentStamp = TrophyCatalog.ContentStamp(gameDirectory);
        List<CachedTrophy>? trophies = null;
        lock (Gate)
        {
            EnsureLoaded();
            if (_games!.TryGetValue(key, out var cached) && cached.ContentStamp == contentStamp)
            {
                cached.Languages.TryGetValue(language, out trophies);
            }
        }

        if (trophies is null)
        {
            trophies = TrophyCatalog.Load(gameDirectory, titleId, includeIcons: false, language)
                .Select(t => new CachedTrophy(t.Id, t.Grade, t.Name, t.Description, t.Hidden))
                .ToList();
            lock (Gate)
            {
                if (!_games!.TryGetValue(key, out var cached) || cached.ContentStamp != contentStamp)
                {
                    cached = new CachedGame(package.Length, new Dictionary<string, List<CachedTrophy>>(StringComparer.OrdinalIgnoreCase), contentStamp);
                    _games[key] = cached;
                }

                cached.Languages[language] = trophies;
                _dirty = true;
            }
        }

        var unlocks = TrophyCatalog.ReadUnlocks(titleId);
        return trophies
            .Select(t => new TrophyEntry(
                t.Id,
                t.Grade,
                t.Name,
                t.Description,
                t.Hidden,
                unlocks.ContainsKey(t.Id),
                unlocks.TryGetValue(t.Id, out var at) ? at : null,
                null))
            .ToArray();
    }

    public static void Save()
    {
        string json;
        lock (Gate)
        {
            if (!_dirty || _games is null)
            {
                return;
            }

            json = JsonSerializer.Serialize(_games, JsonOptions);
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[GUI][WARN] Could not save the trophy cache: {ex.Message}");
        }
    }

    private static void EnsureLoaded()
    {
        if (_games is not null)
        {
            return;
        }

        try
        {
            var stored = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, CachedGame>>(File.ReadAllBytes(FilePath), JsonOptions)
                : null;
            _games = stored is null
                ? null
                : stored.Where(entry => entry.Value?.Languages is not null).ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value with { Languages = new Dictionary<string, List<CachedTrophy>>(entry.Value.Languages, StringComparer.OrdinalIgnoreCase) },
                    GameLibraryPath.Comparer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException
                                       or ArgumentException)
        {
            _games = null;
        }

        _games ??= new Dictionary<string, CachedGame>(GameLibraryPath.Comparer);
    }
}

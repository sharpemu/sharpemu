// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEmu.GUI;

/// <summary>
/// Remembers the last completed library scan so a cold start can paint the
/// grid immediately instead of waiting on a recursive walk of every game
/// folder. The cache is a display seed, never an authority: startup still
/// runs the normal scan and reconciles over it, so a stale file can only
/// ever cost one frame of wrong content, not a wrong library.
/// </summary>
internal static class GameLibraryCache
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string CachePath =>
        Path.Combine(AppContext.BaseDirectory, "user", "library_cache.json");

    internal sealed class CachedGame
    {
        public string Name { get; set; } = string.Empty;
        public string? TitleId { get; set; }
        public string? Version { get; set; }
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? CoverPath { get; set; }
        public string? BackgroundPath { get; set; }
    }

    internal sealed class CacheDocument
    {
        public int Version { get; set; }
        public List<string> Folders { get; set; } = [];
        public List<CachedGame> Games { get; set; } = [];
    }

    /// <summary>
    /// Returns the cached games when the file matches the configured folder
    /// set and the executables still exist. Entries whose executable is gone
    /// are dropped so a removed game never flashes on screen.
    /// </summary>
    internal static List<GameEntry> Load(IReadOnlyList<string> folders)
    {
        var games = new List<GameEntry>();
        try
        {
            if (!File.Exists(CachePath))
            {
                return games;
            }

            var document = JsonSerializer.Deserialize<CacheDocument>(
                File.ReadAllText(CachePath),
                SerializerOptions);
            if (document is null || document.Version != CurrentVersion)
            {
                return games;
            }

            if (!SameFolders(document.Folders, folders))
            {
                return games;
            }

            foreach (var cached in document.Games)
            {
                if (string.IsNullOrWhiteSpace(cached.Path) || !File.Exists(cached.Path))
                {
                    continue;
                }

                games.Add(new GameEntry(
                    cached.Name,
                    cached.TitleId,
                    cached.Version,
                    cached.Path,
                    cached.SizeBytes,
                    Existing(cached.CoverPath),
                    Existing(cached.BackgroundPath)));
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[GUI][WARN] Could not read the library cache: {exception.Message}");
            games.Clear();
        }

        return games;
    }

    internal static void Save(IReadOnlyList<string> folders, IReadOnlyList<GameEntry> games)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new CacheDocument
            {
                Version = CurrentVersion,
                Folders = [.. folders],
            };

            foreach (var game in games)
            {
                document.Games.Add(new CachedGame
                {
                    Name = game.Name,
                    TitleId = game.TitleId,
                    Version = game.Version,
                    Path = game.Path,
                    SizeBytes = game.SizeBytes,
                    CoverPath = game.CoverPath,
                    BackgroundPath = game.BackgroundPath,
                });
            }

            File.WriteAllText(
                CachePath,
                JsonSerializer.Serialize(document, SerializerOptions));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[GUI][WARN] Could not write the library cache: {exception.Message}");
        }
    }

    private static string? Existing(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;

    private static bool SameFolders(
        IReadOnlyList<string> cached,
        IReadOnlyList<string> configured)
    {
        if (cached.Count != configured.Count)
        {
            return false;
        }

        var known = new HashSet<string>(cached, GameLibraryPath.Comparer);
        foreach (var folder in configured)
        {
            if (!known.Contains(folder))
            {
                return false;
            }
        }

        return true;
    }
}

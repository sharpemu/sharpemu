// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

public sealed record TrophyEntry(
    int Id,
    int Grade,
    string Name,
    string Description,
    bool Hidden,
    bool Unlocked,
    DateTime? UnlockedAtUtc,
    byte[]? IconPng)
{
    public int GroupId { get; init; } = -1;
    public int PlatinumId { get; init; } = -1;
}

public interface ITrophyPackageReader
{
    string ConfigurationStamp { get; }
    IReadOnlyList<TrophyEntry> Load(string gameDirectory, bool includeIcons, string? language);
}

public sealed record TrophyUnlockNotice(string TitleId, int TrophyId, int Grade, string Name);

public static class TrophyCatalog
{
    public static ITrophyPackageReader? CompatibilityReader { get; set; }

    public static string PackagePath(string gameDirectory)
    {
        var path = Path.Combine(gameDirectory, "sce_sys", "trophy2", "trophy00.ucp");
        return File.Exists(path) ? path : Path.Combine(gameDirectory, "sce_sys", "trophy", "trophy00.trp");
    }

    public static string ContentStamp(string gameDirectory)
    {
        var package = new FileInfo(PackagePath(gameDirectory));
        var binding = new FileInfo(Path.Combine(gameDirectory, "sce_sys", "npbind.dat"));
        return $"{package.FullName}|{(package.Exists ? package.Length : 0)}|{(package.Exists ? package.LastWriteTimeUtc.Ticks : 0)}|" +
            $"{(binding.Exists ? binding.LastWriteTimeUtc.Ticks : 0)}|{CompatibilityReader?.ConfigurationStamp}";
    }

    public static IReadOnlyDictionary<int, DateTime> ReadUnlocks(string titleId)
    {
        var unlocks = new Dictionary<int, DateTime>();
        try
        {
            var path = TrophyService.UnlockFilePath(titleId.ToUpperInvariant());
            if (!File.Exists(path))
            {
                return unlocks;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("unlocked", out var unlocked) &&
                unlocked.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var entry in unlocked.EnumerateObject())
                {
                    if (int.TryParse(entry.Name, out var id) && entry.Value.TryGetInt64(out var at))
                    {
                        unlocks[id] = new DateTime(at * 10, DateTimeKind.Utc);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or ArgumentException)
        {
        }

        return unlocks;
    }

    public static long Stamp(string gameDirectory, string titleId)
    {
        var unlocks = new FileInfo(TrophyService.UnlockFilePath(titleId.ToUpperInvariant()));
        return HashCode.Combine(
            ContentStamp(gameDirectory),
            unlocks.Exists ? unlocks.LastWriteTimeUtc.Ticks : 0,
            unlocks.Exists ? unlocks.Length : 0);
    }

    public static IReadOnlyList<TrophyEntry> Load(string gameDirectory, string titleId, bool includeIcons = true, string? language = null)
    {
        try
        {
            var trophies = ReadDefinitions(gameDirectory, includeIcons, language);
            TrophyService.ApplyUnlocks(trophies, TrophyService.UnlockFilePath(titleId.ToUpperInvariant()));
            return trophies
                .Select(t => new TrophyEntry(
                    t.Id,
                    t.Grade,
                    t.Name,
                    t.Description,
                    t.Hidden,
                    t.Unlocked,
                    t.Unlocked ? new DateTime(t.UnlockedAt * 10, DateTimeKind.Utc) : null,
                    t.IconPng) { GroupId = t.GroupId, PlatinumId = t.PlatinumId })
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    internal static TrophyService.Trophy[] ReadDefinitions(string gameDirectory, bool includeIcons, string? language)
    {
        var path = PackagePath(gameDirectory);
        if (!File.Exists(path))
            return [];
        if (Path.GetExtension(path).Equals(".trp", StringComparison.OrdinalIgnoreCase))
        {
            return CompatibilityReader?.Load(gameDirectory, includeIcons, language).Select(t => new TrophyService.Trophy
            {
                Id = t.Id, Grade = t.Grade, Name = t.Name, Description = t.Description, Hidden = t.Hidden,
                GroupId = t.GroupId, PlatinumId = t.PlatinumId, IconPng = t.IconPng,
            }).ToArray() ?? [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        return TrophyService.ReadTrophies(stream, includeIcons, language);
    }

    public static bool TryParseUnlockLine(string line, out TrophyUnlockNotice notice)
    {
        notice = null!;
        var start = line.IndexOf(TrophyService.UnlockMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        var parts = line[(start + TrophyService.UnlockMarker.Length)..].Split('|', 4);
        if (parts.Length != 4 || !int.TryParse(parts[1], out var id) || !int.TryParse(parts[2], out var grade))
        {
            return false;
        }

        notice = new TrophyUnlockNotice(parts[0], id, grade, parts[3].TrimEnd());
        return true;
    }
}

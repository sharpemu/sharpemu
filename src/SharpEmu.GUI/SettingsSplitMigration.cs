// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEmu.GUI;

/// <summary>
/// Moves the game settings a gui-settings.json written before the split still holds into each
/// known game's file, then drops them from the global file. A value equal to the built-in
/// default needs no move; any other one goes to every game that has no setting of its own for
/// it, so each known game keeps running exactly as before. A game the launcher has never seen
/// starts from the built-in defaults.
/// </summary>
/// <remarks>
/// Safe to run on every start: the global file is backed up once before anything is written,
/// game files are written before the global file, and a run that stops part way leaves the
/// global keys in place for the next run, which skips every value a game already has.
/// </remarks>
public static class SettingsSplitMigration
{
    public const string BackupSuffix = ".bak-pre-split";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The keys gui-settings.json held before the split that now belong to each game.</summary>
    public static IReadOnlyList<string> LegacyGameKeys { get; } =
    [
        nameof(PerGameSettings.LogLevel),
        nameof(PerGameSettings.ImportTraceLimit),
        nameof(PerGameSettings.StrictDynlibResolution),
        nameof(PerGameSettings.LogToFile),
        nameof(PerGameSettings.WindowMode),
        nameof(PerGameSettings.Resolution),
        nameof(PerGameSettings.DisplayIndex),
        nameof(PerGameSettings.RefreshRate),
        nameof(PerGameSettings.ScalingMode),
        nameof(PerGameSettings.VSync),
        nameof(PerGameSettings.HdrMode),
        nameof(PerGameSettings.GpuReadbacks),
        nameof(PerGameSettings.OverlayEnabled),
        nameof(PerGameSettings.OverlayCorner),
        nameof(PerGameSettings.OverlayMode),
        nameof(PerGameSettings.BinkPlaybackMode),
        nameof(PerGameSettings.EnvironmentToggles),
    ];

    /// <summary>Migrates the real settings files; failures are reported and retried on the next start.</summary>
    public static bool MigrateIfNeeded()
    {
        try
        {
            return Migrate(GuiSettings.SettingsPath, PerGameSettings.DirectoryPath, LibraryTitleIds);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[SETTINGS][WARN] Could not move game settings out of {GuiSettings.SettingsPath}; " +
                $"retrying on the next start: {exception.Message}");
            return false;
        }
    }

    /// <param name="libraryTitleIds">The title ids of the library under the given game folders.</param>
    /// <returns>Whether the global file held game settings and was rewritten without them.</returns>
    public static bool Migrate(
        string settingsPath,
        string perGameDirectory,
        Func<IReadOnlyList<string>, IEnumerable<string>> libraryTitleIds)
    {
        if (!File.Exists(settingsPath))
        {
            return false;
        }

        var json = File.ReadAllText(settingsPath);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // An unreadable file loads as defaults and has nothing to move.
            return false;
        }

        if (root is null || !LegacyGameKeys.Any(root.ContainsKey))
        {
            return false;
        }

        var backupPath = settingsPath + BackupSuffix;
        if (!File.Exists(backupPath))
        {
            File.Copy(settingsPath, backupPath);
        }

        var carried = CarriedValues(json);
        if (!carried.IsEmpty)
        {
            var folders = GuiSettings.NormalizeFromJson(json).GameFolders;
            foreach (var titleId in KnownTitleIds(perGameDirectory, libraryTitleIds(folders)))
            {
                CarryInto(PerGameSettings.PathFor(perGameDirectory, titleId), carried);
            }
        }

        foreach (var key in LegacyGameKeys)
        {
            root.Remove(key);
        }

        SettingsFilePersistence.Write(settingsPath, root.ToJsonString(WriteOptions));
        return true;
    }

    /// <summary>
    /// The game settings of a pre-split global file that differ from the built-in defaults,
    /// normalised the way that file was normalised when the launcher loaded it.
    /// </summary>
    public static PerGameSettings CarriedValues(string legacyGlobalJson)
    {
        var legacy = PerGameSettings.NormalizeFromJson(legacyGlobalJson) ?? new PerGameSettings();
        legacy.EnvironmentChanges = null;
        legacy.CustomEnvironmentVariables = null;

        legacy.WindowMode = GuiSettings.NormalizeChoice(legacy.WindowMode, "Windowed", "Borderless", "Exclusive");
        legacy.Resolution = NormalizeResolution(legacy.Resolution);
        legacy.ScalingMode = GuiSettings.NormalizeChoice(legacy.ScalingMode, "Fit", "Cover", "Stretch", "Integer");
        legacy.HdrMode = GuiSettings.NormalizeChoice(legacy.HdrMode, "Auto", "On", "Off");
        legacy.GpuReadbacks = GuiSettings.NormalizeChoice(legacy.GpuReadbacks, "Full", "Off");
        legacy.BinkPlaybackMode = GuiSettings.NormalizeChoice(legacy.BinkPlaybackMode, "Guest", "Host", "Skip");
        legacy.OverlayCorner = GuiSettings.NormalizeChoice(
            legacy.OverlayCorner, "TopRight", "TopLeft", "BottomRight", "BottomLeft");
        legacy.OverlayMode = GuiSettings.NormalizeChoice(legacy.OverlayMode, "TitleBar", "Full", "Minimal");
        legacy.DisplayIndex = legacy.DisplayIndex is { } displayIndex ? Math.Max(0, displayIndex) : null;
        legacy.RefreshRate = legacy.RefreshRate is { } refreshRate ? Math.Clamp(refreshRate, 0, 1000) : null;
        // The on-screen keyboard profile is a launcher setting (GuiSettings.DefaultProfile), not
        // a game switch; very old files may still carry it in their switch list.
        legacy.EnvironmentToggles?.RemoveAll(entry => string.Equals(
            EnvironmentOverrides.Name(entry),
            "SHARPEMU_DEFAULT_PROFILE",
            StringComparison.OrdinalIgnoreCase));

        legacy.RemoveDefaultValues();
        return legacy;
    }

    /// <summary>
    /// Fills every setting <paramref name="game"/> leaves unset with the carried global value;
    /// a setting the game already has, and every environment variable it names, stays its own.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public static bool Fill(PerGameSettings game, PerGameSettings carried)
    {
        var changed = false;
        game.LogLevel = Fill(game.LogLevel, carried.LogLevel, ref changed);
        game.ImportTraceLimit = Fill(game.ImportTraceLimit, carried.ImportTraceLimit, ref changed);
        game.StrictDynlibResolution = Fill(game.StrictDynlibResolution, carried.StrictDynlibResolution, ref changed);
        game.LogToFile = Fill(game.LogToFile, carried.LogToFile, ref changed);
        game.WindowMode = Fill(game.WindowMode, carried.WindowMode, ref changed);
        game.Resolution = Fill(game.Resolution, carried.Resolution, ref changed);
        game.DisplayIndex = Fill(game.DisplayIndex, carried.DisplayIndex, ref changed);
        game.RefreshRate = Fill(game.RefreshRate, carried.RefreshRate, ref changed);
        game.ScalingMode = Fill(game.ScalingMode, carried.ScalingMode, ref changed);
        game.VSync = Fill(game.VSync, carried.VSync, ref changed);
        game.HdrMode = Fill(game.HdrMode, carried.HdrMode, ref changed);
        game.GpuReadbacks = Fill(game.GpuReadbacks, carried.GpuReadbacks, ref changed);
        game.OverlayEnabled = Fill(game.OverlayEnabled, carried.OverlayEnabled, ref changed);
        game.OverlayCorner = Fill(game.OverlayCorner, carried.OverlayCorner, ref changed);
        game.OverlayMode = Fill(game.OverlayMode, carried.OverlayMode, ref changed);
        game.BinkPlaybackMode = Fill(game.BinkPlaybackMode, carried.BinkPlaybackMode, ref changed);

        // A game with a whole list of its own never followed the global one. Otherwise its
        // changes were laid over the global list; the same effective list, as changes from the
        // defaults, is what it keeps.
        if (carried.EnvironmentToggles is { } globalToggles && game.EnvironmentToggles is null)
        {
            var changes = EnvironmentOverrides.Between(
                EffectiveLaunchSettings.Defaults.EnvironmentToggles,
                EnvironmentOverrides.Merge(globalToggles, game.EnvironmentChanges));
            if (!(changes ?? []).SequenceEqual(game.EnvironmentChanges ?? [], StringComparer.OrdinalIgnoreCase))
            {
                game.EnvironmentChanges = changes;
                changed = true;
            }
        }

        return changed;
    }

    private static string? Fill(string? current, string? carried, ref bool changed)
    {
        if (current is not null || carried is null)
        {
            return current;
        }

        changed = true;
        return carried;
    }

    private static T? Fill<T>(T? current, T? carried, ref bool changed) where T : struct
    {
        if (current is not null || carried is null)
        {
            return current;
        }

        changed = true;
        return carried;
    }

    private static void CarryInto(string path, PerGameSettings carried)
    {
        var game = new PerGameSettings();
        if (File.Exists(path))
        {
            try
            {
                game = PerGameSettings.NormalizeFromJson(File.ReadAllText(path)) ?? game;
            }
            catch (JsonException)
            {
                // The launcher already ignores this file; leave it for the user rather than
                // replace it.
                return;
            }
        }

        if (Fill(game, carried))
        {
            SettingsFilePersistence.Write(path, PerGameSettings.Serialize(game));
        }
    }

    private static IEnumerable<string> KnownTitleIds(string perGameDirectory, IEnumerable<string> libraryTitleIds)
    {
        List<string> existing = Directory.Exists(perGameDirectory)
            ? Directory.EnumerateFiles(perGameDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name) && !name.StartsWith('.'))
                .Select(name => name!)
                .ToList()
            : [];
        return existing
            .Concat(libraryTitleIds.Where(titleId => !string.IsNullOrWhiteSpace(titleId)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // The library as the launcher last saw it, plus whatever the folders hold now: a drive that
    // is not mounted still has its games in the cache, and a game added since the last scan is
    // only on disk. Games removed from the library are included, in case they come back.
    private static IEnumerable<string> LibraryTitleIds(IReadOnlyList<string> folders) =>
        GameLibraryCache.CachedSettingsKeys()
            .Concat(MainWindow.ScanFolders(folders, new HashSet<string>())
                .Select(game => PerGameSettings.KeyFor(game.TitleId, game.Path)));

    private static string NormalizeResolution(string? value) =>
        HostDisplayOptions.TryParseResolution(value, out var width, out var height)
            ? $"{width}x{height}"
            : "1920x1080";
}

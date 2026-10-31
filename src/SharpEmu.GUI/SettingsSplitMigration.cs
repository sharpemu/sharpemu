// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEmu.GUI;

/// <summary>
/// Moves the game settings a gui-settings.json written before the split still holds into the
/// settings every game starts from (<see cref="PerGameSettings.GlobalPath"/>), then drops them
/// from the launcher's file. A value equal to the built-in default needs no move. Each game's
/// own file is left as it is: it still lays its values over the same shared ones, so every game
/// keeps running exactly as before.
/// </summary>
/// <remarks>
/// Safe to run on every start: the launcher's file is backed up once before anything is
/// written, the shared file is written before the launcher's file, and a run that stops part
/// way leaves the old keys in place for the next run, which keeps every value the shared file
/// already has.
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
            return Migrate(GuiSettings.SettingsPath, PerGameSettings.GlobalPath);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[SETTINGS][WARN] Could not move game settings out of {GuiSettings.SettingsPath}; " +
                $"retrying on the next start: {exception.Message}");
            return false;
        }
    }

    /// <param name="sharedGamePath">The file of the settings every game starts from.</param>
    /// <returns>Whether the launcher's file held game settings and was rewritten without them.</returns>
    public static bool Migrate(string settingsPath, string sharedGamePath)
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
            CarryInto(sharedGamePath, carried);
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
    /// Fills every setting <paramref name="game"/> leaves unset with the carried value; a setting
    /// it already has, and every environment variable it names, stays as it is.
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

        // A whole list of its own never followed the carried one. Otherwise its changes are laid
        // over the carried list; the same effective list, as changes from the defaults, is kept.
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
                // replace it. The old keys are still dropped: they would only fill it again.
                return;
            }
        }

        if (Fill(game, carried))
        {
            SettingsFilePersistence.Write(path, PerGameSettings.Serialize(game));
        }
    }

    private static string NormalizeResolution(string? value) =>
        HostDisplayOptions.TryParseResolution(value, out var width, out var height)
            ? $"{width}x{height}"
            : "1920x1080";
}

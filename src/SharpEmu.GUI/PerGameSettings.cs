// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEmu.GUI;

/// <summary>
/// One game's settings: everything that changes how a game runs or is shown. A null value
/// means the built-in default in <see cref="EffectiveLaunchSettings.Defaults"/>; there is no
/// global layer in between, so a file holds only what the game changes.
/// </summary>
public sealed class PerGameSettings
{
    // A null value is the default, so it is left out: the file names only what the game changes.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string? LogLevel { get; set; }

    public int? ImportTraceLimit { get; set; }

    public bool? StrictDynlibResolution { get; set; }

    public bool? LogToFile { get; set; }

    public string? WindowMode { get; set; }

    public string? Resolution { get; set; }

    public int? DisplayIndex { get; set; }

    public int? RefreshRate { get; set; }

    public string? ScalingMode { get; set; }

    public bool? VSync { get; set; }

    public string? HdrMode { get; set; }

    public string? GpuReadbacks { get; set; }

    public bool? OverlayEnabled { get; set; }
    public string? OverlayCorner { get; set; }
    public string? OverlayMode { get; set; }

    public string? BinkPlaybackMode { get; set; }

    // Written by older launchers: the game's whole switch list, which replaces the default one
    // (a switch missing from it is off). Still honoured; the launcher now writes
    // EnvironmentChanges instead.
    public List<string>? EnvironmentToggles { get; set; }

    // Per-variable overrides laid over the default list (or over EnvironmentToggles when an
    // older file has one); every variable not named here keeps its default state.
    public List<string>? EnvironmentChanges { get; set; }

    public List<string>? CustomEnvironmentVariables { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        LogLevel is null &&
        ImportTraceLimit is null &&
        StrictDynlibResolution is null &&
        LogToFile is null &&
        WindowMode is null &&
        Resolution is null &&
        DisplayIndex is null &&
        RefreshRate is null &&
        ScalingMode is null &&
        VSync is null &&
        HdrMode is null &&
        GpuReadbacks is null &&
        OverlayEnabled is null &&
        OverlayCorner is null &&
        OverlayMode is null &&
        BinkPlaybackMode is null &&
        EnvironmentToggles is null &&
        EnvironmentChanges is null &&
        CustomEnvironmentVariables is null;

    public static string DirectoryPath =>
        Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "custom_configs");

    // Windows paths are case-insensitive. Keep case on Unix, where a case-sensitive volume
    // can hold two different games whose paths differ only by case.
    public static string KeyFor(string? titleId, string gamePath)
    {
        if (!string.IsNullOrWhiteSpace(titleId)) return titleId.Trim();
        var path = Path.GetFullPath(gamePath);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return "PATH-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32];
    }

    public static string PathFor(string titleId) => PathFor(DirectoryPath, titleId);

    internal static string PathFor(string directory, string titleId) =>
        Path.Combine(directory, SanitizeTitleId(titleId) + ".json");

    internal static string Serialize(PerGameSettings settings) =>
        JsonSerializer.Serialize(settings, SerializerOptions);

    public static PerGameSettings? Load(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        try
        {
            var path = PathFor(titleId);
            if (File.Exists(path))
            {
                return NormalizeFromJson(File.ReadAllText(path));
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    // A null list keeps the defaults; only entries in a present list are sanitized.
    public static PerGameSettings? NormalizeFromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<PerGameSettings>(json, SerializerOptions);
        if (settings?.EnvironmentToggles is { } toggles)
        {
            settings.EnvironmentToggles = toggles.Where(entry => !string.IsNullOrEmpty(entry)).ToList();
        }
        if (settings?.EnvironmentChanges is { } changes)
        {
            settings.EnvironmentChanges = changes.Where(entry => !string.IsNullOrEmpty(entry)).ToList();
        }
        if (settings?.CustomEnvironmentVariables is { } customEnvironmentVariables)
        {
            settings.CustomEnvironmentVariables = customEnvironmentVariables
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .ToList();
        }

        return settings;
    }

    /// <summary>
    /// Removes values that already match <see cref="EffectiveLaunchSettings.Defaults"/> so the
    /// remaining object contains only the game's own settings.
    /// </summary>
    public void RemoveDefaultValues()
    {
        var defaults = EffectiveLaunchSettings.Defaults;
        LogLevel = UnlessEqual(LogLevel, defaults.LogLevel);
        ImportTraceLimit = UnlessEqual(ImportTraceLimit, defaults.ImportTraceLimit);
        StrictDynlibResolution = UnlessEqual(StrictDynlibResolution, defaults.StrictDynlibResolution);
        LogToFile = UnlessEqual(LogToFile, defaults.LogToFile);
        WindowMode = UnlessEqual(WindowMode, defaults.WindowMode);
        Resolution = UnlessEqual(Resolution, defaults.Resolution);
        DisplayIndex = UnlessEqual(DisplayIndex, defaults.DisplayIndex);
        RefreshRate = UnlessEqual(RefreshRate, defaults.RefreshRate);
        ScalingMode = UnlessEqual(ScalingMode, defaults.ScalingMode);
        VSync = UnlessEqual(VSync, defaults.VSync);
        HdrMode = UnlessEqual(HdrMode, defaults.HdrMode);
        GpuReadbacks = UnlessEqual(GpuReadbacks, defaults.GpuReadbacks);
        OverlayEnabled = UnlessEqual(OverlayEnabled, defaults.OverlayEnabled);
        OverlayCorner = UnlessEqual(OverlayCorner, defaults.OverlayCorner);
        OverlayMode = UnlessEqual(OverlayMode, defaults.OverlayMode);
        BinkPlaybackMode = UnlessEqual(BinkPlaybackMode, defaults.BinkPlaybackMode);

        if (EnvironmentToggles is { } environmentToggles &&
            EnvironmentEntriesEqual(environmentToggles, defaults.EnvironmentToggles))
        {
            EnvironmentToggles = null;
        }

        if (EnvironmentChanges is { } changes)
        {
            EnvironmentChanges = EnvironmentOverrides.WithoutInherited(
                changes,
                EnvironmentToggles ?? defaults.EnvironmentToggles);
        }
    }

    private static string? UnlessEqual(string? value, string fallback) =>
        string.Equals(value, fallback, StringComparison.OrdinalIgnoreCase) ? null : value;

    private static T? UnlessEqual<T>(T? value, T fallback) where T : struct =>
        value is { } present && EqualityComparer<T>.Default.Equals(present, fallback) ? null : value;

    public void Save(string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return;
        }

        try
        {
            SaveOrThrow(titleId);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Writes overrides or removes an empty override, propagating persistence failures.</summary>
    public void SaveOrThrow(string titleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);
        var path = PathFor(titleId);
        if (IsEmpty)
        {
            File.Delete(path); // missing file is harmless; permission/query failure must reach the explicit caller
            return;
        }
        SettingsFilePersistence.Write(path, Serialize(this));
    }

    private static string SanitizeTitleId(string titleId)
    {
        var trimmed = titleId.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return trimmed.Length == 0 ? "UNKNOWN" : trimmed;
    }

    private static bool EnvironmentEntriesEqual(
        IEnumerable<string> left,
        IEnumerable<string> right) =>
        NormalizeEnvironmentEntries(left).SetEquals(NormalizeEnvironmentEntries(right));

    private static HashSet<string> NormalizeEnvironmentEntries(IEnumerable<string> entries)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (StrictComputeSettings.IsEnabled(entries)) normalized.Add(StrictComputeSettings.VariableName);
        var materialized = entries as IReadOnlyCollection<string> ?? entries.ToList();
        // A launcher setting counts only when it differs from the emulator's default, in either direction.
        foreach (var setting in EnvironmentSettings.All)
        {
            if (EnvironmentSettings.Canonical(materialized, setting) is { } canonical)
            {
                normalized.Add(canonical);
            }
        }

        foreach (var entry in materialized)
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (EnvironmentSettings.Find(parts[0]) is not null) continue;
            // An explicit zero differs from the enabled default for this switch.
            if (string.Equals(parts[0], StrictComputeSettings.VariableName, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            if (parts.Length == 2 && parts[1] == "0")
            {
                continue;
            }

            normalized.Add(parts.Length == 2 && parts[1] != "1"
                ? $"{parts[0]}={parts[1]}"
                : parts[0]);
        }

        return normalized;
    }
}

/// <summary>What a launch uses: the game's settings with the built-in default for every value it leaves unset.</summary>
public sealed record EffectiveLaunchSettings(
    string LogLevel,
    int ImportTraceLimit,
    bool StrictDynlibResolution,
    bool LogToFile,
    string WindowMode,
    string Resolution,
    int DisplayIndex,
    int RefreshRate,
    string ScalingMode,
    bool VSync,
    string HdrMode,
    string GpuReadbacks,
    bool OverlayEnabled,
    string OverlayCorner,
    string OverlayMode,
    string BinkPlaybackMode,
    IReadOnlyList<string> EnvironmentToggles)
{
    /// <summary>
    /// The one place game settings take their defaults from: a new game, and every value a
    /// game's file leaves out, runs with these.
    /// </summary>
    public static EffectiveLaunchSettings Defaults { get; } = new(
        LogLevel: "Info",
        ImportTraceLimit: 0,
        StrictDynlibResolution: false,
        LogToFile: false,
        WindowMode: "Windowed",
        Resolution: "1920x1080",
        DisplayIndex: 0,
        RefreshRate: 0,
        ScalingMode: "Fit",
        VSync: true,
        HdrMode: "Auto",
        GpuReadbacks: "Full",
        OverlayEnabled: true,
        OverlayCorner: "TopRight",
        OverlayMode: "TitleBar",
        BinkPlaybackMode: "Guest",
        EnvironmentToggles: ["SHARPEMU_WRITABLE_APP0", "SHARPEMU_DISABLE_IMPORT_LOOP_GUARD"]);

    public static EffectiveLaunchSettings Resolve(PerGameSettings? perGame)
    {
        var defaults = Defaults;
        return new(
            perGame?.LogLevel ?? defaults.LogLevel,
            perGame?.ImportTraceLimit ?? defaults.ImportTraceLimit,
            perGame?.StrictDynlibResolution ?? defaults.StrictDynlibResolution,
            perGame?.LogToFile ?? defaults.LogToFile,
            perGame?.WindowMode ?? defaults.WindowMode,
            perGame?.Resolution ?? defaults.Resolution,
            Math.Max(0, perGame?.DisplayIndex ?? defaults.DisplayIndex),
            Math.Clamp(perGame?.RefreshRate ?? defaults.RefreshRate, 0, 1000),
            perGame?.ScalingMode ?? defaults.ScalingMode,
            perGame?.VSync ?? defaults.VSync,
            perGame?.HdrMode ?? defaults.HdrMode,
            perGame?.GpuReadbacks ?? defaults.GpuReadbacks,
            perGame?.OverlayEnabled ?? defaults.OverlayEnabled,
            perGame?.OverlayCorner ?? defaults.OverlayCorner,
            perGame?.OverlayMode ?? defaults.OverlayMode,
            perGame?.BinkPlaybackMode ?? defaults.BinkPlaybackMode,
            EnvironmentOverrides.Merge(
                perGame?.EnvironmentToggles ?? defaults.EnvironmentToggles,
                perGame?.EnvironmentChanges));
    }
}

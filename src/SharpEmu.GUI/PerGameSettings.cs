// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;
using SharpEmu.Libs;

namespace SharpEmu.GUI;

public sealed class PerGameSettings
{

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
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

    public bool? OverlayEnabled { get; set; }
    public string? OverlayCorner { get; set; }
    public string? OverlayMode { get; set; }

    public List<string>? EnvironmentToggles { get; set; }

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
        OverlayEnabled is null &&
        OverlayCorner is null &&
        OverlayMode is null &&
        EnvironmentToggles is null &&
        CustomEnvironmentVariables is null;

    public static string DirectoryPath => OperatingSystem.IsLinux()
        ? Path.Combine(XdgPaths.ConfigHome, "sharpemu", "custom_configs")
        : Path.Combine(AppContext.BaseDirectory, "user", "custom_configs");
    
    public static string PathFor(string titleId) =>
        Path.Combine(DirectoryPath, SanitizeTitleId(titleId) + ".json");

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

    // A null list inherits global settings; only entries in a present list are sanitized.
    internal static PerGameSettings? NormalizeFromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<PerGameSettings>(json, SerializerOptions);
        if (settings?.EnvironmentToggles is { } toggles)
        {
            settings.EnvironmentToggles = toggles.Where(entry => !string.IsNullOrEmpty(entry)).ToList();
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
    /// Removes values that already match the global configuration so the
    /// remaining object contains only effective per-game overrides.
    /// </summary>
    internal void RemoveInheritedValues(GuiSettings global)
    {
        if (string.Equals(LogLevel, global.LogLevel, StringComparison.OrdinalIgnoreCase))
        {
            LogLevel = null;
        }

        if (ImportTraceLimit == global.ImportTraceLimit)
        {
            ImportTraceLimit = null;
        }

        if (StrictDynlibResolution == global.StrictDynlibResolution)
        {
            StrictDynlibResolution = null;
        }

        if (LogToFile == global.LogToFile)
        {
            LogToFile = null;
        }

        if (string.Equals(WindowMode, global.WindowMode, StringComparison.OrdinalIgnoreCase))
        {
            WindowMode = null;
        }

        if (string.Equals(Resolution, global.Resolution, StringComparison.OrdinalIgnoreCase))
        {
            Resolution = null;
        }

        if (DisplayIndex == global.DisplayIndex)
        {
            DisplayIndex = null;
        }

        if (RefreshRate == global.RefreshRate)
        {
            RefreshRate = null;
        }

        if (string.Equals(ScalingMode, global.ScalingMode, StringComparison.OrdinalIgnoreCase))
        {
            ScalingMode = null;
        }

        if (VSync == global.VSync)
        {
            VSync = null;
        }

        if (string.Equals(HdrMode, global.HdrMode, StringComparison.OrdinalIgnoreCase))
        {
            HdrMode = null;
        }

        if (OverlayEnabled == global.OverlayEnabled)
        {
            OverlayEnabled = null;
        }

        if (string.Equals(OverlayCorner, global.OverlayCorner, StringComparison.OrdinalIgnoreCase))
        {
            OverlayCorner = null;
        }

        if (string.Equals(OverlayMode, global.OverlayMode, StringComparison.OrdinalIgnoreCase))
        {
            OverlayMode = null;
        }

        if (EnvironmentToggles is { } environmentToggles &&
            EnvironmentEntriesEqual(environmentToggles, global.EnvironmentToggles))
        {
            EnvironmentToggles = null;
        }

    }

    public void Save(string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return;
        }

        try
        {
            var path = PathFor(titleId);
            if (IsEmpty)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception)
        {
        }
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
        foreach (var entry in entries)
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
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
    bool OverlayEnabled,
    string OverlayCorner,
    string OverlayMode,
    IReadOnlyList<string> EnvironmentToggles)
{
    public static EffectiveLaunchSettings Resolve(GuiSettings global, PerGameSettings? perGame) => new(
        perGame?.LogLevel ?? global.LogLevel,
        perGame?.ImportTraceLimit ?? global.ImportTraceLimit,
        perGame?.StrictDynlibResolution ?? global.StrictDynlibResolution,
        perGame?.LogToFile ?? global.LogToFile,
        perGame?.WindowMode ?? global.WindowMode,
        perGame?.Resolution ?? global.Resolution,
        Math.Max(0, perGame?.DisplayIndex ?? global.DisplayIndex),
        Math.Clamp(perGame?.RefreshRate ?? global.RefreshRate, 0, 1000),
        perGame?.ScalingMode ?? global.ScalingMode,
        perGame?.VSync ?? global.VSync,
        perGame?.HdrMode ?? global.HdrMode,
        perGame?.OverlayEnabled ?? global.OverlayEnabled,
        perGame?.OverlayCorner ?? global.OverlayCorner,
        perGame?.OverlayMode ?? global.OverlayMode,
        perGame?.EnvironmentToggles ?? global.EnvironmentToggles);
}

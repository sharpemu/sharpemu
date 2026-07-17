// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

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

    public List<string>? EnvironmentToggles { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        LogLevel is null &&
        ImportTraceLimit is null &&
        StrictDynlibResolution is null &&
        LogToFile is null &&
        EnvironmentToggles is null;

    public static string DirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "user", "custom_configs");

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
                return JsonSerializer.Deserialize<PerGameSettings>(File.ReadAllText(path), SerializerOptions);
            }
        }
        catch (Exception)
        {
        }

        return null;
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
}

public sealed record EffectiveLaunchSettings(
    string LogLevel,
    int ImportTraceLimit,
    bool StrictDynlibResolution,
    bool LogToFile,
    IReadOnlyList<string> EnvironmentToggles)
{
    public static EffectiveLaunchSettings Resolve(GuiSettings global, PerGameSettings? perGame) => new(
        perGame?.LogLevel ?? global.LogLevel,
        perGame?.ImportTraceLimit ?? global.ImportTraceLimit,
        perGame?.StrictDynlibResolution ?? global.StrictDynlibResolution,
        perGame?.LogToFile ?? global.LogToFile,
        perGame?.EnvironmentToggles ?? global.EnvironmentToggles);
}

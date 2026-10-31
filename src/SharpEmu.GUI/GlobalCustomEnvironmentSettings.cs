// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GUI;

internal sealed class GlobalCustomEnvironmentSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public List<string> EnvironmentVariables { get; set; } = new();

    public static string FilePath =>
        Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "custom_envs.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var settings = JsonSerializer.Deserialize<GlobalCustomEnvironmentSettings>(
                File.ReadAllText(FilePath),
                SerializerOptions);
            return Normalize(settings?.EnvironmentVariables);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Writes the normalized entries; failures reach the caller so the launcher can report them.</summary>
    public static void SaveOrThrow(IEnumerable<string> entries)
    {
        var settings = new GlobalCustomEnvironmentSettings
        {
            EnvironmentVariables = Normalize(entries).ToList(),
        };
        SettingsFilePersistence.Write(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? entries) =>
        CustomEnvironmentVariables.Merge(entries);
}

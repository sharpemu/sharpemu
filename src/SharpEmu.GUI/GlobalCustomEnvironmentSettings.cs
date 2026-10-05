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
        Path.Combine(AppContext.BaseDirectory, "user", "custom_envs.json");

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

    public static void Save(IEnumerable<string> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var settings = new GlobalCustomEnvironmentSettings
            {
                EnvironmentVariables = Normalize(entries).ToList(),
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SerializerOptions));
        }
        catch (Exception)
        {
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string>? entries) =>
        CustomEnvironmentVariables.Merge(entries);
}

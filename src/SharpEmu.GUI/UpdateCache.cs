// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GUI;

/// <summary>Best-effort persistent cache for conditional GitHub release requests.</summary>
internal static class UpdateCache
{
    private sealed record Entry(string? Etag, string Json);

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "user", "update-cache.json");

    public static (string? Etag, string? Json) Load()
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(Path));
            return entry is null ? (null, null) : (entry.Etag, entry.Json);
        }
        catch { return (null, null); }
    }

    public static void Save(string? etag, string json)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(new Entry(etag, json)));
        }
        catch { }
    }
}

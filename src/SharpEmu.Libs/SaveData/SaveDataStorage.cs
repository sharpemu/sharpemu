// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpEmu.Libs.SaveData;

/// <summary>
/// Host-side layout and metadata for PS5 save data. Saves live under
/// <c>~/SharpEmu/Saves/&lt;titleId&gt;/&lt;dirName&gt;/</c> (overridable via
/// <c>SHARPEMU_SAVEDATA_DIR</c>); the game's files are written directly inside a
/// slot through the mounted <c>/savedata0</c> filesystem, and the PS5 UI
/// metadata (title/subtitle/detail/userParam) plus icon live under
/// <c>&lt;slot&gt;/sce_sys/</c>. This type is pure filesystem logic with no guest
/// interop so the path and metadata handling can be unit-tested.
/// </summary>
public static class SaveDataStorage
{
    /// <summary>Root of all saves: the env override, else <c>~/SharpEmu/Saves</c>.</summary>
    public static string Root(string? overrideDir = null)
    {
        var configured = overrideDir ?? Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "SharpEmu",
                "Saves")
            : configured;
        return Path.GetFullPath(root);
    }

    /// <summary>Per-title directory: <c>&lt;root&gt;/&lt;titleId&gt;</c>.</summary>
    public static string TitleRoot(string root, string titleId) =>
        Path.Combine(root, Sanitize(titleId));

    /// <summary>A single save slot: <c>&lt;titleRoot&gt;/&lt;dirName&gt;</c>.</summary>
    public static string SlotDir(string titleRoot, string dirName) =>
        Path.Combine(titleRoot, Sanitize(dirName));

    /// <summary>The SaveDataMemory blob shared by a title.</summary>
    public static string MemoryPath(string titleRoot) =>
        Path.Combine(titleRoot, "sce_sdmemory", "memory.dat");

    public static string ParamPath(string slotDir) =>
        Path.Combine(slotDir, "sce_sys", "param.json");

    public static string IconPath(string slotDir) =>
        Path.Combine(slotDir, "sce_sys", "icon0.png");

    /// <summary>
    /// Replaces characters that are invalid in a host path segment. Empty or
    /// all-invalid input collapses to "default" so a bad guest name can never
    /// escape the save root or produce an empty segment.
    /// </summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            buffer[i] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
        }

        var sanitized = new string(buffer).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    /// <summary>Reads a slot's metadata, or defaults if none has been written.</summary>
    public static SaveDataMetadata ReadMetadata(string slotDir)
    {
        var path = ParamPath(slotDir);
        if (File.Exists(path))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(File.ReadAllText(path), SaveDataMetadataContext.Default.SaveDataMetadata);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fall through to defaults on a corrupt or unreadable metadata file.
            }
        }

        return SaveDataMetadata.CreateDefault(Path.GetFileName(slotDir.TrimEnd(Path.DirectorySeparatorChar)));
    }

    /// <summary>Writes a slot's metadata, creating <c>sce_sys/</c> as needed.</summary>
    public static void WriteMetadata(string slotDir, SaveDataMetadata metadata)
    {
        var path = ParamPath(slotDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(metadata, SaveDataMetadataContext.Default.SaveDataMetadata));
    }
}

/// <summary>PS5 save-slot metadata surfaced by sceSaveDataGetParam / the save UI.</summary>
public sealed record SaveDataMetadata
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = "Saved Data";

    [JsonPropertyName("subTitle")]
    public string SubTitle { get; init; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = string.Empty;

    [JsonPropertyName("userParam")]
    public uint UserParam { get; init; }

    public static SaveDataMetadata CreateDefault(string dirName) =>
        new() { Title = string.IsNullOrWhiteSpace(dirName) ? "Saved Data" : dirName };
}

[JsonSerializable(typeof(SaveDataMetadata))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class SaveDataMetadataContext : JsonSerializerContext
{
}

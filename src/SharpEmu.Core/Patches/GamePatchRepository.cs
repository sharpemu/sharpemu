// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace SharpEmu.Core.Patches;

public sealed class GamePatchRepository
{
    public static string DefaultDirectory => Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "patches");

    public GamePatchRepository(string? directory = null) => DirectoryPath = directory ?? DefaultDirectory;

    public string DirectoryPath { get; }

    public IReadOnlyList<GamePatch> Load(string? titleId, Action<string>? warning = null)
    {
        var patches = new List<GamePatch>();
        if (string.IsNullOrWhiteSpace(titleId) || !Directory.Exists(DirectoryPath))
        {
            return patches;
        }

        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = File.OpenRead(path);
                foreach (var patch in Read(Path.GetFileName(path), stream, titleId))
                {
                    if (patch.Error is not null)
                    {
                        warning?.Invoke($"{Path.GetFileName(path)} / {patch.Name}: {patch.Error}");
                    }

                    if (patches.All(existing => existing.Id != patch.Id))
                    {
                        patches.Add(patch);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or FormatException)
            {
                warning?.Invoke($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return patches;
    }

    public static IReadOnlyList<GamePatch> Read(string fileName, Stream stream, string titleId)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 << 20,
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "Patch") throw new FormatException("The XML root must be Patch.");
        if (root.Element("TitleID")?.Elements("ID").Any(id =>
                string.Equals(id.Value.Trim(), titleId, StringComparison.OrdinalIgnoreCase)) != true)
            return [];

        return root.Elements("Metadata").Select(metadata => GamePatch.Parse(Path.GetFileName(fileName), metadata))
            .DistinctBy(patch => patch.Id).ToArray();
    }

    public HashSet<string> LoadEnabled(string titleId)
    {
        var path = SettingsPath(titleId);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []
            : [];
    }

    public void SetEnabled(string titleId, GamePatch patch, bool enabled)
    {
        if (enabled && patch.Error is not null)
        {
            throw new InvalidOperationException(patch.Error);
        }

        var selected = LoadEnabled(titleId);
        if (enabled) selected.Add(patch.Id);
        else selected.Remove(patch.Id);
        Directory.CreateDirectory(DirectoryPath);
        var path = SettingsPath(titleId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(selected.Order(StringComparer.Ordinal), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal IReadOnlyList<GamePatch> LoadActive(string? titleId, string? version, Action<string> warning)
    {
        if (string.IsNullOrWhiteSpace(titleId)) return [];
        try
        {
            var selected = LoadEnabled(titleId);
            if (selected.Count == 0) return [];
            var active = new List<GamePatch>();
            foreach (var patch in Load(titleId, warning))
            {
                if (patch.Error is not null || !selected.Contains(patch.Id)) continue;
                if (!patch.MatchesVersion(version))
                {
                    warning($"Skipped '{patch.Name}': requires {patch.AppVersion}, installed version is {version ?? "unknown"}.");
                    continue;
                }

                active.Add(patch);
            }

            return active;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            warning($"Could not load patch selections: {exception.Message}");
            return [];
        }
    }

    private string SettingsPath(string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId) || titleId.Length > 32 || !titleId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Invalid patch title ID.", nameof(titleId));
        }

        return Path.Combine(DirectoryPath, titleId.ToUpperInvariant() + ".json");
    }
}

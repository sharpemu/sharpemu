// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GameContent;

public readonly record struct GameMetadata(string? Title, string? TitleId, string? Version);

public static class GameMetadataReader
{
    public static GameMetadata Read(IReadOnlyGameFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        foreach (var path in new[] { "sce_sys/param.json", "param.json" })
        {
            if (!fileSystem.TryGetEntry(path, out var entry) || !entry.IsFile)
            {
                continue;
            }

            try
            {
                using var stream = fileSystem.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                return Read(document.RootElement);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        return default;
    }

    private static GameMetadata Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        var titleId = GetString(root, "titleId");
        var version = GetString(root, "contentVersion")
            ?? GetString(root, "masterVersion")
            ?? GetString(root, "targetContentVersion");
        JsonElement localized = default;
        if ((!root.TryGetProperty("localizedParameters", out localized) || localized.ValueKind != JsonValueKind.Object) &&
            root.TryGetProperty("disc", out var disc) && disc.ValueKind == JsonValueKind.Object)
        {
            disc.TryGetProperty("localizedParameters", out localized);
        }

        string? title = null;
        if (localized.ValueKind == JsonValueKind.Object)
        {
            var defaultLanguage = GetString(localized, "defaultLanguage");
            if (!string.IsNullOrWhiteSpace(defaultLanguage) &&
                localized.TryGetProperty(defaultLanguage, out var language) &&
                language.ValueKind == JsonValueKind.Object)
            {
                title = GetString(language, "titleName");
            }

            if (string.IsNullOrWhiteSpace(title) &&
                localized.TryGetProperty("en-US", out var english) &&
                english.ValueKind == JsonValueKind.Object)
            {
                title = GetString(english, "titleName");
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                foreach (var property in localized.EnumerateObject())
                {
                    var candidate = property.Value.ValueKind == JsonValueKind.Object
                        ? GetString(property.Value, "titleName")
                        : null;
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        title = candidate;
                        break;
                    }
                }
            }
        }

        return new GameMetadata(
            string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(titleId) ? null : titleId,
            string.IsNullOrWhiteSpace(version) ? null : version.Trim());
    }

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

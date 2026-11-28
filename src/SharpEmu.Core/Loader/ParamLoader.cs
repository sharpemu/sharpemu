// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Core;

namespace SharpEmu.Core.Loader;

public sealed record ParamLoader(
    string? TitleId,
    string? ContentId,
    string? ContentVersion,
    string? MasterVersion,
    string? TargetContentVersion,
    LocalizedParameters? LocalizedParameters,
    Disc? Disc
);

public sealed record LocalizedParameters(
    string? DefaultLanguage,
    Dictionary<string, LocalizedLanguage>? Languages
);

public sealed record LocalizedLanguage(string? TitleName);

public sealed record Disc(LocalizedParameters? LocalizedParameters);

public static class Ps5ParamJsonReader
{
    private const uint Ps5ProEnhancedFlag = 0x0040_0000;

    public static (string? Title, string? TitleId, string? Version, string? ConceptId) TryReadPs5Param(IFileSystem fs, string paramJsonPath) =>
        TryReadPs5Param(fs, paramJsonPath, out _);

    internal static (string? Title, string? TitleId, string? Version, string? ConceptId) TryReadPs5Param(
        IFileSystem fs, string paramJsonPath, out bool supportsPs5Pro)
    {
        supportsPs5Pro = false;
        if (!fs.Exists(paramJsonPath))
            return (null, null, null, null);

        if (!fs.TryReadAllBytes(paramJsonPath, out var data))
            return (null, null, null, null);

        return TryReadPs5Param(data, out supportsPs5Pro);
    }

    public static (string? Title, string? TitleId, string? Version, string? ConceptId) TryReadPs5Param(byte[] data) =>
        TryReadPs5Param(data, out _);

    internal static (string? Title, string? TitleId, string? Version, string? ConceptId) TryReadPs5Param(
        byte[] data, out bool supportsPs5Pro)
    {
        supportsPs5Pro = false;
        if (data == null || data.Length == 0)
            return (null, null, null, null);

        try
        {
            ReadOnlyMemory<byte> json = data;
            if (json.Span.StartsWith("\uFEFF"u8))
            {
                json = json[3..];
            }

            using var doc = JsonDocument.Parse(json);
            supportsPs5Pro = SupportsPs5Pro(doc.RootElement);
            return TryReadPs5Param(doc.RootElement);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }

    public static bool SupportsPs5Pro(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("attribute3", out var attribute) &&
        attribute.ValueKind == JsonValueKind.Number &&
        attribute.TryGetUInt32(out var value) && (value & Ps5ProEnhancedFlag) != 0;

    private static (string? Title, string? TitleId, string? Version, string? ConceptId) TryReadPs5Param(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (null, null, null, null);

        var titleId = GetString(root, "titleId");

        string? ver =
            GetString(root, "contentVersion")
            ?? GetString(root, "masterVersion")
            ?? GetString(root, "targetContentVersion");

        string? title = ExtractTitleName(root);

        // conceptId is shared by every regional release of one game (param.sfo has none).
        var conceptId = GetString(root, "conceptId");

        return (title, titleId, ver, conceptId);
    }

    private static string? ExtractTitleName(JsonElement root)
    {
        if ((!root.TryGetProperty("localizedParameters", out var lp) || lp.ValueKind != JsonValueKind.Object) &&
            root.TryGetProperty("disc", out var disc) && disc.ValueKind == JsonValueKind.Object)
        {
            disc.TryGetProperty("localizedParameters", out lp);
        }

        if (lp.ValueKind != JsonValueKind.Object)
            return null;

        var defLang = GetString(lp, "defaultLanguage");

        if (!string.IsNullOrEmpty(defLang))
        {
            if (lp.TryGetProperty(defLang, out var langObj) && langObj.ValueKind == JsonValueKind.Object)
            {
                var title = GetString(langObj, "titleName");
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        if (lp.TryGetProperty("en-US", out var en) && en.ValueKind == JsonValueKind.Object)
        {
            var title = GetString(en, "titleName");
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        foreach (var property in lp.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var title = GetString(property.Value, "titleName");
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

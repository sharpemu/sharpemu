// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text.Json;

namespace SharpEmu.Libs.Overlay;

public static class OverlayText
{
    public const string LanguageVariable = "SHARPEMU_UI_LANGUAGE";
    public const string StringsVariable = "SHARPEMU_UI_STRINGS";
    public const string KeyPrefix = "Overlay.";

    private static readonly Lazy<Dictionary<string, string>> Strings = new(LoadStrings);
    private static readonly Lazy<CultureInfo> LoadedCulture = new(LoadCulture);

    public static CultureInfo Culture => LoadedCulture.Value;

    public static string Get(string key, string fallback) =>
        Strings.Value.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    public static CultureInfo CultureFor(string? code)
    {
        var name = code?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "br" => "pt-BR",
            "pt" => "pt-PT",
            "dk" => "da-DK",
            "ee" => "et-EE",
            "zh" => "zh-CN",
            var other => other,
        };
        if (name is null)
        {
            return CultureInfo.CurrentCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentCulture;
        }
    }

    public static string FormatDate(DateTime utc) => FormatDate(utc, Culture);

    public static string FormatDate(DateTime utc, CultureInfo culture)
    {
        var local = utc.ToLocalTime();
        if (culture.TextInfo.IsRightToLeft)
        {
            return local.ToString("yyyy/MM/dd  HH:mm", CultureInfo.InvariantCulture);
        }

        return local.ToString(culture.DateTimeFormat.ShortDatePattern, culture) + "  " +
               local.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
    }

    public static string FormatSize(long bytes) => FormatSize(bytes, Culture);

    public static string FormatSize(long bytes, CultureInfo culture)
    {
        if (culture.TextInfo.IsRightToLeft)
        {
            culture = CultureInfo.InvariantCulture;
        }

        if (bytes < 1024)
        {
            return string.Create(culture, $"{bytes} B");
        }

        if (bytes < 1024 * 1024)
        {
            return string.Create(culture, $"{bytes / 1024.0:0.#} KB");
        }

        return bytes < 1024L * 1024 * 1024
            ? string.Create(culture, $"{bytes / (1024.0 * 1024):0.#} MB")
            : string.Create(culture, $"{bytes / (1024.0 * 1024 * 1024):0.##} GB");
    }

    private static Dictionary<string, string> LoadStrings()
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        var configured = Environment.GetEnvironmentVariable(StringsVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return strings;
        }

        try
        {
            using var document = JsonDocument.Parse(configured);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return strings;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                {
                    strings[property.Name] = value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return strings;
    }

    private static CultureInfo LoadCulture() => CultureFor(Environment.GetEnvironmentVariable(LanguageVariable));
}

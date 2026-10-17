// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Np;

public static class TrophyLanguage
{
    private static readonly string[] SystemLanguageTags =
    [
        "ja-JP", "en-US", "fr-FR", "es-ES", "de-DE", "it-IT", "nl-NL", "pt-PT", "ru-RU", "ko-KR",
        "zh-Hant", "zh-Hans", "fi-FI", "sv-SE", "da-DK", "no-NO", "pl-PL", "pt-BR", "en-GB", "tr-TR",
        "es-419", "ar-AE", "fr-CA", "cs-CZ", "hu-HU", "el-GR", "ro-RO", "th-TH", "vi-VN", "id-ID",
        "uk-UA",
    ];

    private static readonly string[] ConsoleLanguageNames =
    [
        "Japanese", "EnglishUS", "French", "SpanishSpain", "German", "Italian", "Dutch", "PortuguesePortugal", "Russian", "Korean",
        "ChineseTraditional", "ChineseSimplified", "Finnish", "Swedish", "Danish", "Norwegian", "Polish", "PortugueseBrazil", "EnglishUK", "Turkish",
        "SpanishLatinAmerica", "Arabic", "FrenchCanada", "Czech", "Hungarian", "Greek", "Romanian", "Thai", "Vietnamese", "Indonesian",
        "Ukrainian",
    ];

    private static readonly Dictionary<string, string> InterfaceTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ar"] = "ar-AE",
        ["br"] = "pt-BR",
        ["de"] = "de-DE",
        ["dk"] = "da-DK",
        ["ee"] = "et-EE",
        ["en"] = "en-US",
        ["es"] = "es-ES",
        ["fr"] = "fr-FR",
        ["hu"] = "hu-HU",
        ["it"] = "it-IT",
        ["ja"] = "ja-JP",
        ["ko"] = "ko-KR",
        ["nl"] = "nl-NL",
        ["pl"] = "pl-PL",
        ["pt"] = "pt-PT",
        ["ru"] = "ru-RU",
        ["tr"] = "tr-TR",
    };

    public static string FromSystemLanguage(int language) =>
        language >= 0 && language < SystemLanguageTags.Length ? SystemLanguageTags[language] : "en-US";

    public static string FromConsoleLanguage(string? name)
    {
        var index = Array.FindIndex(ConsoleLanguageNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return FromSystemLanguage(index);
    }

    public static string FromInterfaceLanguage(string? code) =>
        code is not null && InterfaceTags.TryGetValue(code, out var tag) ? tag : code ?? "en-US";

    internal static string? Resolve(IReadOnlyCollection<string> available, string? preferred)
    {
        if (string.IsNullOrEmpty(preferred) || available.Count == 0)
        {
            return null;
        }

        var exact = available.FirstOrDefault(tag => string.Equals(tag, preferred, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var primary = Primary(preferred);
        return available
            .Where(tag => string.Equals(Primary(tag), primary, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string Primary(string tag)
    {
        var separator = tag.IndexOf('-');
        return separator < 0 ? tag : tag[..separator];
    }
}

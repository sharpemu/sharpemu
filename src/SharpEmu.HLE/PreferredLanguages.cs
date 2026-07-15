// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.HLE;

public static class PreferredLanguages
{
    public const int DefaultLanguage = 1;
    private const int MaxLanguageId = 29;

    private static readonly int[] DefaultLanguages = [DefaultLanguage];
    private static int[] _languages = DefaultLanguages;

    public static IReadOnlyList<int> Current => Volatile.Read(ref _languages);

    public static void Configure(IEnumerable<int>? languages)
    {
        var normalized = languages?
            .Where(language => language is >= 0 and <= MaxLanguageId)
            .Distinct()
            .ToArray();

        if (normalized is not { Length: > 0 })
        {
            normalized = Detect();
        }

        Volatile.Write(ref _languages, normalized is { Length: > 0 } ? normalized : DefaultLanguages);
    }

    public static bool TryParse(string value, out IReadOnlyList<int> languages)
    {
        var parsed = new List<int>();
        foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseLanguage(token, out var language))
            {
                languages = Array.Empty<int>();
                return false;
            }

            if (!parsed.Contains(language))
            {
                parsed.Add(language);
            }
        }

        languages = parsed;
        return parsed.Count > 0;
    }

    public static ulong ToLanguageMask()
    {
        var mask = 0UL;
        foreach (var language in Current)
        {
            mask |= 1UL << (63 - language);
        }

        return mask;
    }

    public static int GetPrimaryLanguage()
    {
        return Current[0];
    }

    public static string ToLanguageTag(int language)
    {
        return language switch
        {
            0 => "ja-JP",
            1 => "en-US",
            2 => "fr-FR",
            3 => "es-ES",
            4 => "de-DE",
            5 => "it-IT",
            6 => "nl-NL",
            7 => "pt-PT",
            8 => "ru-RU",
            9 => "ko-KR",
            10 => "zh-Hant",
            11 => "zh-Hans",
            12 => "fi-FI",
            13 => "sv-SE",
            14 => "da-DK",
            15 => "nb-NO",
            16 => "pl-PL",
            17 => "pt-BR",
            18 => "en-GB",
            19 => "tr-TR",
            20 => "es-419",
            21 => "ar-SA",
            22 => "fr-CA",
            23 => "cs-CZ",
            24 => "hu-HU",
            25 => "el-GR",
            26 => "ro-RO",
            27 => "th-TH",
            28 => "vi-VN",
            29 => "id-ID",
            _ => "en-US",
        };
    }

    private static int[] Detect()
    {
        var detected = new List<int>();
        AddCulture(detected, CultureInfo.CurrentUICulture);
        AddCulture(detected, CultureInfo.CurrentCulture);
        return detected.Count > 0 ? detected.ToArray() : DefaultLanguages;
    }

    private static void AddCulture(List<int> languages, CultureInfo culture)
    {
        if (TryParseLanguage(culture.Name, out var language) && !languages.Contains(language))
        {
            languages.Add(language);
        }
    }

    private static bool TryParseLanguage(string value, out int language)
    {
        var normalized = value.Trim().Replace('_', '-').ToLowerInvariant();
        language = normalized switch
        {
            "ja" or "ja-jp" or "jp" => 0,
            "en" or "en-us" => 1,
            "fr" or "fr-fr" => 2,
            "es" or "es-es" => 3,
            "de" or "de-de" => 4,
            "it" or "it-it" => 5,
            "nl" or "nl-nl" => 6,
            "pt" or "pt-pt" => 7,
            "ru" or "ru-ru" => 8,
            "ko" or "ko-kr" => 9,
            "zh-hant" or "zh-tw" => 10,
            "zh-hans" or "zh-cn" => 11,
            "fi" or "fi-fi" => 12,
            "sv" or "sv-se" => 13,
            "da" or "da-dk" => 14,
            "no" or "nb" or "no-no" => 15,
            "pl" or "pl-pl" => 16,
            "pt-br" => 17,
            "en-gb" => 18,
            "tr" or "tr-tr" => 19,
            "es-la" or "es-419" => 20,
            "ar" or "ar-sa" => 21,
            "fr-ca" => 22,
            "cs" or "cs-cz" => 23,
            "hu" or "hu-hu" => 24,
            "el" or "el-gr" => 25,
            "ro" or "ro-ro" => 26,
            "th" or "th-th" => 27,
            "vi" or "vi-vn" => 28,
            "id" or "id-id" => 29,
            _ => -1,
        };

        return language >= 0;
    }
}

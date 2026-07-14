// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GUI;

/// <summary>
/// Loads UI strings for the launcher. Every language ships embedded in the
/// assembly (see SharpEmu.GUI.csproj) so a release build is fully
/// self-contained; an optional Languages/&lt;code&gt;.json file next to the
/// executable overrides the embedded copy for that code, so a translation
/// fix or a brand-new language never needs a rebuild.
/// </summary>
public sealed class Localization
{
    public static Localization Instance { get; } = new();

    public sealed record LanguageInfo(string Code, string NativeName);

    private const string EmbeddedResourcePrefix = "Languages.";
    private const string EmbeddedResourceSuffix = ".json";

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallbackStrings = new();


    private Localization()
    {
    }

    /// <summary>Directory holding optional *.json language overrides, next to the executable.</summary>
    public static string LanguagesDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public string CurrentCode { get; private set; } = "en";

    public string Get(string key)
    {
        if (_strings.TryGetValue(key, out var value))
            return value;

        if (_fallbackStrings.TryGetValue(key, out var fallbackValue))
            return fallbackValue;

        return key;
    }

    public string Format(string key, params object?[] args) => string.Format(Get(key), args);

    /// <summary>
    /// Languages available either embedded in the binary or as a loose
    /// override file, sorted by code. A loose file's declared name wins when
    /// the same code exists in both places.
    /// </summary>
    public List<LanguageInfo> DiscoverLanguages()
    {
        var languages = new Dictionary<string, LanguageInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in EmbeddedLanguageCodes())
        {
            using var stream = OpenEmbeddedLanguageStream(code);
            if (stream is not null)
            {
                languages[code] = new LanguageInfo(code, ReadLanguageName(stream) ?? code);
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(LanguagesDirectory, "*.json"))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                using var stream = File.OpenRead(file);
                languages[code] = new LanguageInfo(code, ReadLanguageName(stream) ?? code);
            }
        }
        catch (Exception)
        {
            // No loose Languages directory: the embedded languages still stand.
        }

        var result = languages.Values.ToList();
        result.Sort((a, b) => string.CompareOrdinal(a.Code, b.Code));
        return result;
    }

    /// <summary>Loads a language by code (e.g. "en"): a loose override file first, then the embedded copy.</summary>
    /// english is the fallback language
    public void Load(string code)
    {
        if (_fallbackStrings.Count == 0 && !string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryLoadLooseFile("en", out var fallback) && !TryLoadEmbedded("en", out fallback))
            {
                fallback = new Dictionary<string, string>();
            }
            _fallbackStrings = fallback;
        }
        else if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
        {
            if (TryLoadLooseFile("en", out var enDict) || TryLoadEmbedded("en", out enDict))
            {
                _strings = enDict;
                _fallbackStrings = enDict;
            }
            else
            {
                _strings = new Dictionary<string, string>();
                _fallbackStrings = new Dictionary<string, string>();
            }
            CurrentCode = "en";
            return;
        }

        // Load the requested language
        if (TryLoadLooseFile(code, out var loaded) || TryLoadEmbedded(code, out loaded))
        {
            _strings = loaded;
        }
        else
        {
            if (_fallbackStrings.Count > 0)
                _strings = new Dictionary<string, string>(_fallbackStrings);
            else
                _strings = new Dictionary<string, string>();
        }
        CurrentCode = code;
    }

    private static IEnumerable<string> EmbeddedLanguageCodes()
    {
        foreach (var name in typeof(Localization).Assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(EmbeddedResourcePrefix, StringComparison.Ordinal) &&
                name.EndsWith(EmbeddedResourceSuffix, StringComparison.Ordinal))
            {
                yield return name[EmbeddedResourcePrefix.Length..^EmbeddedResourceSuffix.Length];
            }
        }
    }

    private static Stream? OpenEmbeddedLanguageStream(string code) =>
        typeof(Localization).Assembly.GetManifestResourceStream($"{EmbeddedResourcePrefix}{code}{EmbeddedResourceSuffix}");

    private static string? ReadLanguageName(Stream stream)
    {
        try
        {
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("_languageName", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }
        }
        catch (Exception)
        {
            // Malformed file: fall back to the code as its own display name.
        }

        return null;
    }

    private bool TryLoadLooseFile(string code)
    {
        try
        {
            var path = Path.Combine(LanguagesDirectory, $"{code}.json");
            return File.Exists(path) && TryLoad(code, File.ReadAllText(path));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryLoadEmbedded(string code)
    {
        try
        {
            using var stream = OpenEmbeddedLanguageStream(code);
            if (stream is null)
            {
                return false;
            }

            using var reader = new StreamReader(stream);
            return TryLoad(code, reader.ReadToEnd());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryLoadLooseFile(string code, out Dictionary<string, string> result)
    {
        result = new Dictionary<string, string>();
        try
        {
            var path = Path.Combine(LanguagesDirectory, $"{code}.json");
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            return TryLoad(json, out result);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryLoadEmbedded(string code, out Dictionary<string, string> result)
    {
        result = new Dictionary<string, string>();
        try
        {
            using var stream = OpenEmbeddedLanguageStream(code);
            if (stream is null)
                return false;

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return TryLoad(json, out result);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryLoad(string json, out Dictionary<string, string> result)
    {
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (loaded is null)
        {
            result = new Dictionary<string, string>();
            return false;
        }

        result = loaded;
        return true;
    }
    
    private bool TryLoad(string code, string json)
    {
        if (TryLoad(json, out var dict))
        {
            _strings = dict;
            CurrentCode = code;
            return true;
        }
        return false;
    }
}
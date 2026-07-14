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

    private Localization()
    {
    }

    /// <summary>Directory holding optional *.json language overrides, next to the executable.</summary>
    public static string LanguagesDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public string CurrentCode { get; private set; } = "en";

    public string Get(string key) => _strings.TryGetValue(key, out var value) ? value : key;

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
    public void Load(string code)
    {
        if (!TryLoadLooseFile(code) && !TryLoadEmbedded(code) &&
            !string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
        {
            _ = TryLoadLooseFile("en") || TryLoadEmbedded("en");
        }
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

    private bool TryLoad(string code, string json)
    {
        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (loaded is null)
        {
            return false;
        }

        _strings = loaded;
        CurrentCode = code;
        return true;
    }
}

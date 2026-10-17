// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SharpEmu.GUI;

public sealed record LanguageInfo(string Code, string NativeName);

/// <summary>
/// Loads UI strings for the launcher. Every language ships embedded in the
/// assembly (see SharpEmu.GUI.csproj) so a release build is fully
/// self-contained; an optional Languages/&lt;code&gt;.json file next to the
/// executable overrides the embedded copy for that code, so a translation
/// fix or a brand-new language never needs a rebuild.
/// </summary>
public sealed class Localization : INotifyPropertyChanged
{
    public static Localization Instance { get; } = new();

    private const string EmbeddedResourcePrefix = "Languages.";
    private const string EmbeddedResourceSuffix = ".json";
    private const string IndexerPropertyName = "Item";

    private readonly string _languagesDirectory;
    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallbackStrings = new();
    private string _currentCode = "en";

    private Localization() : this(LanguagesDirectory)
    {
    }

    internal Localization(string languagesDirectory)
    {
        _languagesDirectory = languagesDirectory;
    }

    /// <summary>Directory holding optional *.json language overrides, next to the executable.</summary>
    public static string LanguagesDirectory => Path.Combine(AppContext.BaseDirectory, "Languages");

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Exposes localized strings to XAML bindings by key.</summary>
    public string this[string key] => Get(key);

    public string CurrentCode
    {
        get => _currentCode;
        private set
        {
            if (_currentCode == value)
            {
                return;
            }

            _currentCode = value;
            OnPropertyChanged();
        }
    }

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
            foreach (var file in Directory.EnumerateFiles(_languagesDirectory, "*.json"))
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

    /// <summary>
    /// Loads a language by code. Loose files overlay the embedded language,
    /// while English supplies values missing from the selected language.
    /// </summary>
    public void Load(string code)
    {
        _fallbackStrings = LoadMergedLanguage("en");
        _strings = string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)
            ? _fallbackStrings
            : LoadMergedLanguage(code);

        CurrentCode = code;
        OnPropertyChanged(IndexerPropertyName);
    }

    private Dictionary<string, string> LoadMergedLanguage(string code)
    {
        var merged = TryLoadEmbedded(code, out var embedded)
            ? embedded
            : new Dictionary<string, string>();

        if (TryLoadLooseFile(code, out var loose))
        {
            foreach (var (key, value) in loose)
            {
                merged[key] = value;
            }
        }

        return merged;
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

    private bool TryLoadLooseFile(string code, out Dictionary<string, string> result)
    {
        result = new Dictionary<string, string>();
        try
        {
            var path = Path.Combine(_languagesDirectory, $"{code}.json");
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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

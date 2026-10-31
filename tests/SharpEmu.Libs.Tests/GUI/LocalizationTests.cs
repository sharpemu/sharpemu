// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Data;
using SharpEmu.GUI;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class LocalizationTests : IDisposable
{
    private readonly string _languagesDirectory = Path.Combine(
        Path.GetTempPath(),
        $"sharpemu-localization-{Guid.NewGuid():N}");

    public LocalizationTests()
    {
        Directory.CreateDirectory(_languagesDirectory);
    }

    [Fact]
    public void Load_RaisesNotificationsForCurrentCodeAndIndexer()
    {
        var localization = new Localization(_languagesDirectory);
        var changedProperties = new List<string?>();
        localization.PropertyChanged += (_, args) =>
            changedProperties.Add(args.PropertyName);

        localization.Load("ru");

        Assert.Equal("ru", localization.CurrentCode);
        Assert.Contains(nameof(Localization.CurrentCode), changedProperties);
        Assert.Contains("Item", changedProperties);
    }

    [Fact]
    public void IndexerBinding_UpdatesWhenLanguageChanges()
    {
        var localization = new Localization(_languagesDirectory);
        localization.Load("en");
        var englishLibraryLabel = localization.Get("Page.Library");
        localization.Load("ru");
        var russianLibraryLabel = localization.Get("Page.Library");
        Assert.NotEqual(englishLibraryLabel, russianLibraryLabel);

        localization.Load("en");
        var text = new TextBlock();
        text.Bind(
            TextBlock.TextProperty,
            new Binding("[Page.Library]") { Source = localization });

        Assert.Equal(englishLibraryLabel, text.Text);

        localization.Load("ru");

        Assert.Equal(russianLibraryLabel, text.Text);
    }

    [Fact]
    public void LocalizedChoice_UpdatesLabelWithoutReplacingStableValue()
    {
        var localization = new Localization(_languagesDirectory);
        var choice = LocalizedChoice.FromKey(
            "Fit",
            "Options.Scaling.Fit");
        var changedProperties = new List<string?>();
        choice.PropertyChanged += (_, args) =>
            changedProperties.Add(args.PropertyName);

        localization.Load("en");
        choice.Refresh(localization);
        var englishLabel = choice.Label;

        localization.Load("ru");
        choice.Refresh(localization);

        Assert.Equal("Fit", choice.Value);
        Assert.NotEqual(englishLabel, choice.Label);
        Assert.Equal(localization.Get("Options.Scaling.Fit"), choice.Label);
        Assert.Contains(nameof(LocalizedChoice.Label), changedProperties);
    }

    [Fact]
    public void DiscordButtonContentUpdatesWhenLanguageChanges()
    {
        var localization = new Localization(_languagesDirectory);
        localization.Load("en");
        var englishLabel = localization.Get("About.DiscordButton");
        var button = new Button();
        button.Bind(
            Button.ContentProperty,
            new Binding("[About.DiscordButton]") { Source = localization });

        Assert.Equal(englishLabel, button.Content);

        localization.Load("ru");

        Assert.NotEqual(englishLabel, localization.Get("About.DiscordButton"));
        Assert.Equal(localization.Get("About.DiscordButton"), button.Content);
    }

    [Fact]
    public void EmbeddedLanguagesProvideDiscordButtonLabels()
    {
        var assembly = typeof(Localization).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name =>
                name.StartsWith("Languages.", StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(resourceNames);

        foreach (var resourceName in resourceNames)
        {
            var language = ReadEmbeddedLanguage(assembly, resourceName);
            Assert.True(language.TryGetValue("About.DiscordButton", out var label),
                $"{resourceName} has no Discord button label.");
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.DoesNotContain("About.DiscordComingSoon", language.Keys);
        }
    }

    [Fact]
    public void ComboBoxSelection_KeepsLiveLocalizedChoiceAsSelectionBoxItem()
    {
        var localization = new Localization(_languagesDirectory);
        var choice = LocalizedChoice.FromKey(
            "Fit",
            "Options.Scaling.Fit");
        localization.Load("en");
        choice.Refresh(localization);
        var comboBox = new ComboBox
        {
            ItemsSource = new[] { choice },
            SelectedIndex = 0,
        };

        Assert.Same(choice, comboBox.SelectionBoxItem);

        localization.Load("ru");
        choice.Refresh(localization);

        Assert.Same(choice, comboBox.SelectionBoxItem);
        Assert.Equal(localization.Get("Options.Scaling.Fit"), choice.Label);
    }

    [Fact]
    public void EmbeddedLanguages_ContainEveryEnglishOptionsKey()
    {
        var assembly = typeof(Localization).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name =>
                name.StartsWith("Languages.", StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal))
            .Order()
            .ToArray();
        var english = ReadEmbeddedLanguage(assembly, "Languages.en.json");
        var optionKeys = english.Keys
            .Where(key => key.StartsWith("Options.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var resourceName in resourceNames)
        {
            var language = ReadEmbeddedLanguage(assembly, resourceName);
            var missing = optionKeys
                .Where(key => !language.ContainsKey(key))
                .Order()
                .ToArray();
            var empty = optionKeys
                .Where(key =>
                    language.TryGetValue(key, out var value) &&
                    string.IsNullOrWhiteSpace(value))
                .Order()
                .ToArray();

            Assert.True(
                missing.Length == 0,
                $"{resourceName} is missing option keys: {string.Join(", ", missing)}");
            Assert.True(
                empty.Length == 0,
                $"{resourceName} has empty option values: {string.Join(", ", empty)}");
        }
    }

    // A catalog switch without strings shows its raw key in Game options.
    [Fact]
    public void EmbeddedLanguages_LabelEveryEnvironmentSetting()
    {
        var assembly = typeof(Localization).Assembly;
        var keys = new List<string>();
        foreach (var setting in EnvironmentSettings.All)
        {
            keys.Add($"Setting.{setting.Id}.Label");
            keys.Add($"Setting.{setting.Id}.Desc");
            keys.AddRange((setting.Choices ?? [])
                .Where(choice => choice.Label is null)
                .Select(choice => $"Setting.{setting.Id}.{choice.Value}"));
        }

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name =>
                name.StartsWith("Languages.", StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal));
        foreach (var resourceName in resourceNames)
        {
            var language = ReadEmbeddedLanguage(assembly, resourceName);
            var missing = keys
                .Where(key => !language.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                .ToArray();
            Assert.True(
                missing.Length == 0,
                $"{resourceName} has no text for: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void EmbeddedLanguages_ContainEveryPatchKeyAndPreserveFormatArguments()
    {
        var assembly = typeof(Localization).Assembly;
        var english = ReadEmbeddedLanguage(assembly, "Languages.en.json");
        var patchKeys = english.Keys.Where(key => key == "Library.Context.Patches" ||
            key.StartsWith("Library.Patches.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(patchKeys);

        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name =>
            name.StartsWith("Languages.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var language = ReadEmbeddedLanguage(assembly, resourceName);
            foreach (var key in patchKeys)
            {
                Assert.True(language.TryGetValue(key, out var value), $"{resourceName} is missing {key}.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{resourceName} has an empty value for {key}.");
                var expected = Regex.Matches(english[key], @"\{\d+\}").Select(match => match.Value).Order().ToArray();
                var actual = Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).Order().ToArray();
                Assert.True(expected.SequenceEqual(actual), $"{resourceName} has different format arguments for {key}.");
                _ = string.Format(CultureInfo.InvariantCulture, value, "1.4.0", 10);
            }
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void LooseLanguageFile_OverridesEmbeddedValuesWithoutReplacingThem(
        string languageCode)
    {
        var localization = new Localization(_languagesDirectory);
        localization.Load(languageCode);
        var embeddedOptionsLabel = localization.Get("Page.Options");
        File.WriteAllText(
            Path.Combine(_languagesDirectory, $"{languageCode}.json"),
            $$"""
            {
              "_languageName": "Test {{languageCode}}",
              "Page.Library": "Custom library"
            }
            """);

        localization.Load(languageCode);

        Assert.Equal("Custom library", localization.Get("Page.Library"));
        Assert.Equal(embeddedOptionsLabel, localization.Get("Page.Options"));
    }

    [Fact]
    public void MissingLanguage_FallsBackToEnglish()
    {
        var localization = new Localization(_languagesDirectory);
        localization.Load("en");
        var englishLibraryLabel = localization.Get("Page.Library");

        localization.Load("missing");

        Assert.Equal("missing", localization.CurrentCode);
        Assert.Equal(englishLibraryLabel, localization.Get("Page.Library"));
    }

    public void Dispose()
    {
        Directory.Delete(_languagesDirectory, recursive: true);
    }

    private static Dictionary<string, string> ReadEmbeddedLanguage(
        System.Reflection.Assembly assembly,
        string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"Could not parse {resourceName}.");
    }
}

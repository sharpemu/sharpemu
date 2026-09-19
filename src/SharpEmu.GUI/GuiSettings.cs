// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GUI;

public sealed class GuiSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public List<string> GameFolders { get; set; } = new();

    /// <summary>Eboot paths hidden from the library via "Remove from library".</summary>
    public List<string> ExcludedGames { get; set; } = new();

    public string LogLevel { get; set; } = "Info";

    public int ImportTraceLimit { get; set; }

    public bool StrictDynlibResolution { get; set; }

    /// <summary>
    /// Mirror emulator output to user/logs/&lt;titleId&gt;-&lt;timestamp&gt;.log, if <see cref="LogFilePath"/> is null.
    /// </summary>
    public bool LogToFile { get; set; }

    /// <summary>If <see cref="LogToFile"/> is true it logs to this file path.</summary>
    public string? LogFilePath { get; set; }

    /// <summary> 
    /// If <see cref="OverrideLogFile"/> is false it appends &lt;titleId&gt;-&lt;timestamp&gt; to the filename specified by 
    /// <see cref="LogFilePath"/>. Otherwise it uses the exact filename from <see cref="LogFilePath"/>
    /// </summary>
    public bool OverrideLogFile { get; set; }

    /// <summary>Loop the selected game's sce_sys/snd0.at9 preview music.</summary>
    public bool PlayTitleMusic { get; set; } = true;

    public string LibraryLayout { get; set; } = "Carousel";

    public string? EmulatorPath { get; set; }

    /// <summary>UI language, matching a file code under Languages/ (e.g. "en", "tr").</summary>
    public string Language { get; set; } = "en";

    /// <summary>Default text-entry profile exposed to games.</summary>
    public string DefaultProfile { get; set; } = "Sharp";

    /// <summary>Publish launcher/game status to Discord Rich Presence.</summary>
    public bool DiscordRichPresence { get; set; } = true;

    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Whether an available update may open its confirmation window at startup.</summary>
    public bool ShowUpdateNotifications { get; set; } = true;

    public string WindowMode { get; set; } = "Windowed";

    public string Resolution { get; set; } = "1920x1080";

    public int DisplayIndex { get; set; }

    public int RefreshRate { get; set; }

    public string ScalingMode { get; set; } = "Fit";

    public bool VSync { get; set; } = true;

    public string HdrMode { get; set; } = "Auto";

    /// <summary>Names of SHARPEMU_* switches set to "1" in the emulator's environment at launch.</summary>
    public List<string> EnvironmentToggles { get; set; } = new();

    /// <summary>Internal render resolution scale (1.0 = native, 0.5 = half).</summary>
    public double RenderResolutionScale { get; set; } = 1.0;

    /// <summary>
    /// Discord application ID used for Rich Presence; the default is the
    /// SharpEmu application. Override to rebrand what Discord shows as
    /// "Playing …" (register at discord.com/developers/applications).
    /// </summary>
    public string DiscordClientId { get; set; } = "1525606762248540221";

    // The emulator is portable and keeps its data next to the executable;
    // the GUI follows the same convention.
    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "gui-settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return NormalizeFromJson(json);
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings fall back to defaults.
        }

        return new GuiSettings();
    }

    /// <summary>
    /// Deserializes settings and normalizes null references and null or empty list
    /// entries introduced by JSON. Empty scalar strings remain unchanged.
    /// </summary>
    internal static GuiSettings NormalizeFromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<GuiSettings>(json, SerializerOptions) ?? new GuiSettings();

        settings.GameFolders = FilterNullOrEmpty(settings.GameFolders);
        settings.ExcludedGames = FilterNullOrEmpty(settings.ExcludedGames);
        settings.EnvironmentToggles = FilterNullOrEmpty(settings.EnvironmentToggles);
        settings.LogLevel ??= "Info";
        settings.Language ??= "en";
        var legacyProfile = settings.EnvironmentToggles
            .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(parts =>
                parts.Length == 2 &&
                string.Equals(parts[0], "SHARPEMU_DEFAULT_PROFILE", StringComparison.OrdinalIgnoreCase));
        settings.EnvironmentToggles.RemoveAll(entry =>
            string.Equals(
                entry.Split('=', 2, StringSplitOptions.TrimEntries)[0],
                "SHARPEMU_DEFAULT_PROFILE",
                StringComparison.OrdinalIgnoreCase));
        settings.DefaultProfile = NormalizeDefaultProfile(
            legacyProfile is { Length: 2 } ? legacyProfile[1] : settings.DefaultProfile);
        settings.DiscordClientId ??= "1525606762248540221";
        if (settings.RenderResolutionScale <= 0 || settings.RenderResolutionScale > 2.0)
        {
            settings.RenderResolutionScale = 1.0;
        }
        settings.LibraryLayout = NormalizeChoice(settings.LibraryLayout, "Carousel", "Grid");
        settings.WindowMode = NormalizeChoice(settings.WindowMode, "Windowed", "Borderless", "Exclusive");
        settings.Resolution = NormalizeResolution(settings.Resolution);
        settings.ScalingMode = NormalizeChoice(settings.ScalingMode, "Fit", "Cover", "Stretch", "Integer");
        settings.HdrMode = NormalizeChoice(settings.HdrMode, "Auto", "On", "Off");
        settings.DisplayIndex = Math.Max(0, settings.DisplayIndex);
        settings.RefreshRate = Math.Clamp(settings.RefreshRate, 0, 1000);

        return settings;
    }

    // JSON can populate non-nullable lists with null references and entries.
    private static List<string> FilterNullOrEmpty(List<string>? source)
    {
        if (source is null)
        {
            return [];
        }

        return source.Where(entry => !string.IsNullOrEmpty(entry)).ToList();
    }

    private static string NormalizeChoice(string? value, string fallback, params string[] choices) =>
        choices.Prepend(fallback).FirstOrDefault(
            choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private static string NormalizeResolution(string? value)
    {
        if (!HostDisplayOptions.TryParseResolution(value, out var width, out var height))
        {
            return "1920x1080";
        }

        return $"{width}x{height}";
    }

    internal static string NormalizeDefaultProfile(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? "Sharp" : trimmed;
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception)
        {
            // Settings persistence is best-effort.
        }
    }
}

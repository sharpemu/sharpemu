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

    public string? EmulatorPath { get; set; }

    /// <summary>UI language, matching a file code under Languages/ (e.g. "en", "tr").</summary>
    public string Language { get; set; } = "en";

    /// <summary>Publish launcher/game status to Discord Rich Presence.</summary>
    public bool DiscordRichPresence { get; set; } = true;

    public bool CheckForUpdatesOnStartup { get; set; } = true;

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
        settings.DiscordClientId ??= "1525606762248540221";
        if (settings.RenderResolutionScale <= 0 || settings.RenderResolutionScale > 2.0)
        {
            settings.RenderResolutionScale = 1.0;
        }

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

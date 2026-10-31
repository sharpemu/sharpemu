// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.GnmMode.Libraries.Np;

namespace SharpEmu.GUI;

/// <summary>The platform-neutral launch contract shared by the launcher and command line.</summary>
public sealed record LaunchConfiguration(EffectiveLaunchSettings Settings, IReadOnlyList<string> CustomEnvironment)
{
    public static IReadOnlyList<string> SwitchVariables { get; } =
    [
        "SHARPEMU_PROFILE_PERFORMANCE", "SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE",
        StrictComputeSettings.VariableName, "SHARPEMU_BTHID_UNAVAILABLE",
        "SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", "SHARPEMU_WRITABLE_APP0", "SHARPEMU_VK_VALIDATION",
        "SHARPEMU_DUMP_SPIRV", "SHARPEMU_SKIP_RT", "SHARPEMU_LOG_DIRECT_MEMORY", "SHARPEMU_LOG_IO",
        "SHARPEMU_LOG_NP", "SHARPEMU_RENDERDOC", "SHARPEMU_VK_DISABLE_IMPLICITS", "SHARPEMU_CRASH_CAPTURE",
    ];

    /// <summary>
    /// A game's launch, later layers winning: built-in defaults, the settings for every game,
    /// the game's own settings, then custom variables for every game and the game's own.
    /// </summary>
    public static LaunchConfiguration Load(string gamePath, string? titleId)
    {
        SettingsSplitMigration.MigrateIfNeeded();
        var game = PerGameSettings.Load(PerGameSettings.KeyFor(titleId, gamePath));
        return new(EffectiveLaunchSettings.Resolve(PerGameSettings.LoadGlobal(), game), CustomEnvironmentVariables.Merge(
            GlobalCustomEnvironmentSettings.Load(), game?.CustomEnvironmentVariables));
    }

    /// <summary>Adds variables for this launch only; they apply after every saved setting.</summary>
    public LaunchConfiguration WithOneOff(IReadOnlyList<string>? oneOff) =>
        oneOff is { Count: > 0 }
            ? this with { CustomEnvironment = CustomEnvironmentVariables.Merge(CustomEnvironment, oneOff) }
            : this;

    /// <summary>Default text of the on-screen keyboard; machine-wide, from the launcher settings.</summary>
    public const string DefaultProfileVariable = "SHARPEMU_DEFAULT_PROFILE";

    // Defaults stay sparse in the settings file, but are explicit at launch so a launcher's
    // inherited shell variables cannot silently change a game's switches. Null removes the
    // variable, which restores a choice the emulator makes itself (for example where the
    // Vulkan pipeline cache is kept).
    public IReadOnlyDictionary<string, string?> BuildEnvironment(GuiSettings launcher)
    {
        var entries = Settings.EnvironmentToggles;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SwitchVariables)
        {
            values[name] = EnvironmentOverrides.IsSet(entries, name) ? "1" : "0";
        }
        values[StrictComputeSettings.VariableName] = StrictComputeSettings.GetLaunchValue(entries);
        values[RayTracingSkipSettings.VariableName] = RayTracingSkipSettings.GetLaunchValue(entries);
        foreach (var setting in EnvironmentSettings.All)
        {
            if (setting.Kind == EnvironmentSettingKind.Toggle)
                values[setting.Variable] = EnvironmentSettings.IsEnabled(entries, setting) ? "1" : setting.OffValue;
            else
            {
                var choice = EnvironmentSettings.GetChoice(entries, setting);
                values[setting.Variable] = choice == "auto" ? null : choice;
            }
        }
        // Keep unknown switches from older files and explicit values on known switches.
        foreach (var entry in entries)
        {
            if (CustomEnvironmentVariables.TryParseEntry(entry, out var name, out var value) &&
                name.StartsWith("SHARPEMU_", StringComparison.OrdinalIgnoreCase) &&
                EnvironmentSettings.Find(name) is null)
            {
                values[name] = value;
            }
        }
        values["SHARPEMU_GPU_READBACKS"] =
            string.Equals(Settings.GpuReadbacks, "Off", StringComparison.OrdinalIgnoreCase) ? "off" : "full";
        values[DefaultProfileVariable] = GuiSettings.NormalizeDefaultProfile(launcher.DefaultProfile);
        values[GnmTrophyPackageReader.KeyEnvironmentName] =
            string.IsNullOrEmpty(launcher.GnmTrophyKey) ? null : launcher.GnmTrophyKey;
        values["SHARPEMU_BINK_MODE"] = Settings.BinkPlaybackMode.ToLowerInvariant() switch
        {
            "guest" => "guest", "skip" => "skip", _ => "native",
        };
        foreach (var entry in CustomEnvironment)
        {
            if (CustomEnvironmentVariables.TryParseEntry(entry, out var name, out var value)) values[name] = value;
        }
        return values;
    }

    /// <summary>CLI variables supplied by a script win over the game's file.</summary>
    public IReadOnlyDictionary<string, string?> BuildCliEnvironment(
        GuiSettings launcher, Func<string, string?> inheritedValue) =>
        BuildEnvironment(launcher).Where(pair => inheritedValue(pair.Key) is null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    public static string ResolveLogFilePath(GuiSettings launcher, string? gameKey, string logsDirectory)
    {
        var id = string.IsNullOrWhiteSpace(gameKey) ? "UNKNOWN" : gameKey;
        foreach (var invalid in Path.GetInvalidFileNameChars()) id = id.Replace(invalid, '_');
        var suffix = $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}";
        if (string.IsNullOrWhiteSpace(launcher.LogFilePath)) return Path.Combine(logsDirectory, suffix + ".log");
        if (launcher.OverrideLogFile) return launcher.LogFilePath;
        var name = Path.GetFileNameWithoutExtension(launcher.LogFilePath) + "-" + suffix + Path.GetExtension(launcher.LogFilePath);
        var directory = Path.GetDirectoryName(launcher.LogFilePath);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    public List<string> BuildArguments(string gamePath, string consoleLanguage)
    {
        var settings = Settings;
        var args = new List<string>
        {
            "--cpu-engine=native", $"--log-level={settings.LogLevel}", "--console=ps5",
            $"--console-language={consoleLanguage}", $"--strict={(settings.StrictDynlibResolution ? "on" : "off")}",
            $"--trace-imports={Math.Max(0, settings.ImportTraceLimit).ToString(CultureInfo.InvariantCulture)}",
            $"--window-mode={settings.WindowMode.ToLowerInvariant()}", $"--resolution={settings.Resolution}",
            $"--display={settings.DisplayIndex.ToString(CultureInfo.InvariantCulture)}",
            $"--scaling={settings.ScalingMode.ToLowerInvariant()}", $"--vsync={(settings.VSync ? "on" : "off")}",
            $"--hdr={settings.HdrMode.ToLowerInvariant()}", $"--overlay={(settings.OverlayEnabled ? "on" : "off")}",
            $"--overlay-mode={settings.OverlayMode.ToLowerInvariant()}",
            $"--overlay-corner={settings.OverlayCorner.ToLowerInvariant()}",
            $"--refresh-rate={settings.RefreshRate.ToString(CultureInfo.InvariantCulture)}",
            // The GUI mirrors stdout itself; avoid a second file mirror in its child.
            "--no-log-file",
        };
        args.Add(Path.GetFullPath(gamePath));
        return args;
    }
}

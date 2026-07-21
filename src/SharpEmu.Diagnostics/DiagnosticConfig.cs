// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Configuration for diagnostic plugins. Sources (in priority order):
/// 1. Environment variables: SHARPEMU_DIAG_BOOT=1, SHARPEMU_DIAG_IMPORTS=1, etc.
/// 2. diagnostics.json file: {"BootTimeline": true, "ImportTimeline": false}
/// 3. SHARPEMU_DIAG=1 (enable all default plugins)
/// </summary>
public sealed class DiagnosticConfig
{
    public Dictionary<string, bool> Plugins { get; set; } = new();

    /// <summary>True if any plugin is enabled.</summary>
    public bool IsAnyEnabled => Plugins.Values.Any(v => v);

    /// <summary>Check if a specific plugin is enabled.</summary>
    public bool IsEnabled(string pluginName) =>
        Plugins.TryGetValue(pluginName, out var enabled) && enabled;

    /// <summary>Load config from environment variables and diagnostics.json.</summary>
    public static DiagnosticConfig Load()
    {
        var config = new DiagnosticConfig();
        var pluginNames = new[]
        {
            "BootTimeline", "ImportTimeline", "FirstFailure",
            "CpuTrace", "CrashPackage", "ThreadTimeline", "MemoryTimeline", "Statistics", "ConsoleSink"
        };

        // Check for global enable
        var globalEnable = Environment.GetEnvironmentVariable("SHARPEMU_DIAG") == "1";

        // Load from env vars
        foreach (var name in pluginNames)
        {
            var envVar = $"SHARPEMU_DIAG_{name.ToUpperInvariant()}";
            var envVal = Environment.GetEnvironmentVariable(envVar);
            if (envVal == "1")
                config.Plugins[name] = true;
            else if (envVal == "0")
                config.Plugins[name] = false;
            else if (globalEnable)
                config.Plugins[name] = true; // default-on when SHARPEMU_DIAG=1
        }

        // Load from diagnostics.json (overrides env vars if present)
        var configPath = Path.Combine(Directory.GetCurrentDirectory(), "diagnostics.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var fileConfig = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
                if (fileConfig != null)
                {
                    foreach (var (key, value) in fileConfig)
                        config.Plugins[key] = value;
                }
            }
            catch { /* ignore bad config */ }
        }

        return config;
    }
}

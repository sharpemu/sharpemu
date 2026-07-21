// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Diagnostics.Contracts;

/// <summary>
/// Metadata for a diagnostic plugin. Plugins declare this so the
/// DiagnosticManager can list, describe, and auto-enable them.
/// </summary>
public sealed class PluginMetadata
{
    /// <summary>Human-readable name (e.g. "Boot Timeline").</summary>
    public required string Name { get; init; }

    /// <summary>API version of the plugin (semantic-ish, e.g. "1.0").</summary>
    public required string Version { get; init; } = "1.0";

    /// <summary>Short description shown in --list-plugins output.</summary>
    public required string Description { get; init; } = "";

    /// <summary>Environment variable name that enables this plugin (e.g. "SHARPEMU_DIAG_BOOT").</summary>
    public required string EnvVar { get; init; }

    /// <summary>If true, plugin is active when SHARPEMU_DIAG=1 is set without a specific var.</summary>
    public bool EnabledByDefault { get; init; }

    /// <summary>Names of plugins that must be registered before this one.</summary>
    public string[] Requires { get; init; } = Array.Empty<string>();

    /// <summary>Plugin priority (lower = higher priority). Crash=0, Export=100.</summary>
    public int Priority { get; init; } = 50;

    /// <summary>Performance cost: High, Medium, Low. Used for budget warnings.</summary>
    public string PerformanceBudget { get; init; } = "Low";
}

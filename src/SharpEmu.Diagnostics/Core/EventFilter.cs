// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Diagnostics.Contracts;

namespace SharpEmu.Diagnostics.Core;

/// <summary>
/// Event filter — allows filtering events by category before they reach plugins.
/// Set via SHARPEMU_DIAG_FILTER=cpu,memory,crash (comma-separated categories).
/// If not set, all events pass through.
/// </summary>
public sealed class EventFilter
{
    private readonly HashSet<string>? _allowedCategories;

    public EventFilter()
    {
        var filterEnv = Environment.GetEnvironmentVariable("SHARPEMU_DIAG_FILTER");
        if (!string.IsNullOrWhiteSpace(filterEnv))
            _allowedCategories = new HashSet<string>(
                filterEnv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True if this event should be allowed through to plugins.</summary>
    public bool Allows(IDiagnosticEvent e) =>
        _allowedCategories == null || _allowedCategories.Contains(e.Category);

    /// <summary>True if filtering is active (not all events pass).</summary>
    public bool IsActive => _allowedCategories != null;
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal static class RayTracingSkipSettings
{
    internal const string VariableName = "SHARPEMU_SKIP_RT";

    internal static bool IsEnabled(IEnumerable<string> entries)
    {
        var enabled = false;
        foreach (var entry in entries)
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (string.Equals(parts[0], VariableName, StringComparison.OrdinalIgnoreCase))
                enabled = parts.Length == 1 || parts[1] == "1";
        }
        return enabled;
    }

    internal static void SetEnabled(List<string> entries, bool enabled)
    {
        entries.RemoveAll(entry => string.Equals(entry.Split('=', 2, StringSplitOptions.TrimEntries)[0],
            VariableName, StringComparison.OrdinalIgnoreCase));
        if (enabled) entries.Add(VariableName);
    }

    internal static string GetLaunchValue(IEnumerable<string> entries) => IsEnabled(entries) ? "1" : "0";
}

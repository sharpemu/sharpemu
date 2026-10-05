// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal static class CustomEnvironmentVariables
{
    public static bool TryParseLines(
        string? text,
        out IReadOnlyList<string> entries,
        out string invalidLine)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!TryParseEntry(line, out var name, out var value))
            {
                entries = [];
                invalidLine = line;
                return false;
            }

            parsed[name] = value;
        }

        entries = parsed.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
        invalidLine = string.Empty;
        return true;
    }

    public static IReadOnlyList<string> Merge(
        params IEnumerable<string>?[] sources)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source is null)
            {
                continue;
            }

            foreach (var entry in source)
            {
                if (TryParseEntry(entry, out var name, out var value))
                {
                    merged[name] = value;
                }
            }
        }

        return merged.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
    }

    public static bool TryParseEntry(string entry, out string name, out string value)
    {
        var separator = entry.IndexOf('=');
        name = (separator >= 0 ? entry[..separator] : entry).Trim();
        value = separator >= 0 ? entry[(separator + 1)..] : "1";

        return name.Length != 0 &&
               !name.Contains('\0') &&
               !name.Contains('=');
    }
}

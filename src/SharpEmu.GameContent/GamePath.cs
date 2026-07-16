// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GameContent;

internal static class GamePath
{
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var components = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(static component => component is "." or ".."))
        {
            throw new ArgumentException("Game paths cannot contain '.' or '..' components.", nameof(path));
        }

        return string.Join('/', components);
    }

    public static string GetParent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    public static string Combine(string left, string right) =>
        string.IsNullOrEmpty(left) ? Normalize(right) : Normalize($"{left}/{right}");
}

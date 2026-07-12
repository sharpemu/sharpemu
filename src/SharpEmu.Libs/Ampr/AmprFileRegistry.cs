// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Ampr;

internal static class AmprFileRegistry
{
    private static readonly ConcurrentDictionary<uint, string> _hostPathsById = new();

    public static uint Register(string guestPath, string hostPath)
    {
        var id = ComputeFileId(guestPath);
        _hostPathsById[id] = hostPath;
        return id;
    }

    public static bool TryGetHostPath(uint id, out string hostPath)
    {
        return _hostPathsById.TryGetValue(id, out hostPath!);
    }

    internal static uint ComputeFileId(string guestPath)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(guestPath);

        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}

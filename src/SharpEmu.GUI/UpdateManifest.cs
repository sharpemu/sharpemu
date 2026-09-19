// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.GUI;

/// <summary>Machine-readable metadata published with each versioned release.</summary>
internal sealed record UpdateManifest(int Schema, string Version, string Commit, string Sha256Sums)
{
    public static UpdateManifest? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1)
            {
                return null;
            }

            return new UpdateManifest(
                1,
                root.GetProperty("version").GetString() ?? "",
                root.GetProperty("commit").GetString() ?? "",
                root.GetProperty("sha256sums").GetString() ?? "");
        }
        catch { return null; }
    }
}

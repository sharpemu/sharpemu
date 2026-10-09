// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO;

namespace SharpEmu.GUI;

internal static class BundledDemo
{
    internal const string FolderName = "demo";

    private static readonly string[] RequiredFiles =
    [
        "eboot.bin",
        Path.Combine("sce_sys", "param.json"),
        Path.Combine("sce_sys", "icon0.png"),
        Path.Combine("sce_sys", "pic0.png"),
        Path.Combine("sce_sys", "snd0.at9"),
    ];

    internal static string? FindEboot(string baseDirectory)
    {
        var root = Path.Combine(baseDirectory, FolderName);
        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(root, file)))
            {
                return null;
            }
        }

        return Path.GetFullPath(Path.Combine(root, "eboot.bin"));
    }
}

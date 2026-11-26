// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem;

namespace SharpEmu.GUI;

// A game that lives in one file: a .zar the emulator writes, or a fake PS5 .pkg it mounts. Both are
// mounted at a cache folder that stands in for the game folder. The type checks look at the
// extension only, so a game keeps its type while the library watcher catches up with a moved file.
internal static class GameContainer
{
    public static bool IsContainer(string path) => IsArchive(path) || IsPackage(path);

    public static bool IsArchive(string path) => HasExtension(path, ".zar");

    public static bool IsPackage(string path) => HasExtension(path, ".pkg");

    // The folder standing in for the game folder, or null for a game that is already a folder.
    public static string? CacheRoot(string path)
    {
        if (IsArchive(path))
        {
            return ZArchiveCache.GetCacheRoot(path, Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "zar-cache"));
        }

        return IsPackage(path)
            ? PkgArchiveCache.GetCacheRoot(path, Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "pkg-cache"))
            : null;
    }

    // The label a container game's size badge is prefixed with, e.g. "ZAR" or "PKG".
    public static string ExtensionLabel(string path) => Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    private static bool HasExtension(string path, string extension) =>
        !string.IsNullOrEmpty(path) && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}

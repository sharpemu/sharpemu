// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem.Pkg;

namespace SharpEmu.HLE.FileSystem;

// Each mounted .pkg gets one host folder under user/pkg-cache, named after the package. /app0 is
// mounted there while the game runs and the library reads the game's metadata from it; only the
// files host code opens by path are written into it. A stamp of the package's path, size and write
// time tells whether the files in it came from this package; when it differs, the folder starts over.
public static class PkgArchiveCache
{
    private const string StampName = ".sharpemu-package";

    // Fallback for portable layouts; system installs pass the user data root explicitly
    // (SharpEmu.HLE cannot see SharpEmu.Libs).
    private static string DefaultCacheParent => Path.Combine(AppContext.BaseDirectory, "user", "pkg-cache");

    // The cache folder a package uses, without creating or checking it.
    public static string GetCacheRoot(string packagePath, string? cacheParent = null) =>
        Path.Combine(cacheParent ?? DefaultCacheParent, Path.GetFileNameWithoutExtension(packagePath));

    public static string Prepare(string packagePath) => Prepare(packagePath, DefaultCacheParent);

    public static string Prepare(string packagePath, string cacheParent)
    {
        packagePath = Path.GetFullPath(packagePath);
        var info = new FileInfo(packagePath);
        var cacheRoot = GetCacheRoot(packagePath, cacheParent);
        var stampPath = Path.Combine(cacheRoot, StampName);
        var stamp = $"{packagePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (!File.Exists(stampPath) || File.ReadAllText(stampPath) != stamp)
        {
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }

            Directory.CreateDirectory(cacheRoot);
            File.WriteAllText(stampPath, stamp);
        }

        return cacheRoot;
    }

    // Extracts a package's sce_sys metadata (parameters, icon and key art) into its cache folder and
    // returns the folder, or null when the file is not a mountable PS5 package. The folder stands in
    // for the game folder; the game itself stays inside the package.
    public static string? ExtractMetadata(string packagePath, string? cacheParent = null)
    {
        if (!PkgArchive.IsPackagePath(packagePath))
        {
            return null;
        }

        using var package = GamePackage.Open(packagePath);
        if (package is not Ps5Package ps5)
        {
            return null;
        }

        var cacheRoot = cacheParent is null ? Prepare(packagePath) : Prepare(packagePath, cacheParent);
        ps5.ExtractMetadata(cacheRoot);
        return cacheRoot;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem.Pkg;

namespace SharpEmu.HLE.FileSystem;

// Each .zar gets one host folder under user/zar-cache, named after the archive. /app0 is mounted
// there while the game runs and the library reads the game's metadata from it. A stamp of the
// archive's path, size and write time tells whether the files in it came from this archive; when
// it differs, the folder starts over.
public static class ZArchiveCache
{
    private const string StampName = ".sharpemu-archive";
    private const string MetadataMarkerName = ".sharpemu-metadata";

    // The files the library shows for a game: its parameters, icon and key art.
    private static readonly string[] MetadataFiles =
    [
        "sce_sys/param.json",
        "sce_sys/param.sfo",
        "sce_sys/icon0.png",
        "sce_sys/pic0.png",
        "sce_sys/pic1.png",
        "sce_sys/snd0.at9",
        "sce_sys/trophy2/trophy00.ucp",
        "sce_sys/trophy/trophy00.trp",
        "sce_sys/npbind.dat",
    ];

    // Fallback for portable layouts; system installs pass the user data root
    // explicitly (SharpEmu.HLE cannot see SharpEmu.Libs).
    private static string DefaultCacheParent => Path.Combine(AppContext.BaseDirectory, "user", "zar-cache");

    // The cache folder an archive or package uses, without creating or checking it. A package gets
    // a suffix so it never shares a folder with an archive of the same name.
    public static string GetCacheRoot(string archivePath, string? cacheParent = null) =>
        Path.Combine(
            cacheParent ?? DefaultCacheParent,
            Path.GetFileNameWithoutExtension(archivePath) + (IsPackagePath(archivePath) ? "-pkg" : string.Empty));

    // A .zar archive or a PS5 .pkg: both run mounted, without being extracted.
    public static bool IsMountablePath(string? path) => ZArchive.IsArchivePath(path) || Ps5PackageSource.IsPackagePath(path);

    // Any existing .pkg file, PS4 or PS5; the runtime uses it to say a PS4 package must be installed.
    public static bool IsPackageFile(string? path) => IsPackagePath(path) && File.Exists(path);

    private static bool IsPackagePath(string? path) =>
        path is not null && path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase);

    public static string Prepare(string archivePath) => Prepare(archivePath, DefaultCacheParent);

    public static string Prepare(string archivePath, string cacheParent)
    {
        archivePath = Path.GetFullPath(archivePath);
        var info = new FileInfo(archivePath);
        var cacheRoot = GetCacheRoot(archivePath, cacheParent);
        var stampPath = Path.Combine(cacheRoot, StampName);
        var stamp = $"{archivePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
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

    // Extracts an archive's sce_sys metadata into its cache folder and returns the folder, or null
    // when the file is not a readable archive. The folder stands in for the game folder.
    public static string? ExtractMetadata(string archivePath, string? cacheParent = null)
    {
        if (IsPackagePath(archivePath))
        {
            return ExtractPackageMetadata(archivePath, cacheParent);
        }

        using var archive = ZArchive.Open(archivePath);
        if (archive is null)
        {
            return null;
        }

        var cacheRoot = cacheParent is null ? Prepare(archivePath) : Prepare(archivePath, cacheParent);
        foreach (var relative in MetadataFiles)
        {
            var node = archive.LookUp(relative);
            if (!archive.IsFile(node))
            {
                continue;
            }

            var hostPath = Path.Combine(cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            using var source = new ZArchiveFileStream(archive, node, hostPath);
            App0Archive.WriteFile(source, hostPath);
        }

        return cacheRoot;
    }

    // The same files for a package. Opening one decodes its inner metadata, so a marker in the
    // folder (cleared with it when the package changes) skips the open on later library scans.
    private static string? ExtractPackageMetadata(string packagePath, string? cacheParent)
    {
        if (!Ps5PackageSource.IsPackagePath(packagePath))
        {
            return null;
        }

        var cacheRoot = cacheParent is null ? Prepare(packagePath) : Prepare(packagePath, cacheParent);
        var marker = Path.Combine(cacheRoot, MetadataMarkerName);
        if (File.Exists(marker))
        {
            return cacheRoot;
        }

        using var source = Ps5PackageSource.Open(packagePath);
        foreach (var relative in MetadataFiles)
        {
            var node = source.LookUp(relative);
            if (!source.IsFile(node))
            {
                continue;
            }

            var hostPath = Path.Combine(cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            using var stream = new ZArchiveFileStream(source, node, hostPath);
            App0Archive.WriteFile(stream, hostPath);
        }

        File.WriteAllText(marker, string.Empty);
        return cacheRoot;
    }
}

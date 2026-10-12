// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.FileSystem;

// Each .zar gets one host folder under user/zar-cache, named after the archive. /app0 is mounted
// there while the game runs and the library reads the game's metadata from it. A stamp of the
// archive's path, size and write time tells whether the files in it came from this archive; when
// it differs, the folder starts over.
public static class ZArchiveCache
{
    private const string StampName = ".sharpemu-archive";

    // The files the library shows for a game: its parameters, icon and key art.
    private static readonly string[] MetadataFiles =
    [
        "sce_sys/param.json",
        "sce_sys/icon0.png",
        "sce_sys/pic0.png",
        "sce_sys/pic1.png",
        "sce_sys/snd0.at9",
    ];

    private static string DefaultCacheParent => Path.Combine(AppContext.BaseDirectory, "user", "zar-cache");

    // The cache folder an archive uses, without creating or checking it.
    public static string GetCacheRoot(string archivePath, string? cacheParent = null) =>
        Path.Combine(cacheParent ?? DefaultCacheParent, Path.GetFileNameWithoutExtension(archivePath));

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
}

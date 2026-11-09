// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.GnmMode.Loader;
using SharpEmu.HLE.FileSystem;

namespace SharpEmu.GnmMode;

public static partial class GnmRuntime
{
    public static ISelfLoader? CreateLoader(string path)
    {
        if (!UsesGnmMode(path))
            return null;
        return new GnmSelfLoader();
    }

    public static (string? Title, string? TitleId, string? Version) ReadApplicationInfo(string ebootPath)
    {
        try
        {
            if (ZArchive.IsArchivePath(ebootPath))
            {
                using var archive = ZArchive.Open(ebootPath);
                if (archive is null || !UsesGnmMode(archive))
                    return default;
                var node = archive.LookUp("sce_sys/param.sfo");
                if (!archive.IsFile(node) || archive.GetFileSize(node) > int.MaxValue)
                    return default;
                using var stream = new ZArchiveFileStream(archive, node, "sce_sys/param.sfo");
                var data = new byte[(int)stream.Length];
                stream.ReadExactly(data);
                return GnmParamSfo.Read(data);
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(ebootPath));
            if (directory is null)
                return default;

            var paramPath = Path.Combine(directory, "sce_sys", "param.sfo");
            if (!File.Exists(paramPath) || !UsesGnmMode(ebootPath))
                return default;

            return GnmParamSfo.Read(File.ReadAllBytes(paramPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // Missing or invalid game metadata must not prevent launching.
            return default;
        }
    }

    public static bool UsesCompatibilityMode(string path) => UsesGnmMode(path);

    private static bool UsesGnmMode(string path)
    {
        if (!File.Exists(path))
            return false;
        if (ZArchive.IsArchivePath(path))
        {
            using var archive = ZArchive.Open(path);
            return archive is not null && UsesGnmMode(archive);
        }
        using var stream = File.OpenRead(path);
        return GnmLoader.IsGnmExecutable(stream);
    }

    private static bool UsesGnmMode(ZArchive archive)
    {
        var node = archive.LookUp("eboot.bin");
        if (!archive.IsFile(node))
            return false;
        using var stream = new ZArchiveFileStream(archive, node, "eboot.bin");
        return GnmLoader.IsGnmExecutable(stream);
    }
}

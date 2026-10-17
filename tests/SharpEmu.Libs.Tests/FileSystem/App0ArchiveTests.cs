// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

public sealed class App0ArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-app0-").FullName;
    private readonly string _root;

    public App0ArchiveTests()
    {
        var archivePath = Path.Combine(_directory, "game.zar");
        File.WriteAllBytes(archivePath, ZArchiveTestWriter.Build(
        [
            ("eboot.bin", Bytes(100_000, 1)),
            ("sce_sys/param.json", Bytes(500, 2)),
            ("data/movies/intro.bk2", Bytes(80_000, 3)),
            ("data/level.bin", Bytes(3_000, 4)),
        ]));
        _root = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(_root);
        App0Archive.Mount(ZArchive.Open(archivePath)!, _root);
    }

    public void Dispose()
    {
        App0Archive.Unmount();
        Directory.Delete(_directory, recursive: true);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)(index * 13 + seed);
        }

        return bytes;
    }

    [Fact]
    public void HostPathsUnderTheMountResolveInTheArchive()
    {
        Assert.True(App0Archive.TryGetFile(Path.Combine(_root, "DATA", "level.bin"), out var size));
        Assert.Equal(3_000UL, size);
        Assert.True(App0Archive.IsDirectory(Path.Combine(_root, "data", "movies")));
        Assert.True(App0Archive.IsDirectory(_root));
        Assert.False(App0Archive.Exists(Path.Combine(_root, "data", "missing.bin")));
        Assert.False(App0Archive.Exists(Path.Combine(_directory, "data", "level.bin")));
        Assert.False(App0Archive.Exists(_root + "-other" + Path.DirectorySeparatorChar + "eboot.bin"));
    }

    [Fact]
    public void AStreamSeeksAndReadsLikeAFile()
    {
        using var stream = App0Archive.OpenRead(Path.Combine(_root, "data", "movies", "intro.bk2"))!;
        var expected = Bytes(80_000, 3);

        Assert.Equal(expected.Length, stream.Length);
        stream.Seek(-1_000, SeekOrigin.End);
        var tail = new byte[4_000];
        Assert.Equal(1_000, stream.Read(tail));
        Assert.Equal(expected.AsSpan(79_000).ToArray(), tail.AsSpan(0, 1_000).ToArray());
        Assert.Equal(0, stream.Read(tail));

        var window = new byte[10_000];
        Assert.Equal(window.Length, stream.ReadAt(window, 60_000));
        Assert.Equal(expected.AsSpan(60_000, window.Length).ToArray(), window);
        Assert.Equal(expected.Length, stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Write(window, 0, 1));
    }

    [Fact]
    public void ExtractionWritesOneFileAndListingsShowTheArchive()
    {
        var movie = Path.Combine(_root, "data", "movies", "intro.bk2");
        Assert.True(App0Archive.Extract(movie));
        Assert.Equal(Bytes(80_000, 3), File.ReadAllBytes(movie));
        Assert.False(File.Exists(Path.Combine(_root, "data", "level.bin")));
        Assert.False(App0Archive.Extract(Path.Combine(_root, "data", "missing.bin")));

        App0Archive.ExtractDirectory("sce_sys");
        Assert.Equal(Bytes(500, 2), File.ReadAllBytes(Path.Combine(_root, "sce_sys", "param.json")));

        var names = App0Archive.List(_root).Select(entry => entry.Name).Order(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["data", "eboot.bin", "sce_sys"], names);
    }

    [Fact]
    public void RecursiveListingsJoinTheDiskAndTheArchive()
    {
        File.WriteAllBytes(Path.Combine(_root, "local.bk2"), [1]);

        var movies = App0Archive.EnumerateFilesRecursive(_root, "*.bk2")
            .Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["data/movies/intro.bk2", "local.bk2"], movies);
        Assert.Equal(
            ["data/level.bin", "data/movies/intro.bk2", "eboot.bin", "sce_sys/param.json"],
            App0Archive.ListAllFiles().Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AReadOpenPrefersTheDiskAndFallsBackToTheArchive()
    {
        var level = Path.Combine(_root, "data", "level.bin");
        using (var archived = App0Archive.OpenReadOrFile(level))
        {
            Assert.IsType<ZArchiveFileStream>(archived);
            Assert.Equal(3_000, archived.Length);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(level)!);
        File.WriteAllBytes(level, [9, 9]);
        using (var onDisk = App0Archive.OpenReadOrFile(level))
        {
            Assert.IsType<FileStream>(onDisk);
            Assert.Equal(2, onDisk.Length);
        }
    }

    [Fact]
    public void TheCacheKeepsItsFilesUntilTheArchiveChanges()
    {
        var cacheParent = Path.Combine(_directory, "caches");
        var archivePath = Path.Combine(_directory, "game.zar");

        var cacheRoot = ZArchiveCache.ExtractMetadata(archivePath, cacheParent)!;
        Assert.Equal(Path.Combine(cacheParent, "game"), cacheRoot);
        Assert.Equal(Bytes(500, 2), File.ReadAllBytes(Path.Combine(cacheRoot, "sce_sys", "param.json")));

        var kept = Path.Combine(cacheRoot, "kept.bin");
        File.WriteAllBytes(kept, [1]);
        ZArchiveCache.Prepare(archivePath, cacheParent);
        Assert.True(File.Exists(kept));

        File.SetLastWriteTimeUtc(archivePath, DateTime.UtcNow.AddMinutes(5));
        ZArchiveCache.Prepare(archivePath, cacheParent);
        Assert.False(File.Exists(kept));

        var plain = Path.Combine(_directory, "plain.zar");
        File.WriteAllBytes(plain, Bytes(4096, 5));
        Assert.Null(ZArchiveCache.ExtractMetadata(plain, cacheParent));
    }
}

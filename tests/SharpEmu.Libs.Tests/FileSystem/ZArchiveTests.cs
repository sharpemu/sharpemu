// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

public sealed class ZArchiveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-zar-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(params (string Path, byte[] Data)[] files)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.zar");
        File.WriteAllBytes(path, ZArchiveTestWriter.Build(files));
        return path;
    }

    private static byte[] Pattern(int length, int seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * 31 + seed) ^ (index >> 9));
        }

        return bytes;
    }

    [Fact]
    public void FilesReadBackAcrossBlocksAndDirectories()
    {
        var eboot = Pattern(200_000, 1);
        var param = Pattern(1_234, 2);
        var data = Pattern(70_000, 3);
        using var archive = ZArchive.Open(Write(("eboot.bin", eboot), ("sce_sys/param.json", param), ("data/level/a.bin", data)))!;

        Assert.NotNull(archive);
        foreach (var (path, expected) in new[] { ("eboot.bin", eboot), ("sce_sys/param.json", param), ("data/level/a.bin", data) })
        {
            var node = archive.LookUp(path);
            Assert.True(archive.IsFile(node));
            Assert.Equal((ulong)expected.Length, archive.GetFileSize(node));
            var actual = new byte[expected.Length];
            Assert.Equal(expected.Length, archive.Read(node, 0, actual));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void AReadAtAnOffsetSpansBlocksAndStopsAtTheEnd()
    {
        var bytes = Pattern(150_000, 7);
        using var archive = ZArchive.Open(Write(("big.bin", bytes)))!;
        var node = archive.LookUp("big.bin");

        var window = new byte[20_000];
        Assert.Equal(window.Length, archive.Read(node, 60_000, window));
        Assert.Equal(bytes.AsSpan(60_000, window.Length).ToArray(), window);

        var tail = new byte[1000];
        Assert.Equal(100, archive.Read(node, 149_900, tail));
        Assert.Equal(bytes.AsSpan(149_900, 100).ToArray(), tail.AsSpan(0, 100).ToArray());
        Assert.Equal(0, archive.Read(node, 150_000, tail));
    }

    [Fact]
    public void LookupIgnoresCaseAndAcceptsEitherSeparator()
    {
        using var archive = ZArchive.Open(Write(("sce_sys/Param.JSON", [1, 2, 3])))!;

        Assert.True(archive.IsFile(archive.LookUp("SCE_SYS\\param.json")));
        Assert.True(archive.IsDirectory(archive.LookUp("/sce_sys/")));
        Assert.True(archive.IsDirectory(archive.LookUp(string.Empty)));
        Assert.Equal(ZArchive.InvalidNode, archive.LookUp("sce_sys/missing.json"));
        Assert.Equal(ZArchive.InvalidNode, archive.LookUp("sce_sys/param.json/inner"));
    }

    [Fact]
    public void DirectoriesListTheirEntries()
    {
        using var archive = ZArchive.Open(Write(("dir/b.bin", [1]), ("dir/a.bin", [1, 2]), ("dir/sub/c.bin", [3])))!;

        var entries = archive.GetEntries(archive.LookUp("dir")).OrderBy(entry => entry.Name).ToArray();

        Assert.Equal(["a.bin", "b.bin", "sub"], entries.Select(entry => entry.Name));
        Assert.Equal(2UL, entries[0].Size);
        Assert.True(entries[2].IsDirectory);
    }

    [Fact]
    public void LongNamesAndIncompressibleBlocksReadBack()
    {
        var name = new string('n', 300) + ".bin";
        var random = new byte[100_000];
        new System.Random(5).NextBytes(random);
        using var archive = ZArchive.Open(Write((name, random)))!;

        var node = archive.LookUp(name);
        var actual = new byte[random.Length];
        Assert.Equal(random.Length, archive.Read(node, 0, actual));
        Assert.Equal(random, actual);
    }

    [Fact]
    public void AFileThatIsNotAnArchiveDoesNotOpen()
    {
        var path = Path.Combine(_directory, "plain.zar");
        File.WriteAllBytes(path, Pattern(4096, 9));

        Assert.Null(ZArchive.Open(path));
    }
}

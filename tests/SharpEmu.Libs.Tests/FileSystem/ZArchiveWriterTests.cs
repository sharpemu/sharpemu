// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

public sealed class ZArchiveWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-zarpack-").FullName;
    private readonly string _game;

    public ZArchiveWriterTests()
    {
        _game = Path.Combine(_directory, "game");
        Put("eboot.bin", Bytes(150_000, 1));
        Put(Path.Combine("sce_sys", "param.json"), Bytes(700, 2));
        Put(Path.Combine("data", "Level", "a.bin"), Bytes(0, 3));
        Put(Path.Combine("data", "Level", "b.bin"), Bytes(200_000, 4));
        Put(Path.Combine("data", new string('n', 200) + ".bin"), Bytes(10, 5));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void Put(string relative, byte[] data)
    {
        var path = Path.Combine(_game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes.AsSpan(0, length / 2));
        for (var index = length / 2; index < length; index++)
        {
            bytes[index] = (byte)(index % 7);
        }

        return bytes;
    }

    [Fact]
    public void AFolderReadsBackFromItsArchive()
    {
        var archivePath = Path.Combine(_directory, "game.zar");
        var reports = new List<ZArchiveWriter.Progress>();
        ZArchiveWriter.Pack(_game, archivePath, new SynchronousProgress(reports.Add));

        using var archive = ZArchive.Open(archivePath)!;
        Assert.NotNull(archive);
        foreach (var path in Directory.EnumerateFiles(_game, "*", SearchOption.AllDirectories))
        {
            var expected = File.ReadAllBytes(path);
            var node = archive.LookUp(Path.GetRelativePath(_game, path));
            Assert.True(archive.IsFile(node));
            var actual = new byte[expected.Length];
            Assert.Equal(expected.Length, archive.Read(node, 0, actual));
            Assert.Equal(expected, actual);
        }

        Assert.Equal(["data", "eboot.bin", "sce_sys"], archive.GetEntries(archive.LookUp(string.Empty)).Select(e => e.Name).Order());
        Assert.Equal(reports[^1].BytesTotal, reports[^1].BytesDone);
        Assert.Equal(5, reports[^1].FilesTotal);
    }

    [Fact]
    public void ACancelledPackLeavesNoArchive()
    {
        var archivePath = Path.Combine(_directory, "game.zar");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => ZArchiveWriter.Pack(_game, archivePath, cancellationToken: cancelled.Token));
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void AnArchiveMatchesItsFolderUntilTheFolderChanges()
    {
        var archivePath = Path.Combine(_directory, "game.zar");
        ZArchiveWriter.Pack(_game, archivePath);
        Assert.True(ZArchiveWriter.MatchesFolder(archivePath, _game));

        Put("added.bin", Bytes(5, 6));
        Assert.False(ZArchiveWriter.MatchesFolder(archivePath, _game));

        File.Delete(Path.Combine(_game, "added.bin"));
        Put("eboot.bin", Bytes(150_001, 1));
        Assert.False(ZArchiveWriter.MatchesFolder(archivePath, _game));

        File.WriteAllBytes(archivePath, Bytes(4096, 7));
        Assert.False(ZArchiveWriter.MatchesFolder(archivePath, _game));
    }

    [Fact]
    public void AnArchiveCannotBeWrittenIntoTheFolderItPacks()
    {
        Assert.Throws<IOException>(() => ZArchiveWriter.Pack(_game, Path.Combine(_game, "game.zar")));
    }

    private sealed class SynchronousProgress(Action<ZArchiveWriter.Progress> report) : IProgress<ZArchiveWriter.Progress>
    {
        public void Report(ZArchiveWriter.Progress value) => report(value);
    }
}

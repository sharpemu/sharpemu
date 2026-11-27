// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using SharpEmu.HLE.FileSystem;
using SharpEmu.HLE.FileSystem.Pkg;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

// Synthetic packages only. Each test compares the archive against what Extract writes for the
// same package, so the converter must list, name and decode exactly what a folder install does.
public sealed class PkgZarConverterTests : IDisposable
{
    // Window below one group: every consume batch holds one 1 MiB group.
    private const long TinyWindow = 1;

    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-pkgzar-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public enum Format
    {
        Ps4,
        Ps5,
        Ps5Scrambled,
        Ps5Small,
    }

    // ---- direct mode ---------------------------------------------------------------------------

    [Theory]
    [InlineData(Format.Ps4)]
    [InlineData(Format.Ps5)]
    [InlineData(Format.Ps5Scrambled)]
    [InlineData(Format.Ps5Small)]
    public void DirectModeHoldsWhatExtractWritesAndLeavesThePackage(Format format)
    {
        var bytes = Build(format);
        var package = Write("game.pkg", bytes);
        var destination = Path.Combine(_directory, "game.zar");

        PkgZarConverter.Convert(package, destination, Options(consume: false).Build(), null, default);

        Assert.Equal(ExtractTree(bytes), ReadArchive(destination));
        Assert.Equal(bytes, File.ReadAllBytes(package));
        Assert.False(File.Exists(PkgZarConverter.PartialPath(destination)));
        Assert.False(File.Exists(PkgZarConverter.JournalPath(destination)));
    }

    // ---- consume mode --------------------------------------------------------------------------

    [Theory]
    [InlineData(Format.Ps4)]
    [InlineData(Format.Ps5)]
    [InlineData(Format.Ps5Scrambled)]
    [InlineData(Format.Ps5Small)]
    public void ConsumeModeShrinksThePackageAndEndsWithOnlyTheArchive(Format format)
    {
        var bytes = Build(format);
        var package = Write("game.pkg", bytes);
        var destination = Path.Combine(_directory, "game.zar");
        var lengths = new List<long> { bytes.Length };
        var reads = 0;
        var options = Options(consume: true) with
        {
            Observer = (stage, _, _) =>
            {
                if (stage == PkgZarStage.BatchTruncated)
                {
                    lengths.Add(new FileInfo(package).Length);
                }
            },
            // Every byte read from the file must lie below the current cut.
            SourceReadObserver = (offset, length, frontier) =>
            {
                Assert.True(offset + length <= frontier, $"read 0x{offset:X}+0x{length:X} past the frontier 0x{frontier:X}");
                Assert.True(offset + length <= new FileInfo(package).Length);
                reads++;
            },
        };

        PkgZarConverter.Convert(package, destination, options.Build(), null, default);

        Assert.True(reads > 0);
        Assert.Equal(ExtractTree(bytes), ReadArchive(destination));
        Assert.False(File.Exists(package));
        Assert.False(File.Exists(PkgZarConverter.PartialPath(destination)));
        Assert.False(File.Exists(PkgZarConverter.JournalPath(destination)));
        for (var index = 1; index < lengths.Count; index++)
        {
            Assert.True(lengths[index] <= lengths[index - 1], $"package grew: {string.Join(", ", lengths)}");
        }

        if (format is Format.Ps4 or Format.Ps5)
        {
            // Contiguous layouts let most batches give space back, down to the parsed metadata.
            Assert.True(lengths.Distinct().Count() >= 4, $"package barely shrank: {string.Join(", ", lengths)}");
            Assert.True(lengths[^1] < bytes.Length / 2, $"package barely shrank: {string.Join(", ", lengths)}");
        }
    }

    // ---- interruption and resume ---------------------------------------------------------------

    public static TheoryData<Format, string, int> Interruptions()
    {
        var data = new TheoryData<Format, string, int>();
        foreach (var format in new[] { Format.Ps4, Format.Ps5 })
        {
            data.Add(format, nameof(PkgZarStage.JournalCreated), -1);
            data.Add(format, nameof(PkgZarStage.BatchWritten), 0);
            data.Add(format, nameof(PkgZarStage.BatchCommitted), 0);
            data.Add(format, nameof(PkgZarStage.BatchTruncated), 1);
            data.Add(format, nameof(PkgZarStage.BatchWritten), 3);
            data.Add(format, nameof(PkgZarStage.BatchCommitted), 4);
            data.Add(format, nameof(PkgZarStage.DataComplete), -1);
            data.Add(format, nameof(PkgZarStage.TrailerWritten), -1);
            data.Add(format, nameof(PkgZarStage.JournalFinished), -1);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Interruptions))]
    public void AnInterruptedConversionResumesToTheSameArchive(Format format, string stageName, int batch)
    {
        var stage = Enum.Parse<PkgZarStage>(stageName);
        var bytes = Build(format);
        var expected = ConsumeUninterrupted(bytes);
        var package = Write("game.pkg", bytes);
        var destination = Path.Combine(_directory, "game.zar");
        var options = Options(consume: true) with
        {
            Observer = (at, index, _) =>
            {
                if (at == stage && (batch < 0 || index == batch))
                {
                    throw new SimulatedCrash();
                }
            },
        };

        Assert.Throws<SimulatedCrash>(() => PkgZarConverter.Convert(package, destination, options.Build(), null, default));
        Assert.True(File.Exists(PkgZarConverter.JournalPath(destination)));
        Assert.False(File.Exists(destination));

        PkgZarConverter.Convert(package, destination, Options(consume: true).Build(), null, default);

        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.False(File.Exists(package));
        Assert.False(File.Exists(PkgZarConverter.JournalPath(destination)));
    }

    [Theory]
    [InlineData(Format.Ps4)]
    [InlineData(Format.Ps5)]
    public void CancellationAndRunningOutOfSpacePauseAndResumeRepeatedly(Format format)
    {
        var bytes = Build(format);
        var expected = ConsumeUninterrupted(bytes);
        var package = Write("game.pkg", bytes);
        var destination = Path.Combine(_directory, "game.zar");

        using (var cancellation = new CancellationTokenSource())
        {
            var cancelling = Options(consume: true) with
            {
                Observer = (stage, index, _) =>
                {
                    if (stage == PkgZarStage.BatchTruncated && index == 1)
                    {
                        cancellation.Cancel();
                    }
                },
            };
            Assert.ThrowsAny<OperationCanceledException>(() => PkgZarConverter.Convert(package, destination, cancelling.Build(), null, cancellation.Token));
        }

        var full = Options(consume: true) with
        {
            Observer = (stage, index, _) =>
            {
                if (stage == PkgZarStage.BatchWritten && index == 3)
                {
                    throw new IOException("No space left on device", 28);
                }
            },
        };
        var paused = Assert.Throws<PkgConversionPausedException>(() => PkgZarConverter.Convert(package, destination, full.Build(), null, default));
        Assert.Contains("resume", paused.Message);
        Assert.Equal(destination, PkgZarConverter.FindPendingDestination(package));

        PkgZarConverter.Convert(package, destination, Options(consume: true).Build(), null, default);
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.False(File.Exists(package));
    }

    [Fact]
    public void AJournalForAnotherPackageIsRefused()
    {
        var first = Build(Format.Ps5);
        var second = Build(Format.Ps4);
        var package = Write("game.pkg", first);
        var other = Write("other.pkg", second);
        var destination = Path.Combine(_directory, "game.zar");
        var interrupted = Options(consume: true) with
        {
            Observer = (stage, index, _) =>
            {
                if (stage == PkgZarStage.BatchTruncated && index == 1)
                {
                    throw new SimulatedCrash();
                }
            },
        };
        Assert.Throws<SimulatedCrash>(() => PkgZarConverter.Convert(package, destination, interrupted.Build(), null, default));
        var cut = File.ReadAllBytes(package);
        Assert.True(cut.Length < first.Length);

        // Another package into the same archive.
        var exception = Assert.Throws<InvalidDataException>(() => PkgZarConverter.Convert(other, destination, Options(consume: true).Build(), null, default));
        Assert.Contains("belongs to a conversion of", exception.Message);

        // Another package's bytes at the journal's path, cut to the same length.
        File.WriteAllBytes(package, second.AsSpan(0, Math.Min(second.Length, cut.Length)).ToArray());
        Assert.Throws<InvalidDataException>(() => PkgZarConverter.Convert(package, destination, Options(consume: true).Build(), null, default));

        // A direct conversion does not run over the pending one.
        File.WriteAllBytes(package, cut);
        Assert.Throws<IOException>(() => PkgZarConverter.Convert(package, destination, Options(consume: false).Build(), null, default));

        // The real package still resumes.
        PkgZarConverter.Convert(package, destination, Options(consume: true).Build(), null, default);
        Assert.Equal(ExtractTree(first), ReadArchive(destination));
    }

    [Fact]
    public void ADamagedJournalRecordIsRefusedButACutShortTailIsRedone()
    {
        var bytes = Build(Format.Ps4);
        var expected = ConsumeUninterrupted(bytes);
        var package = Write("game.pkg", bytes);
        var destination = Path.Combine(_directory, "game.zar");
        var interrupted = Options(consume: true) with
        {
            Observer = (stage, index, _) =>
            {
                if (stage == PkgZarStage.BatchCommitted && index == 2)
                {
                    throw new SimulatedCrash();
                }
            },
        };
        Assert.Throws<SimulatedCrash>(() => PkgZarConverter.Convert(package, destination, interrupted.Build(), null, default));
        var journal = PkgZarConverter.JournalPath(destination);
        var intact = File.ReadAllBytes(journal);

        // A torn half record after the last commit: dropped, its batch redone.
        File.WriteAllBytes(journal, [.. intact, 1, 0, 0, 0, 200, 0]);
        // A flipped byte inside a committed record followed by more records: refused.
        var damaged = (byte[])intact.Clone();
        damaged[^100] ^= 0xFF;
        File.WriteAllBytes(journal, [.. damaged, .. intact[^60..]]);
        Assert.Throws<InvalidDataException>(() => PkgZarConverter.Convert(package, destination, Options(consume: true).Build(), null, default));

        File.WriteAllBytes(journal, [.. intact, 1, 0, 0, 0, 200, 0]);
        PkgZarConverter.Convert(package, destination, Options(consume: true).Build(), null, default);
        Assert.Equal(expected, File.ReadAllBytes(destination));
    }

    [Fact]
    public void BatchesFollowTheWindowAndDescendInSourceOrder()
    {
        const long MiB = 1 << 20;
        var plan = new PkgZarConverter.Plan
        {
            Files = [],
            DataSize = 5L * ZArchiveWriter.RecordSpan,
            GroupEnds = [100 * MiB, 500 * MiB, 300 * MiB, 500 * MiB, 0],
            Fingerprint = [],
        };
        string Batches(long window) => string.Join(";", PkgZarConverter.BuildBatches(plan, window).Select(batch => string.Join(",", batch)));

        // At most one group's worth of archive data per batch: one group each.
        Assert.Equal("3;1;2;0;4", Batches(1));
        // The archive-size cap (the package span closes 2 and 0 apart too).
        Assert.Equal("3,1;2;0;4", Batches(2 * MiB));
        // The package-span cap.
        Assert.Equal("3,1,2;0,4", Batches(250 * MiB));
        Assert.Equal("3,1;2;0,4", Batches(150 * MiB));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed class SimulatedCrash : Exception;

    private sealed record OptionsBuilder(bool Consume)
    {
        public Action<PkgZarStage, int, long>? Observer { get; init; }

        public Action<long, long, long>? SourceReadObserver { get; init; }

        public PkgZarOptions Build() => new()
        {
            ConsumeSource = Consume,
            WindowBytes = TinyWindow,
            MaxParallelism = 3,
            AvailableSpace = _ => null,
            Observer = Observer,
            SourceReadObserver = SourceReadObserver,
        };
    }

    private static OptionsBuilder Options(bool consume) => new(consume);

    private byte[] ConsumeUninterrupted(byte[] bytes)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, $"reference-{Guid.NewGuid():N}")).FullName;
        var package = Path.Combine(directory, "game.pkg");
        File.WriteAllBytes(package, bytes);
        var destination = Path.Combine(directory, "game.zar");
        PkgZarConverter.Convert(package, destination, Options(consume: true).Build(), null, default);
        return File.ReadAllBytes(destination);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private Dictionary<string, byte[]> ExtractTree(byte[] bytes)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_directory, $"extract-{Guid.NewGuid():N}")).FullName;
        var package = Path.Combine(directory, "game.pkg");
        File.WriteAllBytes(package, bytes);
        var folder = Path.Combine(directory, "game");
        using (var opened = GamePackage.Open(package))
        {
            opened.Extract(folder);
        }

        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            tree[Path.GetRelativePath(folder, path).Replace('\\', '/')] = File.ReadAllBytes(path);
        }

        Directory.Delete(directory, recursive: true);
        return tree;
    }

    private static Dictionary<string, byte[]> ReadArchive(string path)
    {
        using var archive = ZArchive.Open(path);
        Assert.NotNull(archive);
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Walk(string prefix)
        {
            foreach (var entry in archive.GetEntries(archive.LookUp(prefix)))
            {
                var child = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                if (entry.IsDirectory)
                {
                    Walk(child);
                    continue;
                }

                var node = archive.LookUp(child);
                var data = new byte[entry.Size];
                Assert.Equal(data.Length, archive.Read(node, 0, data));
                tree[child] = data;
            }
        }

        Walk(string.Empty);
        return tree;
    }

    private static byte[] Build(Format format) => format switch
    {
        Format.Ps4 => BuildPs4(),
        Format.Ps5 => BuildPs5(scramble: false),
        Format.Ps5Scrambled => BuildPs5(scramble: true),
        Format.Ps5Small => Ps5PkgTests.BuildPackage(new Ps5PkgTests.Spec()).Bytes,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    // Half random (stored raw), half a repeating pattern (compresses).
    private static byte[] Content(int seed, int length)
    {
        var bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes.AsSpan(0, length / 2));
        for (var index = length / 2; index < length; index++)
        {
            bytes[index] = (byte)(index % 13 + seed);
        }

        return bytes;
    }

    private static readonly (string Path, int Length)[] GameFiles =
    [
        ("eboot.bin", 1_500_000),
        ("data/level0.bin", 2_100_000),
        ("data/empty.bin", 0),
        ("data/small.txt", 777),
        ("movie.bin", 1_700_000),
        ("sce_module/libc.prx", 0x8000),
    ];

    // A PS4 package with several files in uroot and one level of directories. Logical PFS blocks:
    // 0 header, 1 inodes, 2 super root, 3 uroot, then one block per subdirectory, then file data.
    private static byte[] BuildPs4()
    {
        const int BlockSize = PfscImage.BlockSize;
        var directories = GameFiles.Select(file => file.Path).Where(path => path.Contains('/')).Select(path => path[..path.IndexOf('/')]).Distinct().ToList();
        var firstData = 4 + directories.Count;
        var logical = new List<byte[]>();
        var header = new byte[BlockSize];
        var inodes = new byte[BlockSize];
        var superRoot = new byte[BlockSize];
        var userRoot = new byte[BlockSize];
        var directoryBlocks = directories.Select(_ => new byte[BlockSize]).ToList();
        logical.AddRange([header, inodes, superRoot, userRoot, .. directoryBlocks]);

        // Inodes: 0 super root, 1 uroot, 2.. directories, then files.
        PkgTests.WriteInode(inodes, 0, PfsInode.ModeDirectory, BlockSize, 1, 2);
        PkgTests.WriteInode(inodes, 1, PfsInode.ModeDirectory, BlockSize, 1, 3);
        var offset = PkgTests.WriteDirent(superRoot, 0, 1, PfsDirentType.File, "flat_path_table");
        PkgTests.WriteDirent(superRoot, offset, 1, PfsDirentType.Directory, "uroot");
        var rootAt = PkgTests.WriteDirent(userRoot, 0, 1, PfsDirentType.CurrentDirectory, ".");
        rootAt = PkgTests.WriteDirent(userRoot, rootAt, 0x7FFF, PfsDirentType.ParentDirectory, "..");
        var directoryAt = new int[directories.Count];
        for (var index = 0; index < directories.Count; index++)
        {
            var inode = 2 + index;
            PkgTests.WriteInode(inodes, inode, PfsInode.ModeDirectory, BlockSize, 1, (uint)(4 + index));
            rootAt = PkgTests.WriteDirent(userRoot, rootAt, inode, PfsDirentType.Directory, directories[index]);
            directoryAt[index] = PkgTests.WriteDirent(directoryBlocks[index], 0, inode, PfsDirentType.CurrentDirectory, ".");
        }

        var nextInode = 2 + directories.Count;
        var location = firstData;
        foreach (var (path, length) in GameFiles)
        {
            var data = Content(path.Length, length);
            var blocks = (length + BlockSize - 1) / BlockSize;
            PkgTests.WriteInode(inodes, nextInode, PfsInode.ModeFile, length, (uint)blocks, (uint)location);
            var slash = path.IndexOf('/');
            if (slash < 0)
            {
                rootAt = PkgTests.WriteDirent(userRoot, rootAt, nextInode, PfsDirentType.File, path);
            }
            else
            {
                var index = directories.IndexOf(path[..slash]);
                directoryAt[index] = PkgTests.WriteDirent(directoryBlocks[index], directoryAt[index], nextInode, PfsDirentType.File, path[(slash + 1)..]);
            }

            for (var block = 0; block < blocks; block++)
            {
                var bytes = new byte[BlockSize];
                Array.Copy(data, block * BlockSize, bytes, 0, Math.Min(BlockSize, length - block * BlockSize));
                logical.Add(bytes);
            }

            location += blocks;
            nextInode++;
        }

        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x20), BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x28), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x38), logical.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x30), nextInode);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x40), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x48), 0);

        var stored = new List<byte[]>();
        foreach (var block in logical)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(block);
            }

            stored.Add(compressed.Length < BlockSize ? compressed.ToArray() : block);
        }

        const int TableOffset = 0x30;
        var dataStart = TableOffset + (stored.Count + 1) * 8;
        var pfscHeader = new byte[dataStart];
        BinaryPrimitives.WriteUInt32LittleEndian(pfscHeader, PfscImage.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(pfscHeader.AsSpan(0x0C), BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x10), BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x18), TableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x20), dataStart);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x28), (long)stored.Count * BlockSize);
        long position = dataStart;
        for (var index = 0; index < stored.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(TableOffset + index * 8), position);
            position += stored[index].Length;
        }

        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(TableOffset + stored.Count * 8), position);
        var image = new MemoryStream();
        image.Write(pfscHeader);
        foreach (var block in stored)
        {
            image.Write(block);
        }

        return PkgTests.BuildSyntheticPkg(
            image.ToArray(),
            (0x1000, "PARAM"u8.ToArray()),
            (0x1200, Content(99, 3000)),
            (0x1220, Content(98, 100_000)));
    }

    // A PS5 package whose inner image holds GameFiles plus a uroot sce_sys/param.json that
    // overrides the CNT copy, as on real packages. Every 256 KiB block is stored raw except a
    // 32 KiB one stored Kraken; the outer image is laid out in order unless scrambled.
    private static byte[] BuildPs5(bool scramble)
    {
        const long UblockSize = 0x40000;
        var files = GameFiles.Select(file => (file.Path, Data: Content(file.Path.Length + 7, file.Length))).ToList();
        files.Add(("sce_sys/param.json", System.Text.Encoding.UTF8.GetBytes("""{"titleId":"PPSA01234","override":true}""")));

        // Logical layout: files on 4 KiB boundaries, then the metadata tail on a 64 KiB boundary.
        var placed = new List<(string Path, byte[] Data, long Offset)>();
        long cursor = 0;
        foreach (var (path, data) in files)
        {
            placed.Add((path, data, cursor));
            cursor = (cursor + Math.Max(data.Length, 1) + 0xFFF) & ~0xFFFL;
        }

        var tailBase = (cursor + 0xFFFF) & ~0xFFFFL;
        var directories = files.Select(file => file.Path).Where(path => path.Contains('/')).Select(path => path[..path.IndexOf('/')]).Distinct().ToList();
        // Inodes: 0 root, 1 uroot, 2.. directories, then files.
        var inodeCount = 2 + directories.Count + files.Count;
        var inodeBlocks = (inodeCount + 23) / 24;
        var directoryBlock0 = 1 + inodeBlocks;
        var tailLength = (long)(directoryBlock0 + 2 + directories.Count) * 0x1000;
        var mount = tailBase + tailLength;
        var logical = new byte[mount];
        foreach (var (_, data, offset) in placed)
        {
            data.CopyTo(logical, offset);
        }

        var tail = logical.AsSpan((int)tailBase);
        BinaryPrimitives.WriteUInt64LittleEndian(tail, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(tail[8..], Ps5OuterPfs.PfsMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(tail[0x1C..], 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(tail[0x20..], 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(tail[0x30..], (ulong)inodeCount);
        BinaryPrimitives.WriteUInt64LittleEndian(tail[0x38..], (ulong)(mount / 0x1000));
        void Inode(int index, ushort mode, long size, long where)
        {
            var at = 0x1000 + index / 24 * 0x1000 + index % 24 * 0xA8;
            BinaryPrimitives.WriteUInt16LittleEndian(logical.AsSpan((int)tailBase + at), mode);
            BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan((int)tailBase + at + 8), (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(logical.AsSpan((int)tailBase + at + 0x60), (ulong)where);
        }

        long DirectoryAt(int block) => tailBase + (directoryBlock0 + block) * 0x1000L;
        const ushort DirMode = 0x41ED;
        const ushort FileMode = 0x81A4;
        Inode(0, DirMode, 0x1000, DirectoryAt(0));
        Inode(1, DirMode, 0x1000, DirectoryAt(1));
        var rootEntries = new List<(uint, uint, string)> { (1, 4, "."), (0, 5, "..") };
        var childEntries = directories.Select(name => new List<(uint, uint, string)> { (0, 3, "."), (1, 3, "..") }).ToList();
        for (var index = 0; index < directories.Count; index++)
        {
            Inode(2 + index, DirMode, 0x1000, DirectoryAt(2 + index));
            rootEntries.Add(((uint)(2 + index), 3, directories[index]));
        }

        for (var index = 0; index < placed.Count; index++)
        {
            var (path, data, offset) = placed[index];
            var inode = 2 + directories.Count + index;
            Inode(inode, FileMode, data.Length, offset);
            var slash = path.IndexOf('/');
            if (slash < 0)
            {
                rootEntries.Add(((uint)inode, 2, path));
            }
            else
            {
                childEntries[directories.IndexOf(path[..slash])].Add(((uint)inode, 2, path[(slash + 1)..]));
            }
        }

        Ps5PkgTests.DirectoryBlock(0x1000, (0, 3, "."), (0, 3, ".."), (1, 3, "uroot")).CopyTo(logical, DirectoryAt(0));
        Ps5PkgTests.DirectoryBlock(0x1000, [.. rootEntries]).CopyTo(logical, DirectoryAt(1));
        for (var index = 0; index < directories.Count; index++)
        {
            Ps5PkgTests.DirectoryBlock(0x1000, [.. childEntries[index]]).CopyTo(logical, DirectoryAt(2 + index));
        }

        // NAPS boundaries: every file start, the tail, and the mount size.
        var boundaries = placed.Select(file => file.Offset).Append(tailBase).Distinct().Order().ToList();
        var entries = boundaries.Select(offset => (Offset: offset, Kind: (byte)0)).Append((Offset: mount, Kind: (byte)0x40)).ToList();
        while (entries.Count % 8 != 0)
        {
            entries.Insert(0, (Offset: 0, Kind: 0));
        }

        // Blocks: up to 256 KiB, cut at every boundary; the 32 KiB module is stored Kraken.
        var stored = new MemoryStream();
        var records = new List<byte[]>();
        long at = 0;
        var cuts = boundaries.Append(mount).ToList();
        while (at < mount)
        {
            var next = cuts.First(cut => cut > at);
            var length = (int)Math.Min(UblockSize, next - at);
            var block = logical.AsSpan((int)at, length).ToArray();
            var kraken = length == 0x8000 && at != tailBase;
            records.Add(Ps5PkgTests.CblockRecord((uint)(stored.Length & 0x3FFFF), kraken ? 2UL : 0));
            stored.Write(kraken ? Ps5PkgTests.KrakenLiteralChunk(block) : block);
            at += length;
        }

        records.Add(Ps5PkgTests.CblockRecord((uint)(stored.Length & 0x3FFFF), 0));
        var image = stored.ToArray();
        var ublocks = records.Count - 1;
        var outer = (int)((image.Length + 0xFFFF) / 0x10000);
        outer += outer % 2;
        var u2c = (ublocks + 8) >> 3;
        var fileOffsetsAt = 16 + outer * 8;
        var cblocksAt = (fileOffsetsAt + entries.Count * 6 + u2c * 10 + 7) & ~7;
        var naps = new byte[cblocksAt + records.Count * 9];
        BinaryPrimitives.WriteUInt64LittleEndian(naps, (ulong)(uint)(entries.Count - 1) | (2UL << 24) | ((ulong)(uint)ublocks << 32));
        BinaryPrimitives.WriteUInt64LittleEndian(naps.AsSpan(8), (ulong)(uint)outer | ((ulong)(uint)(records.Count - 2) << 24));
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = fileOffsetsAt + index * 6;
            for (var b = 0; b < 5; b++)
            {
                naps[entry + b] = (byte)(entries[index].Offset >> (8 * b));
            }

            naps[entry + 5] = entries[index].Kind;
        }

        for (var index = 0; index < records.Count; index++)
        {
            records[index].CopyTo(naps, cblocksAt + index * 9);
        }

        var pfs = Ps5PkgTests.BuildOuterPfs(0x10000, "PPRPLAIN-NOAUTH!", [("pfs_image.dat", image), ("naps_pkg_layout.dat", naps)], scramble);
        return Ps5PkgTests.AssemblePackage(new Ps5PkgTests.Spec(), pfs, new Dictionary<string, byte[]>()).Bytes;
    }
}

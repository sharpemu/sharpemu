// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.GameContent;
using Xunit;
using ZstdSharp;

namespace SharpEmu.Libs.Tests.GameContent;

public sealed class ZArchiveFileSystemTests
{
    private const int BlockSize = 64 * 1024;

    [Fact]
    public void ReadsSeeksAndEnumeratesAcrossCompressedAndStoredBlocks()
    {
        var eboot = Enumerable.Range(0, BlockSize * 3 + 123)
            .Select(static index => (byte)(index % 251))
            .ToArray();
        var longName = $"caf\u00E9-{new string('a', 130)}.bin";
        var path = CreateArchive(
            new Dictionary<string, byte[]>
            {
                ["eboot.bin"] = eboot,
                ["Media/data.bin"] = "archive-data"u8.ToArray(),
                [$"Media/{longName}"] = [1, 2, 3],
                ["sce_sys/param.json"] = ParamJson,
            },
            storedBlocks: new HashSet<int> { 1 });

        try
        {
            using var archive = new ZArchiveFileSystem(path);
            Assert.True(archive.TryGetEntry("/EBOOT.BIN", out var entry));
            Assert.Equal(eboot.Length, entry.Length);
            Assert.True(archive.TryGetEntry("media\\DATA.bin", out var nested));
            Assert.Equal("data.bin", nested.Name);
            Assert.True(archive.TryGetEntry($"Media/{longName}", out var longEntry));
            Assert.Equal(3, longEntry.Length);

            var root = archive.EnumerateDirectory("/");
            Assert.Equal(["eboot.bin", "Media", "sce_sys"], root.Select(static item => item.Name));

            using var stream = archive.OpenRead("eboot.bin");
            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);
            Assert.False(stream.CanWrite);
            Assert.Equal(eboot.Length, stream.Length);

            stream.Position = BlockSize - 19;
            var crossBlock = new byte[BlockSize + 41];
            Assert.Equal(crossBlock.Length, stream.Read(crossBlock));
            Assert.Equal(eboot.AsSpan(BlockSize - 19, crossBlock.Length).ToArray(), crossBlock);

            Assert.Equal(eboot.Length - 10, stream.Seek(-10, SeekOrigin.End));
            var tail = new byte[32];
            Assert.Equal(10, stream.Read(tail));
            Assert.Equal(0, stream.Read(tail));
            Assert.Equal(eboot.AsSpan(eboot.Length - 10).ToArray(), tail.AsSpan(0, 10).ToArray());
            Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReusesCachedCompressedBlocks()
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = new byte[BlockSize * 65],
        });

        try
        {
            using var archive = new ZArchiveFileSystem(path);
            using var stream = archive.OpenRead("eboot.bin");
            var one = new byte[1];
            for (var block = 0; block < 65; block++)
            {
                stream.Position = (long)block * BlockSize;
                Assert.Equal(1, stream.Read(one));
            }

            Assert.Equal(65, archive.DecompressionCount);
            stream.Position = 64L * BlockSize;
            Assert.Equal(1, stream.Read(one));
            Assert.Equal(65, archive.DecompressionCount);
            stream.Position = 0;
            Assert.Equal(1, stream.Read(one));
            Assert.Equal(66, archive.DecompressionCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IndependentStreamsReadConcurrentlyAndEmptyFilesBehaveNormally()
    {
        var data = Enumerable.Range(0, BlockSize * 2 + 37)
            .Select(static index => (byte)(index % 239))
            .ToArray();
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = data,
            ["empty.dat"] = [],
        });

        try
        {
            using var archive = new ZArchiveFileSystem(path);
            using var first = archive.OpenRead("eboot.bin");
            using var second = archive.OpenRead("EBOOT.BIN");
            var firstBytes = new byte[BlockSize + 23];
            var secondBytes = new byte[BlockSize + 11];
            first.Position = 17;
            second.Position = BlockSize - 5;

            await Task.WhenAll(
                Task.Run(() => first.ReadExactly(firstBytes)),
                Task.Run(() => second.ReadExactly(secondBytes)));

            Assert.Equal(data.AsSpan(17, firstBytes.Length).ToArray(), firstBytes);
            Assert.Equal(data.AsSpan(BlockSize - 5, secondBytes.Length).ToArray(), secondBytes);
            Assert.Equal(17 + firstBytes.Length, first.Position);
            Assert.Equal(BlockSize - 5 + secondBytes.Length, second.Position);

            using var empty = archive.OpenRead("empty.dat");
            Assert.Equal(0, empty.Length);
            Assert.Equal(-1, empty.ReadByte());
            Assert.Equal(0, empty.Seek(0, SeekOrigin.End));
            Assert.Throws<NotSupportedException>(() => empty.SetLength(1));
            Assert.Throws<NotSupportedException>(() => empty.Write([], 0, 0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingAndTraversalPathsUseNormalFilesystemErrors()
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = new byte[1],
            ["Media/data.bin"] = new byte[1],
        });

        try
        {
            using var archive = new ZArchiveFileSystem(path);
            Assert.False(archive.TryGetEntry("missing.bin", out _));
            Assert.Throws<FileNotFoundException>(() => archive.OpenRead("missing.bin"));
            Assert.Throws<DirectoryNotFoundException>(() => archive.EnumerateDirectory("eboot.bin"));
            Assert.Throws<ArgumentException>(() => archive.TryGetEntry("Media/../eboot.bin", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GameSourceReadsMetadataAndUsesArchiveStorageSize()
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = new byte[100],
            ["sce_sys/param.json"] = ParamJson,
        });

        try
        {
            using var source = GameSource.Open(path);
            var metadata = GameMetadataReader.Read(source.FileSystem);
            Assert.True(source.IsArchive);
            Assert.Equal("eboot.bin", source.ExecutablePath);
            Assert.Equal("Test Game", metadata.Title);
            Assert.Equal("PPSA00001", metadata.TitleId);
            Assert.Equal("01.000.000", metadata.Version);
            Assert.Equal(new FileInfo(path).Length, source.GetStorageSize());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GameSourceRejectsArchiveWithoutRootExecutable()
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["nested/eboot.bin"] = new byte[100],
        });

        try
        {
            var exception = Assert.Throws<FileNotFoundException>(() => GameSource.Open(path));
            Assert.Contains("root", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectsMalformedFooter(int corruption)
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = new byte[100],
        });

        try
        {
            var bytes = File.ReadAllBytes(path);
            var footer = bytes.Length - 144;
            switch (corruption)
            {
                case 0:
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), 0xDEADBEEF);
                    break;
                case 1:
                    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(footer + 128), (ulong)bytes.Length + 1);
                    break;
                case 2:
                    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(footer + 16), 0);
                    break;
            }

            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => new ZArchiveFileSystem(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsUnsupportedVersion()
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = new byte[100],
        });

        try
        {
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 8), 2);
            File.WriteAllBytes(path, bytes);
            Assert.Throws<NotSupportedException>(() => new ZArchiveFileSystem(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsInvalidFileTree()
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = new byte[100],
        });

        try
        {
            var bytes = File.ReadAllBytes(path);
            var footer = bytes.Length - 144;
            var treeOffset = checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(footer + 48)));
            var rootWord = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(treeOffset));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(treeOffset), rootWord | 0x80000000U);
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => new ZArchiveFileSystem(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReportsCorruptCompressedBlockWhenRead()
    {
        var path = CreateArchive(new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = new byte[100],
        });

        try
        {
            var bytes = File.ReadAllBytes(path);
            bytes[0] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            using var archive = new ZArchiveFileSystem(path);
            using var stream = archive.OpenRead("eboot.bin");
            Assert.Throws<InvalidDataException>(() => stream.ReadByte());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PhysicalGameSourceRemainsRootedAndReportsInstallSize()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpemu-game-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        File.WriteAllBytes(Path.Combine(root, "eboot.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(root, "sce_sys", "param.json"), ParamJson);

        try
        {
            using var source = GameSource.Open(Path.Combine(root, "eboot.bin"));
            Assert.False(source.IsArchive);
            Assert.Equal(11 + ParamJson.Length, source.GetStorageSize());
            Assert.Throws<ArgumentException>(() => source.FileSystem.OpenRead("../outside.bin"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryPrefersDecryptedExecutableForTheSamePhysicalGame(bool decryptedFirst)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sharpemu-game-discovery-{Guid.NewGuid():N}");
        var decryptedDirectory = Path.Combine(root, "decrypted");
        Directory.CreateDirectory(decryptedDirectory);
        Directory.CreateDirectory(Path.Combine(root, "Media"));
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        var retailExecutable = Path.Combine(root, "eboot.bin");
        var decryptedExecutable = Path.Combine(decryptedDirectory, "eboot.bin");
        File.WriteAllBytes(retailExecutable, [0x54, 0x14, 0xF5, 0xEE]);
        File.WriteAllBytes(decryptedExecutable, [0x7F, 0x45, 0x4C, 0x46]);
        File.WriteAllBytes(Path.Combine(root, "sce_sys", "param.json"), ParamJson);
        var candidates = decryptedFirst
            ? new[] { decryptedExecutable, retailExecutable }
            : new[] { retailExecutable, decryptedExecutable };

        IReadOnlyList<GameSource>? sources = null;
        try
        {
            sources = GameSourceDiscovery.OpenPreferredSources(candidates);
            var source = Assert.Single(sources);
            Assert.Equal(Path.GetFullPath(decryptedExecutable), source.SourcePath);
            Assert.Equal(Path.GetFullPath(root), source.PhysicalRootPath);
            Assert.Equal("decrypted/eboot.bin", source.ExecutablePath);
            var metadata = GameMetadataReader.Read(source.FileSystem);
            Assert.Equal("Test Game", metadata.Title);
            Assert.Equal("PPSA00001", metadata.TitleId);
            Assert.Equal("01.000.000", metadata.Version);
        }
        finally
        {
            if (sources is not null)
            {
                foreach (var source in sources)
                {
                    source.Dispose();
                }
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] ParamJson =>
        """
        {
          "titleId": "PPSA00001",
          "contentVersion": "01.000.000",
          "localizedParameters": {
            "defaultLanguage": "en-US",
            "en-US": { "titleName": "Test Game" }
          }
        }
        """u8.ToArray();

    private static string CreateArchive(
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlySet<int>? storedBlocks = null)
    {
        var root = new FixtureNode(string.Empty, isFile: false);
        using var raw = new MemoryStream();
        foreach (var (path, data) in files)
        {
            var components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = root;
            for (var i = 0; i < components.Length - 1; i++)
            {
                if (!parent.Children.TryGetValue(components[i], out var directory))
                {
                    directory = new FixtureNode(components[i], isFile: false);
                    parent.Children.Add(components[i], directory);
                }

                parent = directory;
            }

            var file = new FixtureNode(components[^1], isFile: true)
            {
                Offset = checked((ulong)raw.Length),
                Size = checked((ulong)data.Length),
            };
            parent.Children.Add(file.Name, file);
            raw.Write(data);
        }

        AssignTreeIndices(root);
        var nodes = FlattenTree(root);
        using var output = new MemoryStream();
        var rawBytes = raw.ToArray();
        var blockCount = Math.Max(1, (rawBytes.Length + BlockSize - 1) / BlockSize);
        var records = new List<FixtureOffsetRecord>();
        using var compressor = new Compressor(6);
        for (var block = 0; block < blockCount; block++)
        {
            if (block % 16 == 0)
            {
                records.Add(new FixtureOffsetRecord { BaseOffset = checked((ulong)output.Length) });
            }

            var padded = new byte[BlockSize];
            var sourceOffset = block * BlockSize;
            if (sourceOffset < rawBytes.Length)
            {
                rawBytes.AsSpan(sourceOffset, Math.Min(BlockSize, rawBytes.Length - sourceOffset)).CopyTo(padded);
            }

            var compressed = compressor.Wrap(padded).ToArray();
            var stored = storedBlocks?.Contains(block) == true || compressed.Length >= BlockSize;
            var blockBytes = stored ? padded : compressed;
            records[^1].Sizes[block % 16] = checked((ushort)(blockBytes.Length - 1));
            output.Write(blockBytes);
        }

        var compressedSection = new FixtureSection(0, checked((ulong)output.Length));
        while (output.Length % 8 != 0)
        {
            output.WriteByte(0);
        }

        var offsetSectionStart = checked((ulong)output.Length);
        foreach (var record in records)
        {
            Span<byte> encoded = new byte[40];
            BinaryPrimitives.WriteUInt64BigEndian(encoded, record.BaseOffset);
            for (var i = 0; i < 16; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(encoded[(8 + (i * 2))..], record.Sizes[i]);
            }

            output.Write(encoded);
        }

        var offsetSection = new FixtureSection(offsetSectionStart, checked((ulong)output.Length) - offsetSectionStart);
        var nameOffsets = new Dictionary<FixtureNode, uint>();
        var nameSectionStart = checked((ulong)output.Length);
        foreach (var node in nodes.Skip(1))
        {
            nameOffsets[node] = checked((uint)(output.Length - (long)nameSectionStart));
            var name = Encoding.Latin1.GetBytes(node.Name);
            if (name.Length < 0x80)
            {
                output.WriteByte((byte)name.Length);
            }
            else
            {
                output.WriteByte((byte)((name.Length & 0x7F) | 0x80));
                output.WriteByte((byte)(name.Length >> 7));
            }

            output.Write(name);
        }

        var nameSection = new FixtureSection(nameSectionStart, checked((ulong)output.Length) - nameSectionStart);
        var treeSectionStart = checked((ulong)output.Length);
        foreach (var node in nodes)
        {
            Span<byte> encoded = new byte[16];
            var nameAndType = ReferenceEquals(node, root) ? 0x7FFFFFFFU : nameOffsets[node];
            if (node.IsFile)
            {
                nameAndType |= 0x80000000U;
            }

            BinaryPrimitives.WriteUInt32BigEndian(encoded, nameAndType);
            if (node.IsFile)
            {
                BinaryPrimitives.WriteUInt32BigEndian(encoded[4..], (uint)node.Offset);
                BinaryPrimitives.WriteUInt32BigEndian(encoded[8..], (uint)node.Size);
                var high = ((uint)(node.Offset >> 32) & 0xFFFF) |
                    ((uint)(node.Size >> 16) & 0xFFFF0000U);
                BinaryPrimitives.WriteUInt32BigEndian(encoded[12..], high);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(encoded[4..], node.ChildStart);
                BinaryPrimitives.WriteUInt32BigEndian(encoded[8..], checked((uint)node.Children.Count));
            }

            output.Write(encoded);
        }

        var treeSection = new FixtureSection(treeSectionStart, checked((ulong)output.Length) - treeSectionStart);
        var emptySection = new FixtureSection(checked((ulong)output.Length), 0);
        var totalSize = checked((ulong)output.Length + 144);
        Span<byte> footer = stackalloc byte[144];
        var sections = new[] { compressedSection, offsetSection, nameSection, treeSection, emptySection, emptySection };
        for (var i = 0; i < sections.Length; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(footer[(i * 16)..], sections[i].Offset);
            BinaryPrimitives.WriteUInt64BigEndian(footer[(i * 16 + 8)..], sections[i].Size);
        }

        BinaryPrimitives.WriteUInt64BigEndian(footer[128..], totalSize);
        BinaryPrimitives.WriteUInt32BigEndian(footer[136..], 0x61BF3A01);
        BinaryPrimitives.WriteUInt32BigEndian(footer[140..], 0x169F52D6);
        output.Write(footer);

        var archivePath = Path.Combine(Path.GetTempPath(), $"sharpemu-zarchive-{Guid.NewGuid():N}.zar");
        File.WriteAllBytes(archivePath, output.ToArray());
        return archivePath;
    }

    private static void AssignTreeIndices(FixtureNode root)
    {
        var queue = new Queue<FixtureNode>();
        root.Index = 0;
        queue.Enqueue(root);
        uint nextIndex = 1;
        while (queue.Count != 0)
        {
            var directory = queue.Dequeue();
            directory.ChildStart = nextIndex;
            foreach (var child in directory.Children.Values.OrderBy(static child => child.Name, StringComparer.OrdinalIgnoreCase))
            {
                child.Index = checked((int)nextIndex++);
                if (!child.IsFile)
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    private static FixtureNode[] FlattenTree(FixtureNode root)
    {
        var nodes = new FixtureNode[CountNodes(root)];
        var pending = new Stack<FixtureNode>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var node = pending.Pop();
            nodes[node.Index] = node;
            foreach (var child in node.Children.Values)
            {
                pending.Push(child);
            }
        }

        return nodes;
    }

    private static int CountNodes(FixtureNode node) =>
        1 + node.Children.Values.Sum(CountNodes);

    private sealed class FixtureNode(string name, bool isFile)
    {
        public string Name { get; } = name;
        public bool IsFile { get; } = isFile;
        public SortedDictionary<string, FixtureNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Index { get; set; }
        public uint ChildStart { get; set; }
        public ulong Offset { get; set; }
        public ulong Size { get; set; }
    }

    private sealed class FixtureOffsetRecord
    {
        public ulong BaseOffset { get; init; }
        public ushort[] Sizes { get; } = new ushort[16];
    }

    private readonly record struct FixtureSection(ulong Offset, ulong Size);
}

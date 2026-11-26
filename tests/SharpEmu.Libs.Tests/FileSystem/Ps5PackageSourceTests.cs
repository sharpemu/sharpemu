// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE.FileSystem;
using SharpEmu.HLE.FileSystem.Pkg;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

// The mounted view of a PS5 package: same files and bytes as Extract, read in place. Uses the
// synthetic package builder of Ps5PkgTests.
public sealed partial class Ps5PkgTests
{
    // For tests of other classes that need a mountable package.
    internal static (byte[] Bytes, IReadOnlyDictionary<string, byte[]> Files) BuildSamplePackage()
    {
        var built = BuildPackage(new Spec());
        return (built.Bytes, built.ExpectedFiles);
    }

    private static void WithSource(Action<Ps5PackageSource, IReadOnlyDictionary<string, byte[]>> action, long cacheBytes = 256L << 20, int readAhead = 8)
    {
        var built = BuildPackage(new Spec());
        WithFile(built.Bytes, path =>
        {
            using var source = Ps5PackageSource.Open(path, cacheBytes, readAhead);
            action(source, built.ExpectedFiles);
        });
    }

    private static List<string> ListFiles(IApp0Source source, int directory, string prefix)
    {
        var files = new List<string>();
        foreach (var entry in source.GetEntries(directory))
        {
            var path = prefix + entry.Name;
            if (entry.IsDirectory)
            {
                files.AddRange(ListFiles(source, source.LookUp(path), path + "/"));
            }
            else
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static byte[] ReadAll(IApp0Source source, int node, int step)
    {
        var size = (int)source.GetFileSize(node);
        var bytes = new byte[size];
        var done = 0;
        while (done < size)
        {
            var read = source.Read(node, (ulong)done, bytes.AsSpan(done, Math.Min(step, size - done)));
            Assert.True(read > 0);
            done += read;
        }

        return bytes;
    }

    [Fact]
    public void MountedViewListsTheFilesExtractWritesWithTheSameBytes()
    {
        var built = BuildPackage(new Spec());
        var destination = Path.Combine(Path.GetTempPath(), $"sharpemu-ps5pkg-out-{Guid.NewGuid():N}");
        try
        {
            WithFile(built.Bytes, path =>
            {
                using (var package = Ps5Package.Open(path))
                {
                    package.Extract(destination);
                }

                var written = ReadTree(destination);
                using var source = Ps5PackageSource.Open(path);
                var listed = ListFiles(source, source.LookUp(string.Empty), string.Empty);
                Assert.Equal(written.Keys.OrderBy(k => k, StringComparer.Ordinal), listed.OrderBy(k => k, StringComparer.Ordinal));
                foreach (var (name, bytes) in written)
                {
                    var node = source.LookUp(name);
                    Assert.True(source.IsFile(node), name);
                    Assert.Equal((ulong)bytes.Length, source.GetFileSize(node));
                    // One read, then small steps that cross every block boundary at odd offsets.
                    Assert.True(bytes.AsSpan().SequenceEqual(ReadAll(source, node, int.MaxValue)), name);
                    Assert.True(bytes.AsSpan().SequenceEqual(ReadAll(source, node, 97)), name);
                }
            });
        }
        finally
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadsStopAtTheEndAndReadSparseBlocksAsZeros()
    {
        WithSource((source, files) =>
        {
            var data = source.LookUp("assets/data.bin");
            var expected = files["assets/data.bin"];
            var buffer = new byte[0x1000];

            // A read that starts inside the file and runs past its end is short.
            Assert.Equal(0x100, source.Read(data, 0x200, buffer));
            Assert.True(buffer.AsSpan(0, 0x100).SequenceEqual(expected.AsSpan(0x200)));
            Assert.Equal(0, source.Read(data, 0x300, buffer));
            Assert.Equal(0, source.Read(data, 0x301, buffer));
            Assert.Equal(0, source.Read(data, ulong.MaxValue, buffer));
            Assert.Equal(0, source.Read(data, 0, Span<byte>.Empty));

            // The file at 0x7F00 crosses from the raw block into the Kraken one at 0x8000.
            var across = new byte[0x300];
            Assert.Equal(0x300, source.Read(data, 0, across));
            Assert.True(across.AsSpan().SequenceEqual(expected));

            var sparse = source.LookUp("assets/sparse.bin");
            var zeros = new byte[0x2000];
            Array.Fill(zeros, (byte)0xAA);
            Assert.Equal(0x2000, source.Read(sparse, 0, zeros));
            Assert.All(zeros, b => Assert.Equal(0, b));

            var empty = source.LookUp("assets/empty.bin");
            Assert.True(source.IsFile(empty));
            Assert.Equal(0, source.Read(empty, 0, buffer));

            // Not a file: a directory, a missing node.
            Assert.Equal(0, source.Read(source.LookUp("assets"), 0, buffer));
            Assert.Equal(0, source.Read(IApp0Source.InvalidNode, 0, buffer));
            Assert.Equal(0, source.Read(int.MaxValue, 0, buffer));
        });
    }

    [Fact]
    public void LookUpFollowsTheSameRulesAsZArchive()
    {
        WithSource((source, _) =>
        {
            var root = source.LookUp(string.Empty);
            Assert.True(source.IsDirectory(root));
            Assert.Equal(root, source.LookUp("/"));
            Assert.Equal(root, source.LookUp("\\"));

            // No case, either separator, doubled separators.
            var node = source.LookUp("Assets\\SUB//deep.BIN");
            Assert.True(source.IsFile(node));
            Assert.Equal(node, source.LookUp("assets/sub/deep.bin"));
            Assert.Equal(node, source.LookUp("/assets/sub/deep.bin/"));

            Assert.Equal(IApp0Source.InvalidNode, source.LookUp("assets/missing.bin"));
            Assert.Equal(IApp0Source.InvalidNode, source.LookUp("assets/sub/deep.bin/below"));
            Assert.Equal(IApp0Source.InvalidNode, source.LookUp("assets/../eboot.bin"));
            Assert.Equal(IApp0Source.InvalidNode, source.LookUp("readme"));

            Assert.False(source.IsFile(root));
            Assert.False(source.IsDirectory(node));
            Assert.False(source.IsFile(IApp0Source.InvalidNode));
            Assert.False(source.IsDirectory(IApp0Source.InvalidNode));
            Assert.Equal(0UL, source.GetFileSize(root));
            Assert.Empty(source.GetEntries(node));
            Assert.Empty(source.GetEntries(IApp0Source.InvalidNode));

            var entries = source.GetEntries(source.LookUp("assets"));
            Assert.Equal(
                ["data.bin", "empty.bin", "sparse.bin", "sub"],
                entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.True(entries.Single(e => e.Name == "sub").IsDirectory);
            Assert.Equal(0x300UL, entries.Single(e => e.Name == "data.bin").Size);
        });
    }

    [Fact]
    public void InnerFilesOverrideMetadataOfTheSameName()
    {
        var built = BuildPackage(new Spec());
        WithFile(built.Bytes, path =>
        {
            using var package = Ps5Package.Open(path);
            var (_, files) = package.LoadTree();
            var eboot = files.Single(f => f.Components is ["eboot.bin"]);

            // Install writes sce_sys first and the inner files after, so an inner file wins, even
            // when its name differs in case.
            using var source = new Ps5PackageSource(
                path,
                package,
                [new Ps5InnerFile(["SCE_SYS", "Icon0.png"], eboot.Logical, eboot.Size)],
                256L << 20,
                0);
            var icon = source.LookUp("sce_sys/icon0.png");
            Assert.Equal((ulong)eboot.Size, source.GetFileSize(icon));
            Assert.True(built.ExpectedFiles["eboot.bin"].AsSpan().SequenceEqual(ReadAll(source, icon, 4096)));

            // Other metadata stays, held in memory.
            var param = source.LookUp("sce_sys/param.json");
            Assert.True(built.ExpectedFiles["sce_sys/param.json"].AsSpan().SequenceEqual(ReadAll(source, param, 7)));
            Assert.Single(source.GetEntries(source.LookUp("sce_sys")), e => e.Name.Equals("icon0.png", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("x:y")]
    [InlineData("")]
    public void UnsafeNamesAreRefusedWhenBuildingTheView(string name)
    {
        var built = BuildPackage(new Spec());
        WithFile(built.Bytes, path =>
        {
            using var package = Ps5Package.Open(path);
            Assert.Throws<InvalidDataException>(() =>
                new Ps5PackageSource(path, package, [new Ps5InnerFile(["assets", name], 0, 1)], 1 << 20, 0));
        });
    }

    [Fact]
    public void AFileAndADirectoryAtOnePathAreRefused()
    {
        var built = BuildPackage(new Spec());
        WithFile(built.Bytes, path =>
        {
            using var package = Ps5Package.Open(path);
            Assert.Throws<InvalidDataException>(() =>
                new Ps5PackageSource(path, package, [new Ps5InnerFile(["assets"], 0, 1)], 1 << 20, 0));
            Assert.Throws<InvalidDataException>(() =>
                new Ps5PackageSource(path, package, [new Ps5InnerFile(["eboot.bin", "below"], 0, 1)], 1 << 20, 0));
        });
    }

    [Theory]
    [InlineData(256L << 20, 8)]
    [InlineData(0x10000, 4)]
    [InlineData(1, 0)]
    public void ConcurrentReadsReturnTheRightBytes(long cacheBytes, int readAhead)
    {
        WithSource(
            (source, files) =>
            {
                var nodes = files.Keys.Select(name => (Name: name, Node: source.LookUp(name))).ToArray();
                var failures = new ConcurrentQueue<string>();
                Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, worker =>
                {
                    var random = new System.Random(worker);
                    for (var round = 0; round < 400; round++)
                    {
                        var (name, node) = nodes[random.Next(nodes.Length)];
                        var expected = files[name];
                        if (expected.Length == 0)
                        {
                            continue;
                        }

                        if (round % 3 == 0)
                        {
                            // Sequential walk, which also drives the read-ahead.
                            var step = random.Next(1, 700);
                            if (!expected.AsSpan().SequenceEqual(ReadAll(source, node, step)))
                            {
                                failures.Enqueue($"{name} sequential step {step}");
                            }

                            continue;
                        }

                        var offset = random.Next(expected.Length);
                        var buffer = new byte[random.Next(1, expected.Length + 64)];
                        var read = source.Read(node, (ulong)offset, buffer);
                        var want = Math.Min(buffer.Length, expected.Length - offset);
                        if (read != want || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, want)))
                        {
                            failures.Enqueue($"{name} at {offset} length {buffer.Length}");
                        }
                    }
                });
                Assert.Empty(failures);
            },
            cacheBytes,
            readAhead);
    }

    [Fact]
    public void ReadsAfterDisposeFail()
    {
        var built = BuildPackage(new Spec());
        WithFile(built.Bytes, path =>
        {
            var source = Ps5PackageSource.Open(path);
            var node = source.LookUp("eboot.bin");
            source.Dispose();
            source.Dispose();
            Assert.Throws<ObjectDisposedException>(() => source.Read(node, 0, new byte[16]));
        });
    }

    [Fact]
    public void Ps4PackagesAndNonPackagesAreRefusedWithAClearMessage()
    {
        var cnt = new byte[PkgHeader.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cnt, PkgHeader.Magic);
        WithFile(cnt, path =>
        {
            Assert.False(Ps5PackageSource.IsPackagePath(path));
            var exception = Assert.Throws<InvalidDataException>(() => Ps5PackageSource.Open(path));
            Assert.Contains("install", exception.Message, StringComparison.OrdinalIgnoreCase);
        });

        var built = BuildPackage(new Spec());
        WithFile(built.Bytes, path => Assert.True(Ps5PackageSource.IsPackagePath(path)));
        WithFile(built.Bytes, path => Assert.False(Ps5PackageSource.IsPackagePath(path)), extension: ".bin");
        Assert.False(Ps5PackageSource.IsPackagePath(null));
        Assert.False(Ps5PackageSource.IsPackagePath(Path.Combine(Path.GetTempPath(), $"sharpemu-missing-{Guid.NewGuid():N}.pkg")));
    }

    [Fact]
    public void LibraryMetadataComesFromAPackageCacheFolderOfItsOwn()
    {
        var built = BuildPackage(new Spec());
        var cacheParent = Directory.CreateTempSubdirectory("sharpemu-pkg-cache-").FullName;
        try
        {
            WithFile(built.Bytes, path =>
            {
                Assert.True(ZArchiveCache.IsMountablePath(path));
                var root = ZArchiveCache.ExtractMetadata(path, cacheParent);
                Assert.Equal(ZArchiveCache.GetCacheRoot(path, cacheParent), root);
                Assert.EndsWith("-pkg", root);
                Assert.True(built.ExpectedFiles["sce_sys/param.json"].AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root!, "sce_sys", "param.json"))));
                Assert.True(built.ExpectedFiles["sce_sys/icon0.png"].AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root!, "sce_sys", "icon0.png"))));
                Assert.False(File.Exists(Path.Combine(root!, "eboot.bin")));

                // A second call is served from the folder.
                Assert.Equal(root, ZArchiveCache.ExtractMetadata(path, cacheParent));
            });

            Assert.NotEqual(
                ZArchiveCache.GetCacheRoot("C:\\games\\game.zar", cacheParent),
                ZArchiveCache.GetCacheRoot("C:\\games\\game.pkg", cacheParent));
        }
        finally
        {
            Directory.Delete(cacheParent, recursive: true);
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE.FileSystem.Pkg;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

// Synthetic data only: nothing here comes from a real package. The builder writes a tiny but
// complete FIH package: FIH header, a signed 32-bit outer PFS (plain seed, encrypted mode bit set)
// holding pfs_image.dat and naps_pkg_layout.dat, an inner flat PFS, and an embedded CNT.
public sealed class Ps5PkgTests
{
    private const string DefaultContentId = "UP9000-PPSA01234_00-TESTGAME00000000";

    private const string DefaultParamJson =
        """{"titleId":"PPSA01234","localizedParameters":{"defaultLanguage":"en-US","en-US":{"titleName":"Test: The Game"},"fr-FR":{"titleName":"Le Jeu"}}}""";

    // ---- package level -------------------------------------------------------------------------

    [Fact]
    public void FormatDetectionSeparatesFihCntAndJunk()
    {
        var fih = BuildPackage(new Spec());
        WithFile(fih.Bytes, path =>
        {
            Assert.True(GamePackage.IsPackagePath(path));
            using var package = GamePackage.Open(path);
            Assert.IsType<Ps5Package>(package);
        });

        // A bare "\x7FCNT" header with nothing behind it is a (degenerate) PS4 package.
        var cnt = new byte[PkgHeader.Size];
        BinaryPrimitives.WriteUInt32BigEndian(cnt, PkgHeader.Magic);
        WithFile(cnt, path =>
        {
            Assert.True(GamePackage.IsPackagePath(path));
            using var package = GamePackage.Open(path);
            Assert.IsType<PkgPackage>(package);
        });

        WithFile("PK\u0003\u0004 not a package, just junk bytes"u8.ToArray(), path =>
        {
            Assert.False(GamePackage.IsPackagePath(path));
            Assert.Throws<InvalidDataException>(() => GamePackage.Open(path));
        });

        WithFile([0x7F, 0x46], path => Assert.Throws<InvalidDataException>(() => GamePackage.Open(path)));
        WithFile(fih.Bytes, path => Assert.False(GamePackage.IsPackagePath(path)), extension: ".bin");
        Assert.False(GamePackage.IsPackagePath(Path.Combine(Path.GetTempPath(), $"sharpemu-missing-{Guid.NewGuid():N}.pkg")));
    }

    [Fact]
    public void OpenReadsContentIdTitleIdAndTitle()
    {
        WithPackage(BuildPackage(new Spec()).Bytes, (package, _) =>
        {
            Assert.Equal(DefaultContentId, package.ContentId);
            Assert.Equal("PPSA01234", package.TitleId);
            Assert.Equal("Test: The Game", package.Title);
            Assert.Null(package.Category);
            Assert.True(package.IsGameApplication);
            // ':' is refused by Windows, so the folder name drops it on every host.
            Assert.Equal("Test The Game", package.GetFolderName("fallback"));
        });
    }

    [Fact]
    public void TitleFollowsTheDefaultLanguage()
    {
        const string json = """{"titleId":"PPSA01234","localizedParameters":{"defaultLanguage":"fr-FR","en-US":{"titleName":"English"},"fr-FR":{"titleName":"Le Jeu"}}}""";
        WithPackage(BuildPackage(new Spec { ParamJson = json }).Bytes, (package, _) => Assert.Equal("Le Jeu", package.Title));
    }

    [Fact]
    public void FolderNameFallsBackToTheTitleIdThenToTheCaller()
    {
        WithPackage(BuildPackage(new Spec { ParamJson = null }).Bytes, (package, _) =>
        {
            Assert.Null(package.Title);
            Assert.Equal("PPSA01234", package.TitleId);
            Assert.Equal("PPSA01234", package.GetFolderName("fallback"));
        });

        WithPackage(BuildPackage(new Spec { ParamJson = null, ContentId = "SHORT" }).Bytes, (package, _) =>
        {
            Assert.Equal(string.Empty, package.TitleId);
            Assert.Equal("fallback", package.GetFolderName("fallback"));
        });

        WithPackage(BuildPackage(new Spec { ParamJson = "{ not json" }).Bytes, (package, _) => Assert.Null(package.Title));
    }

    [Fact]
    public void ExtractWritesTheGameTreeAndSceSys()
    {
        var built = BuildPackage(new Spec());
        WithPackage(built.Bytes, (package, destination) =>
        {
            var reports = new List<PkgExtractProgress>();
            package.Extract(destination, new CollectingProgress<PkgExtractProgress>(reports.Add));

            var written = ReadTree(destination);
            Assert.Equal(built.ExpectedFiles.Keys.OrderBy(k => k, StringComparer.Ordinal), written.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (name, bytes) in built.ExpectedFiles)
            {
                Assert.True(bytes.AsSpan().SequenceEqual(written[name]), name);
            }

            // Licences (encrypted) and container bookkeeping never reach disk.
            Assert.False(File.Exists(Path.Combine(destination, "sce_sys", "license.dat")));
            Assert.False(File.Exists(Path.Combine(destination, "sce_sys", "digests.bin")));
            Assert.False(File.Exists(Path.Combine(destination, "sce_sys", "metas.bin")));
            Assert.False(File.Exists(Path.Combine(destination, "sce_sys", "entry_names.bin")));
            Assert.False(File.Exists(Path.Combine(destination, "sce_sys", "unnamed.bin")));
            // The sparse region reads as zeros and a file outside uroot is ignored.
            Assert.All(written["assets/sparse.bin"], b => Assert.Equal(0, b));
            Assert.False(written.ContainsKey("readme"));

            Assert.NotEmpty(reports);
            Assert.Equal(reports[^1].BytesTotal, reports[^1].BytesDone);
            Assert.Equal(built.GameBytes, reports[^1].BytesTotal);
            Assert.All(reports, report => Assert.True(report.BytesDone <= report.BytesTotal));
        });
    }

    [Fact]
    public void GetInstalledSizeMatchesWhatExtractWrites()
    {
        WithPackage(BuildPackage(new Spec()).Bytes, (package, destination) =>
        {
            var size = package.GetInstalledSize();
            package.Extract(destination);
            Assert.Equal(size, ReadTree(destination).Values.Sum(bytes => (long)bytes.Length));
        });
    }

    [Fact]
    public void ExtractReportsProgressInsideAFile()
    {
        WithPackage(BuildPackage(new Spec()).Bytes, (package, destination) =>
        {
            var reports = new List<PkgExtractProgress>();
            package.Extract(destination, new CollectingProgress<PkgExtractProgress>(reports.Add), default, 1, null);
            Assert.True(reports.Select(r => r.BytesDone).Distinct().Count() > 5);
            Assert.Equal(reports[^1].BytesTotal, reports[^1].BytesDone);
        });
    }

    [Fact]
    public void RetailPackagesAreRefused()
    {
        var exception = OpenFails(BuildPackage(new Spec { RetailByte = 0x80 }).Bytes);
        Assert.Contains("retail", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEncryptedOuterPfsWithoutThePlainSeedIsRefused()
    {
        var exception = OpenFails(BuildPackage(new Spec { Seed = "SOMETHING-ELSE!!" }).Bytes);
        Assert.Contains("encrypted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("x:y")]
    [InlineData("a/b")]
    public void UnsafeInnerNamesAreRefusedBeforeAnythingIsWritten(string name)
    {
        WithPackage(BuildPackage(new Spec { ModuleFileName = name }).Bytes, (package, destination) =>
        {
            Assert.Throws<InvalidDataException>(() => package.GetInstalledSize());
            Assert.Throws<InvalidDataException>(() => package.Extract(destination));
            Assert.False(Directory.Exists(destination));
        });
    }

    [Fact]
    public void UnsafeCntNamesAreRefusedBeforeAnythingIsWritten()
    {
        WithPackage(BuildPackage(new Spec { ExtraCntName = "../evil.bin" }).Bytes, (package, destination) =>
        {
            Assert.Throws<InvalidDataException>(() => package.Extract(destination));
            Assert.False(Directory.Exists(destination));
        });
    }

    [Fact]
    public void ExtractRefusesWhenTheDriveIsTooSmallAndWritesNothing()
    {
        WithPackage(BuildPackage(new Spec()).Bytes, (package, destination) =>
        {
            var needed = package.GetInstalledSize();
            Assert.Throws<IOException>(() => package.Extract(destination, null, default, PkgPackage.ProgressStepBytes, needed - 1));
            Assert.False(Directory.Exists(destination));

            package.Extract(destination, null, default, PkgPackage.ProgressStepBytes, needed);
            Assert.True(File.Exists(Path.Combine(destination, "eboot.bin")));
        });
    }

    [Fact]
    public void ExtractHonoursCancellation()
    {
        WithPackage(BuildPackage(new Spec()).Bytes, (package, destination) =>
        {
            using var already = new CancellationTokenSource();
            already.Cancel();
            Assert.Throws<OperationCanceledException>(() => package.Extract(destination, null, already.Token));
        });

        WithPackage(BuildPackage(new Spec()).Bytes, (package, destination) =>
        {
            using var source = new CancellationTokenSource();
            var progress = new CollectingProgress<PkgExtractProgress>(report =>
            {
                if (report.BytesDone > 0)
                {
                    source.Cancel();
                }
            });
            Assert.Throws<OperationCanceledException>(() => package.Extract(destination, progress, source.Token, 1, null));
        });
    }

    [Theory]
    [InlineData(4096L, 4)]
    [InlineData(0x8000L, 4)]
    [InlineData(4096L, 1)]
    [InlineData(1L, 3)]
    public void ParallelExtractMatchesTheExpectedFiles(long chunkBytes, int parallelism)
    {
        var built = BuildPackage(new Spec());
        WithPackage(built.Bytes, (package, destination) =>
        {
            var reports = new List<PkgExtractProgress>();
            package.Extract(destination, new CollectingProgress<PkgExtractProgress>(reports.Add), default, 1, null, chunkBytes, parallelism);

            var written = ReadTree(destination);
            Assert.Equal(built.ExpectedFiles.Keys.OrderBy(k => k, StringComparer.Ordinal), written.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (name, bytes) in built.ExpectedFiles)
            {
                Assert.True(bytes.AsSpan().SequenceEqual(written[name]), name);
            }

            Assert.NotEmpty(reports);
            Assert.Equal((built.GameBytes, built.GameBytes), (reports[^1].BytesDone, reports[^1].BytesTotal));
            for (var i = 1; i < reports.Count; i++)
            {
                Assert.True(reports[i].BytesDone >= reports[i - 1].BytesDone, $"report {i} went backwards");
            }
        });
    }

    [Fact]
    public void ParallelExtractHonoursCancellationWithoutAnAggregateException()
    {
        WithPackage(BuildPackage(new Spec()).Bytes, (package, destination) =>
        {
            using var source = new CancellationTokenSource();
            var progress = new CollectingProgress<PkgExtractProgress>(report =>
            {
                if (report.BytesDone > 0)
                {
                    source.Cancel();
                }
            });
            Assert.Throws<OperationCanceledException>(() => package.Extract(destination, progress, source.Token, 1, null, 4096, 4));
        });
    }

    [Fact]
    public void ParallelExtractRethrowsACorruptBlockAsInvalidData()
    {
        var built = BuildPackage(new Spec());
        // The Kraken block starts with the seed bytes of the stored image at logical 0x8000: find
        // them in the package and break the literal count that follows the 8 seed bytes.
        var seed = built.ExpectedFiles["assets/data.bin"].AsSpan(0x100, 8).ToArray();
        // The seed alone also occurs in the periodic test patterns, so match the literal count too.
        byte[] needle = [.. seed, 0x00, 0x7F, 0xF8];
        var at = built.Bytes.AsSpan().IndexOf(needle);
        Assert.True(at >= 0);
        var corrupt = (byte[])built.Bytes.Clone();
        corrupt[at + 8] = 0;
        corrupt[at + 9] = 0x10;
        corrupt[at + 10] = 0;

        WithPackage(corrupt, (package, destination) =>
            Assert.Throws<InvalidDataException>(() => package.Extract(destination, null, default, PkgPackage.ProgressStepBytes, null, 4096, 4)));
    }

    // ---- NAPS ----------------------------------------------------------------------------------

    [Fact]
    public void NapsHeaderFieldsUnpack()
    {
        var w0 = 63UL | (2UL << 24) | (40808UL << 32);
        var w1 = 68506UL | (43579UL << 24);
        var header = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(header, w0);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), w1);
        var counts = Ps5Naps.DecodeHeader(header);
        Assert.Equal((64u, (byte)2, 40808u), (counts.NumFiles, counts.CompressionType, counts.NumUblocks));
        Assert.Equal((68506u, 43581u), (counts.NumOuterBlocks, counts.NumCblockInfo));

        // The header of a large real-world layout: 550661 blocks of 256 KiB.
        var large = Ps5Naps.DecodeHeader([0xAE, 0, 0, 2, 5, 0x67, 8, 0, 0x7A, 0xAB, 0x15, 0xD6, 0xF6, 8, 0, 0]);
        Assert.Equal(0x219C140000L, large.NumUblocks * Ps5Naps.UblockSize);
        Assert.Equal(0x15AB7Au, large.NumOuterBlocks);
        Assert.Equal(0xAFu, large.NumFiles);
        Assert.Throws<InvalidDataException>(() => Ps5Naps.DecodeHeader(new byte[15]));
    }

    [Fact]
    public void CblockInfoKeepsTheHighBitOfTheFirstChunkLength()
    {
        var record = Ps5Naps.DecodeCblock([0x09, 0x72, 0xA1, 0x48, 0x0E, 0xE2, 0xC9, 0x12, 0x00]);
        Assert.False(record.IsRunBase);
        Assert.Equal(75657u, record.EvenComp);
        Assert.Equal(0x22u, record.KrakenFlags);
        Assert.True(record.IsKraken);

        var stored = Ps5Naps.DecodeCblock([0x0A, 0x00, 0xA2, 0x24, 0xC3, 0xFF, 0xFF, 0x04, 0x00]);
        Assert.Equal(0x20000u, stored.EvenComp);
    }

    [Fact]
    public void CblockInfoTableStartsAfterTheEightByteU2cPadding()
    {
        var blob = new byte[96];
        BinaryPrimitives.WriteUInt64LittleEndian(blob, 1UL | (9UL << 32));
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(8), 1UL);
        blob[32] = 0x24; // mount size 0x240000
        blob[35] = 0x40;
        blob[74] = 4; // run base marker at the start of CblockInfo (72)
        blob[81] = 10;

        var layout = Ps5Naps.Parse(blob[..90]);
        Assert.Equal(0x240000, layout.MountSize());
        var first = layout.Candidates[0];
        Assert.Equal(72, first.Start);
        Assert.True(first.Cblocks[0].IsRunBase);
        Assert.False(first.Cblocks[1].IsRunBase);
        Assert.Equal(10, first.Cblocks[0].RunOnDisk(first.Cblocks[1]));

        // Cut off at 89 bytes only the placement right after the file offsets (56) still fits.
        Assert.Equal(new[] { 56 }, Ps5Naps.Parse(blob[..89]).Candidates.Select(c => c.Start).ToArray());
    }

    [Fact]
    public void U2cMayStartRightAfterTheFileOffsets()
    {
        var blob = new byte[74];
        BinaryPrimitives.WriteUInt64LittleEndian(blob, 1UL | (9UL << 32));
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(8), 1UL);
        blob[32] = 0x24;
        blob[35] = 0x40;
        blob[58] = 4; // run base
        blob[65] = 10; // data record, offset 10

        var layout = Ps5Naps.Parse(blob);
        var only = Assert.Single(layout.Candidates);
        Assert.Equal(56, only.Start);
        Assert.Equal(10, only.Cblocks[0].RunOnDisk(only.Cblocks[1]));
    }

    [Fact]
    public void RunBasesOutsideTheImageRejectAPlacement()
    {
        var blob = new byte[74];
        BinaryPrimitives.WriteUInt64LittleEndian(blob, 1UL | (9UL << 32));
        BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(8), 1UL);
        blob[35] = 0x40;
        blob[58] = 4;
        blob[63] = 0x04; // run start 512: 256 KiB block 256, past the 0x10000-byte image
        Assert.Throws<InvalidDataException>(() => Ps5Naps.Parse(blob));
        Assert.Throws<InvalidDataException>(() => Ps5Naps.Parse(new byte[8]));
    }

    // ---- outer PFS -----------------------------------------------------------------------------

    [Fact]
    public void OuterPfsFollowsDirectSingleAndDoubleIndirectBlocks()
    {
        // 4 KiB blocks hold 113 signed pointers: 12 direct + 113 single + 200 through double indirect.
        var data = Pattern(9, (12 + 113 + 200) * 0x1000 - 123);
        var small = Pattern(10, 77);
        var pfs = BuildOuterPfs(0x1000, "PPRPLAIN-NOAUTH!", [("big.bin", data), ("small.bin", small)], scramble: true);
        WithFile(pfs, path =>
        {
            using var source = new Ps5Source(path);
            var outer = Ps5OuterPfs.Open(source, 0, pfs.Length, 0);
            var big = outer.OpenFile(outer.Find("big.bin") ?? throw new InvalidOperationException());
            Assert.Equal(data.Length, big.Size);
            Assert.True(data.AsSpan().SequenceEqual(big.ReadAll()));

            var window = new byte[5000];
            big.ReadExactAt(window, 12 * 0x1000 - 2500);
            Assert.True(data.AsSpan(12 * 0x1000 - 2500, window.Length).SequenceEqual(window));
            Assert.True(small.AsSpan().SequenceEqual(outer.OpenFile(outer.Find("small.bin")!.Value).ReadAll()));
            Assert.Null(outer.Find("missing.bin"));
            Assert.Throws<InvalidDataException>(() => big.ReadExactAt(new byte[2], big.Size - 1));
        }, extension: ".dat");
    }

    [Fact]
    public void SignedInodePointersFollowTheirHashes()
    {
        var layout = new Ps5OuterPfs.Ps5PfsSuperblock(0x0D, 0x10000, 1, 1, 0, []).GetInodeLayout();
        Assert.Equal(0x2C8, layout.Size);
        var record = new byte[layout.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x64 + 32), 0x15AC86);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x64 + 36 + 32), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x64 + 12 * 36 + 32), 0x15AC88);
        var inode = Ps5OuterPfs.ParseInode(record, layout);
        Assert.Equal(0x15AC86, inode.Direct[0]);
        Assert.Equal(7, inode.Direct[1]);
        Assert.Equal(0x15AC88, inode.Indirect[0]);
        Assert.Equal(0x60, inode.Size);
    }

    [Fact]
    public void DirectoryRecordsAreValidated()
    {
        var good = Dirent(2, 3, ".", 24);
        Array.Resize(ref good, 64);
        good = [.. good, .. Dirent(3, 2, "pfs_image.dat", 32)];
        Array.Resize(ref good, 128);
        var names = Ps5Dirent.Parse(good, 64).Select(e => Encoding.ASCII.GetString(e.Name)).ToArray();
        Assert.Equal([".", "pfs_image.dat"], names);

        var tooLong = Dirent(3, 2, "x", 24);
        BinaryPrimitives.WriteUInt32LittleEndian(tooLong.AsSpan(8), 30);
        Assert.Throws<InvalidDataException>(() => Ps5Dirent.Parse(tooLong, 64));
    }

    // ---- Kraken --------------------------------------------------------------------------------

    [Fact]
    public void KrakenCopiesAVerbatimBlock()
    {
        byte[] src = [1, 2, 3, 4, 5, 6, 7, 8];
        var dst = new byte[8];
        Kraken.DecodeBlock(src, 0x02, 8, dst);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void KrakenMemcpyEntropyArrayExpands()
    {
        var (used, output) = Kraken.DecodeBytes([0x80, 0x04, 9, 8, 7, 6], 4);
        Assert.Equal(6, used);
        Assert.Equal<byte[]>([9, 8, 7, 6], output);
    }

    [Fact]
    public void KrakenDecodesASingleSymbolHuffmanArray()
    {
        // Entropy type 2, 3-byte header: 3 payload bytes expand to 10. The payload is "00", a
        // count of 1 symbol, then the symbol 0xAA.
        var (used, output) = Kraken.DecodeBytes([0xA0, 0x18, 0x03, 0x00, 0x6A, 0x80], 10);
        Assert.Equal(6, used);
        Assert.Equal(Enumerable.Repeat((byte)0xAA, 10), output);
    }

    [Fact]
    public void KrakenDecodesAnLzChunkWhoseLiteralsUseMemcpyEntropy()
    {
        var block = Pattern(5, 200);
        var payload = KrakenLiteralChunk(block);
        var output = new byte[block.Length];
        Kraken.DecodeBlock(payload, 0x02, 0, output);
        Assert.Equal(block, output);

        // The same chunk as the second half of a two-chunk block, restarting with a fresh seed.
        var two = new byte[0x20000 + block.Length];
        var first = KrakenLiteralChunk(Pattern(6, 0x20000));
        var combined = new byte[first.Length + payload.Length];
        first.CopyTo(combined, 0);
        payload.CopyTo(combined, first.Length);
        Kraken.DecodeBlock(combined, 0x02 | 0x20 | 0x40, (uint)first.Length, two);
        Assert.True(Pattern(6, 0x20000).AsSpan().SequenceEqual(two.AsSpan(0, 0x20000)));
        Assert.True(block.AsSpan().SequenceEqual(two.AsSpan(0x20000)));
    }

    [Fact]
    public void KrakenReportsUnsupportedEntropyTypesWithTheirNumber()
    {
        // Type 1 (tANS), 3-byte header: 2 compressed bytes for 4 output bytes.
        var exception = Assert.Throws<InvalidDataException>(() => Kraken.DecodeBytes([0x90, 0x04, 0x02, 0, 0], 16));
        Assert.Contains("entropy type 1", exception.Message);
    }

    [Fact]
    public void KrakenRejectsMalformedData()
    {
        var dst = new byte[64];
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBlock([], 0x02, 0, dst));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBlock([1, 2, 3], 0x02, 0, dst));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBlock(new byte[100], 0x02, 0, dst));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBlock([1], 0, 0, []));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBytes([0x80], 8));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBytes([0x80, 0x09, 1, 2], 8));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBytes([0x80, 0x04, 1, 2, 3, 4], 2));

        // A valid chunk cut short or with a corrupted command stream must not decode.
        var good = KrakenLiteralChunk(Pattern(7, 64));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBlock(good.AsSpan(0, good.Length - 1), 0x02, 0, new byte[64]));
        Assert.Throws<InvalidDataException>(() => Kraken.DecodeBlock(good, 0x02, 0, new byte[65]));
    }

    [Fact]
    public void KrakenNeverThrowsAnythingButInvalidDataOnCorruptInput()
    {
        var random = new System.Random(12345);
        var seeds = new List<byte[]>
        {
            KrakenLiteralChunk(Pattern(1, 64)),
            KrakenLiteralChunk(Pattern(2, 1000)),
            new byte[] { 0xA0, 0x18, 0x03, 0x00, 0x6A, 0x80 },
            new byte[] { 0x90, 0x04, 0x02, 0, 0 },
        };
        var lengths = new[] { 64, 1000, 4096, 10, 0x20040 };
        for (var iteration = 0; iteration < 4000; iteration++)
        {
            var payload = (byte[])seeds[random.Next(seeds.Count)].Clone();
            if (random.Next(4) == 0)
            {
                payload = new byte[random.Next(1, 300)];
                random.NextBytes(payload);
            }

            for (var edits = random.Next(0, 4); edits > 0 && payload.Length > 0; edits--)
            {
                payload[random.Next(payload.Length)] = (byte)random.Next(256);
            }

            if (random.Next(5) == 0 && payload.Length > 1)
            {
                payload = payload[..random.Next(1, payload.Length)];
            }

            var dst = new byte[lengths[random.Next(lengths.Length)]];
            try
            {
                Kraken.DecodeBlock(payload, (uint)random.Next(0x80), (uint)random.Next(0, 400), dst);
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record Spec
    {
        public byte RetailByte { get; init; }

        public string Seed { get; init; } = "PPRPLAIN-NOAUTH!";

        public string ContentId { get; init; } = DefaultContentId;

        public string? ParamJson { get; init; } = DefaultParamJson;

        public string ModuleFileName { get; init; } = "libfoo.prx";

        public string? ExtraCntName { get; init; }
    }

    private sealed record Built(byte[] Bytes, Dictionary<string, byte[]> ExpectedFiles, long GameBytes);

    private static byte[] Pattern(int seed, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 31 + seed * 7 + (i >> 8));
        }

        return bytes;
    }

    private static byte[] Dirent(uint inode, uint kind, string name, int recordSize)
    {
        var record = new byte[recordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record, inode);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), kind);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), (uint)Encoding.UTF8.GetByteCount(name));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), (uint)recordSize);
        Encoding.UTF8.GetBytes(name).CopyTo(record, 16);
        return record;
    }

    private static byte[] DirectoryBlock(int blockSize, params (uint Inode, uint Kind, string Name)[] entries)
    {
        var block = new byte[blockSize];
        var at = 0;
        foreach (var (inode, kind, name) in entries)
        {
            var size = (16 + Encoding.UTF8.GetByteCount(name) + 1 + 7) & ~7;
            Dirent(inode, kind, name, size).CopyTo(block, at);
            at += size;
        }

        return block;
    }

    // One LZ chunk (with seed) holding `block` as 8 seed bytes plus literals stored with the memcpy
    // entropy type and no commands. Every array uses the 3-byte memcpy header: a first byte with the
    // high bit set would read as the excess framing.
    private static byte[] KrakenLiteralChunk(byte[] block)
    {
        var literals = block[8..];
        var payload = new List<byte>();
        payload.AddRange(block[..8]);
        payload.AddRange([(byte)(literals.Length >> 16), (byte)(literals.Length >> 8), (byte)literals.Length]);
        payload.AddRange(literals);
        payload.AddRange([0, 0, 0]); // commands
        payload.AddRange([0, 0, 0]); // packed offsets
        payload.AddRange([0, 0, 0]); // packed lengths
        payload.Add(0x80); // the bit stream holding the zero escape-length count
        return [.. payload];
    }

    private static byte[] CblockRecord(uint coffsetMod, ulong kdePredictor)
    {
        var record = new byte[9];
        BinaryPrimitives.WriteUInt64LittleEndian(record, coffsetMod | (kdePredictor << 56));
        return record;
    }

    // Signed 32-bit PFS with the encrypted mode bit set. Block 0 is the superblock, then the inode
    // table, the root directory, then data (and indirect) blocks, optionally in scrambled order.
    private static byte[] BuildOuterPfs(int blockSize, string seed, IReadOnlyList<(string Name, byte[] Data)> files, bool scramble)
    {
        const int InodeSize = 0x2C8;
        const int Stride = 36;
        var inodeCount = files.Count + 1;
        var perInodeBlock = blockSize / InodeSize;
        var tableBlocks = (inodeCount + perInodeBlock - 1) / perInodeBlock;
        var pointersPerBlock = blockSize / Stride;
        var dirBlock = 1 + tableBlocks;
        var firstData = dirBlock + 1;

        var dataCounts = files.Select(file => (file.Data.Length + blockSize - 1) / blockSize).ToArray();
        var dataPool = Enumerable.Range(firstData, dataCounts.Sum()).ToList();
        if (scramble)
        {
            var random = new System.Random(77);
            for (var i = dataPool.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (dataPool[i], dataPool[j]) = (dataPool[j], dataPool[i]);
            }
        }

        var next = firstData + dataPool.Count;
        var extraBlocks = new List<(int Block, int[] Pointers)>();
        var inodePointers = new List<(int[] Direct, int[] Indirect)>();
        var poolAt = 0;
        foreach (var (file, count) in files.Zip(dataCounts))
        {
            var blocks = dataPool.GetRange(poolAt, count);
            poolAt += count;
            var direct = new int[12];
            var indirect = new int[5];
            for (var i = 0; i < Math.Min(12, count); i++)
            {
                direct[i] = blocks[i];
            }

            var rest = blocks.Skip(12).ToList();
            if (rest.Count > 0)
            {
                var single = rest.Take(pointersPerBlock).ToArray();
                indirect[0] = next++;
                extraBlocks.Add((indirect[0], single));
                rest = rest.Skip(pointersPerBlock).ToList();
            }

            if (rest.Count > 0)
            {
                Assert.True(rest.Count <= pointersPerBlock * pointersPerBlock);
                var leaves = new List<int>();
                for (var at = 0; at < rest.Count; at += pointersPerBlock)
                {
                    var leaf = next++;
                    extraBlocks.Add((leaf, rest.Skip(at).Take(pointersPerBlock).ToArray()));
                    leaves.Add(leaf);
                }

                indirect[1] = next++;
                extraBlocks.Add((indirect[1], [.. leaves]));
            }

            inodePointers.Add((direct, indirect));
        }

        var image = new byte[next * blockSize];
        // Superblock.
        BinaryPrimitives.WriteUInt64LittleEndian(image, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(8), Ps5OuterPfs.PfsMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x1C), 0x5); // signed | encrypted
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), (uint)blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x30), (ulong)inodeCount);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x40), (ulong)tableBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0xD8), 1);
        Encoding.ASCII.GetBytes(seed).CopyTo(image, 0x370);

        void WriteInode(int index, long size, int[] direct, int[] indirect)
        {
            var at = blockSize + index / perInodeBlock * blockSize + index % perInodeBlock * InodeSize;
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(at + 8), (ulong)size);
            var pointers = direct.Concat(indirect).ToArray();
            for (var i = 0; i < pointers.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 0x64 + i * Stride + 32), (uint)pointers[i]);
            }
        }

        WriteInode(0, blockSize, [dirBlock, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], new int[5]);
        var entries = new List<(uint, uint, string)> { (0, 3, ".") };
        for (var i = 0; i < files.Count; i++)
        {
            WriteInode(i + 1, files[i].Data.Length, inodePointers[i].Direct, inodePointers[i].Indirect);
            entries.Add(((uint)(i + 1), 2, files[i].Name));
            var blocks = dataPool.GetRange(dataCounts.Take(i).Sum(), dataCounts[i]);
            var logicalBlocks = ResolveBlocks(inodePointers[i], extraBlocks, dataCounts[i], pointersPerBlock);
            Assert.Equal(blocks, logicalBlocks);
            for (var b = 0; b < logicalBlocks.Count; b++)
            {
                var length = Math.Min(blockSize, files[i].Data.Length - b * blockSize);
                files[i].Data.AsSpan(b * blockSize, length).CopyTo(image.AsSpan(logicalBlocks[b] * blockSize));
            }
        }

        DirectoryBlock(blockSize, [.. entries]).CopyTo(image, dirBlock * blockSize);
        foreach (var (block, pointers) in extraBlocks)
        {
            for (var i = 0; i < pointers.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(block * blockSize + i * Stride + 32), (uint)pointers[i]);
            }
        }

        return image;
    }

    // Reads the pointer structure back the way a reader would, so the builder checks itself.
    private static List<int> ResolveBlocks((int[] Direct, int[] Indirect) inode, List<(int Block, int[] Pointers)> extra, int count, int perBlock)
    {
        var map = extra.ToDictionary(e => e.Block, e => e.Pointers);
        var blocks = inode.Direct.Take(Math.Min(12, count)).ToList();
        if (blocks.Count < count)
        {
            blocks.AddRange(map[inode.Indirect[0]]);
        }

        if (blocks.Count < count)
        {
            foreach (var leaf in map[inode.Indirect[1]])
            {
                blocks.AddRange(map[leaf]);
            }
        }

        return blocks.Take(count).ToList();
    }

    private static Built BuildPackage(Spec spec)
    {
        const int Mount = 0x30000;
        var logical = new byte[Mount];
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        void Place(string path, int offset, byte[] data)
        {
            data.CopyTo(logical, offset);
            expected[path] = data;
        }

        Place("eboot.bin", 0, Pattern(1, 0x2345));
        Place("sce_module/" + spec.ModuleFileName, 0x3000, Pattern(2, 0x123));
        // Crosses the boundary between the raw block and the Kraken block.
        Place("assets/data.bin", 0x7F00, Pattern(3, 0x300));
        Place("assets/sub/deep.bin", 0x9000, Pattern(4, 0x10));
        expected["assets/sparse.bin"] = new byte[0x2000];
        expected["assets/empty.bin"] = [];

        // Inner metadata: superblock, one inode block, five directory blocks (0x1000 each).
        var tail = new byte[0x10000];
        BinaryPrimitives.WriteUInt64LittleEndian(tail, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(8), Ps5OuterPfs.PfsMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(0x1C), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(0x20), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(0x30), 11);
        BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(0x38), Mount / 0x1000);

        const long TailBase = 0x20000;
        void Inode(int index, ushort mode, long size, long where)
        {
            var at = 0x1000 + index * 0xA8;
            BinaryPrimitives.WriteUInt16LittleEndian(tail.AsSpan(at), mode);
            BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(at + 8), (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(tail.AsSpan(at + 0x60), (ulong)where);
        }

        const ushort DirMode = 0x41ED;
        const ushort FileMode = 0x81A4;
        Inode(0, DirMode, 0x1000, TailBase + 0x2000);
        Inode(1, DirMode, 0x1000, TailBase + 0x3000);
        Inode(2, FileMode, 0x2345, 0);
        Inode(3, DirMode, 0x1000, TailBase + 0x4000);
        Inode(4, FileMode, 0x123, 0x3000);
        Inode(5, DirMode, 0x1000, TailBase + 0x5000);
        Inode(6, FileMode, 0x300, 0x7F00);
        Inode(7, DirMode, 0x1000, TailBase + 0x6000);
        Inode(8, FileMode, 0x10, 0x9000);
        Inode(9, FileMode, 0x2000, 0x10000);
        Inode(10, FileMode, 0, 0);
        DirectoryBlock(0x1000, (0, 3, "."), (0, 3, ".."), (1, 3, "uroot"), (2, 2, "readme")).CopyTo(tail, 0x2000);
        // Real packages type "." and ".." as PFS self (4) and parent (5) records.
        DirectoryBlock(0x1000, (1, 4, "."), (0, 5, ".."), (2, 2, "eboot.bin"), (3, 3, "sce_module"), (5, 3, "assets")).CopyTo(tail, 0x3000);
        DirectoryBlock(0x1000, (3, 3, "."), (1, 3, ".."), (4, 2, spec.ModuleFileName)).CopyTo(tail, 0x4000);
        DirectoryBlock(0x1000, (5, 3, "."), (1, 3, ".."), (6, 2, "data.bin"), (7, 3, "sub"), (9, 2, "sparse.bin"), (10, 2, "empty.bin"))
            .CopyTo(tail, 0x5000);
        DirectoryBlock(0x1000, (7, 3, "."), (5, 3, ".."), (8, 2, "deep.bin")).CopyTo(tail, 0x6000);
        tail.CopyTo(logical, (int)TailBase);

        // Stored image: raw block [0, 0x8000), a Kraken block for [0x8000, 0x10000), a sparse hole
        // for [0x10000, 0x20000) and the raw metadata tail.
        var kraken = KrakenLiteralChunk(logical[0x8000..0x10000]);
        var stored = new List<byte>();
        stored.AddRange(logical[..0x8000]);
        stored.AddRange(kraken);
        stored.AddRange(logical[0x20000..]);
        var image = stored.ToArray();

        // NAPS: header, one outer digest, five file-offset entries, u2c, four CblockInfo records.
        var naps = new byte[100];
        BinaryPrimitives.WriteUInt64LittleEndian(naps, 4UL | (2UL << 24) | (3UL << 32));
        BinaryPrimitives.WriteUInt64LittleEndian(naps.AsSpan(8), 1UL | (2UL << 24));
        var boundaries = new (int Offset, byte Kind)[] { (0, 0), (0x8000, 0), (0x10000, 0x40), (0x20000, 0), (Mount, 0x40) };
        for (var i = 0; i < boundaries.Length; i++)
        {
            var at = 24 + i * 6;
            for (var b = 0; b < 4; b++)
            {
                naps[at + b] = (byte)(boundaries[i].Offset >> (8 * b));
            }

            naps[at + 5] = boundaries[i].Kind;
        }

        var krakenEnd = 0x8000 + kraken.Length;
        CblockRecord(0, 0).CopyTo(naps, 64);
        CblockRecord(0x8000, 2).CopyTo(naps, 73);
        CblockRecord((uint)(krakenEnd & 0x3FFFF), 0).CopyTo(naps, 82);
        CblockRecord((uint)((krakenEnd + 0x10000) & 0x3FFFF), 0).CopyTo(naps, 91);

        var pfs = BuildOuterPfs(0x10000, spec.Seed, [("pfs_image.dat", image), ("naps_pkg_layout.dat", naps)], scramble: true);

        // CNT: entries, then the names table, then the data they point at.
        var cntFiles = new List<(uint Id, uint NameAt, uint Flags, byte[] Data)>();
        var names = new MemoryStream();
        names.WriteByte(0);
        uint AddName(string name)
        {
            var at = (uint)names.Length;
            names.Write(Encoding.UTF8.GetBytes(name));
            names.WriteByte(0);
            return at;
        }

        cntFiles.Add((0x0001, 0, 0, [1, 2, 3]));
        cntFiles.Add((0x0100, 0, 0, [4, 5, 6]));
        cntFiles.Add((0x0400, 0, 0x80000000, [7, 7, 7, 7]));
        cntFiles.Add((0x1200, 0, 0, Pattern(20, 300)));
        cntFiles.Add((0x1220, 0, 0, []));
        cntFiles.Add((0x7777, 0, 0, [9, 9]));
        var sceSys = new Dictionary<string, byte[]> { ["icon0.png"] = cntFiles[3].Data };
        if (spec.ParamJson is { } json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            cntFiles.Add((0x2000, AddName("/sce_sys/param.json"), 0, bytes));
            sceSys["param.json"] = bytes;
        }

        var playgo = Pattern(21, 40);
        cntFiles.Add((0x1001, AddName("playgo-chunk.dat"), 0, playgo));
        sceSys["playgo-chunk.dat"] = playgo;
        if (spec.ExtraCntName is { } extra)
        {
            cntFiles.Add((0x2011, AddName(extra), 0, [1, 2, 3]));
        }

        cntFiles.Add((0x0200, 0, 0, names.ToArray()));
        foreach (var (name, bytes) in sceSys)
        {
            expected["sce_sys/" + name] = bytes;
        }

        const int TableAt = 0x100;
        var dataAt = TableAt + cntFiles.Count * 0x20;
        var cnt = new byte[dataAt + cntFiles.Sum(file => file.Data.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(cnt, PkgHeader.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(0x10), (uint)cntFiles.Count);
        BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(0x18), TableAt);
        BinaryPrimitives.WriteUInt64BigEndian(cnt.AsSpan(0x28), (ulong)cnt.Length);
        Encoding.ASCII.GetBytes(spec.ContentId).CopyTo(cnt, 0x40);
        for (var i = 0; i < cntFiles.Count; i++)
        {
            var (id, nameAt, flags, data) = cntFiles[i];
            var row = TableAt + i * 0x20;
            BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(row), id);
            BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(row + 4), nameAt);
            BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(row + 8), flags);
            BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(row + 16), (uint)dataAt);
            BinaryPrimitives.WriteUInt32BigEndian(cnt.AsSpan(row + 20), (uint)data.Length);
            data.CopyTo(cnt, dataAt);
            dataAt += data.Length;
        }

        const int PfsAt = 0x10000;
        var package = new byte[PfsAt + pfs.Length + cnt.Length];
        package[0] = 0x7F;
        package[1] = 0x46;
        package[2] = 0x49;
        package[3] = 0x48;
        package[5] = spec.RetailByte;
        BinaryPrimitives.WriteUInt16LittleEndian(package.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(0x10), PfsAt);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(0x18), (ulong)pfs.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(0x20), PfsAt);
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(0x58), (ulong)(PfsAt + pfs.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(package.AsSpan(0xA0), (ulong)cnt.Length);
        pfs.CopyTo(package, PfsAt);
        cnt.CopyTo(package, PfsAt + pfs.Length);

        var gameBytes = expected.Where(pair => !pair.Key.StartsWith("sce_sys/", StringComparison.Ordinal)).Sum(pair => (long)pair.Value.Length);
        return new Built(package, expected, gameBytes);
    }

    private static Dictionary<string, byte[]> ReadTree(string root)
    {
        var tree = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            tree[Path.GetRelativePath(root, path).Replace('\\', '/')] = File.ReadAllBytes(path);
        }

        return tree;
    }

    private static InvalidDataException OpenFails(byte[] bytes)
    {
        InvalidDataException? caught = null;
        WithFile(bytes, path => caught = Assert.Throws<InvalidDataException>(() => Ps5Package.Open(path).Dispose()));
        return caught!;
    }

    private static void WithFile(byte[] bytes, Action<string> action, string extension = ".pkg", Action<string>? afterWrite = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-ps5pkg-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(path, bytes);
            afterWrite?.Invoke(path);
            action(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WithPackage(byte[] bytes, Action<Ps5Package, string> action)
    {
        var destination = Path.Combine(Path.GetTempPath(), $"sharpemu-ps5pkg-out-{Guid.NewGuid():N}");
        try
        {
            WithFile(bytes, path =>
            {
                using var package = Ps5Package.Open(path);
                action(package, destination);
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

    private sealed class CollectingProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

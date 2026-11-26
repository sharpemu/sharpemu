// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SharpEmu.HLE.FileSystem.Pkg;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

// Synthetic data only: nothing here comes from a real package.
public sealed class PkgTests
{
    [Fact]
    public void HeaderFieldsAreReadBigEndian()
    {
        var data = new byte[PkgHeader.Size];
        BinaryPrimitives.WriteUInt32BigEndian(data, PkgHeader.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), 7);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10), 12);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x18), 0x2000);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x20), 0x2000);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x30), 0x80000);
        Encoding.ASCII.GetBytes("UP9000-CUSA00900_00-BLOODBORNE000000").CopyTo(data, 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x78), 0x0A000000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x404), 1);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x408), 0x8000000000000004);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x410), 0x80000);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x418), 0x1234_5678_9ABC);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0x430), 0x2_0000_0000);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x43C), 0x4000);

        var header = PkgHeader.Parse(data);

        Assert.Equal(7u, header.FileCount);
        Assert.Equal(12u, header.TableEntryCount);
        Assert.Equal(0x2000u, header.TableEntryOffset);
        Assert.Equal(0x80000ul, header.ContentOffset);
        Assert.Equal("UP9000-CUSA00900_00-BLOODBORNE000000", header.ContentId);
        Assert.Equal("CUSA00900", header.TitleId);
        Assert.Equal(0x0A000000u, header.ContentFlags);
        Assert.Equal(0x8000000000000004ul, header.PfsImageFlags);
        Assert.Equal(0x80000ul, header.PfsImageOffset);
        Assert.Equal(0x1234_5678_9ABCul, header.PfsImageSize);
        Assert.Equal(0x2_0000_0000ul, header.PkgSize);
        Assert.Equal(0x4000u, header.PfsCacheSize);
    }

    [Fact]
    public void HeaderRejectsBadMagicAndTruncation()
    {
        Assert.Throws<InvalidDataException>(() => PkgHeader.Parse(new byte[PkgHeader.Size]));
        var good = new byte[PkgHeader.Size];
        BinaryPrimitives.WriteUInt32BigEndian(good, PkgHeader.Magic);
        Assert.Throws<InvalidDataException>(() => PkgHeader.Parse(good.AsSpan(0, 0x100)));
        Assert.True(PkgHeader.HasMagic([0x7F, 0x43, 0x4E, 0x54]));
        Assert.False(PkgHeader.HasMagic([0x50, 0x4B, 0x03, 0x04]));
    }

    [Fact]
    public void EntryTableRowsAreParsedAndNamed()
    {
        var table = new byte[64];
        WriteEntry(table.AsSpan(0, 32), id: 0x1000, nameOffset: 5, flags1: 0, flags2: 0, offset: 0x3000, size: 0x1C0);
        WriteEntry(table.AsSpan(32, 32), id: 0x1200, nameOffset: 9, flags1: 0x80000000, flags2: 0x3000, offset: 0x3200, size: 0x4000);

        var entries = PkgEntry.ParseTable(table, 2);

        Assert.Equal(0x1000u, entries[0].Id);
        Assert.Equal("param.sfo", entries[0].Name);
        Assert.Equal(0x3000u, entries[0].Offset);
        Assert.Equal(0x1C0u, entries[0].Size);
        Assert.Equal("icon0.png", entries[1].Name);
        Assert.Equal(0x80000000u, entries[1].Flags1);
        Assert.Equal(0x3000u, entries[1].Flags2);
        Assert.Throws<InvalidDataException>(() => PkgEntry.ParseTable(table, 3));
    }

    [Theory]
    [InlineData(0x402u, "nptitle.dat")]
    [InlineData(0x403u, "npbind.dat")]
    public void TrophyBindingEntriesAreExtractedIntoSceSys(uint id, string expected) =>
        Assert.Equal(expected, PkgEntryNames.Get(id));

    [Fact]
    public void ParamSfoReaderFindsStringKeys()
    {
        var sfo = BuildSfo(("TITLE_ID", "CUSA99999"), ("CATEGORY", "gp"));
        Assert.Equal("gp", PkgParamSfo.ReadString(sfo, "CATEGORY"));
        Assert.Equal("CUSA99999", PkgParamSfo.ReadString(sfo, "TITLE_ID"));
        Assert.Null(PkgParamSfo.ReadString(sfo, "APP_VER"));
    }

    [Fact]
    public void ParamSfoReaderReturnsNullForMalformedData()
    {
        var sfo = BuildSfo(("CATEGORY", "gd"));
        Assert.Null(PkgParamSfo.ReadString([], "CATEGORY"));
        Assert.Null(PkgParamSfo.ReadString(new byte[64], "CATEGORY"));
        for (var length = 0; length < sfo.Length; length++)
        {
            // Truncation at any point must never throw; short of the value it must yield null.
            var truncated = sfo.AsSpan(0, length).ToArray();
            var value = PkgParamSfo.ReadString(truncated, "CATEGORY");
            Assert.True(value is null || value == "gd");
        }

        Assert.Null(PkgParamSfo.ReadString(sfo.AsSpan(0, sfo.Length - 1), "CATEGORY"));

        var hugeCount = (byte[])sfo.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hugeCount.AsSpan(0x10), uint.MaxValue);
        Assert.Null(PkgParamSfo.ReadString(hugeCount, "CATEGORY"));

        var badOffset = (byte[])sfo.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(0x14 + 12), 0x7FFFFFFF);
        Assert.Null(PkgParamSfo.ReadString(badOffset, "CATEGORY"));
    }

    [Theory]
    [InlineData("gd", true)]
    [InlineData("gde", true)]
    [InlineData("gp", false)]
    [InlineData("ac", false)]
    [InlineData(null, true)]
    public void PackageCategoryDecidesWhetherItIsAGame(string? category, bool isGame)
    {
        var pkg = BuildSyntheticPkg(
            BuildSyntheticImage(Encoding.ASCII.GetBytes("EBOOT")),
            (0x1000, category is null ? BuildSfo(("TITLE_ID", "CUSA99999")) : BuildSfo(("CATEGORY", category))));
        var packagePath = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-{Guid.NewGuid():N}.pkg");
        try
        {
            File.WriteAllBytes(packagePath, pkg);
            using var package = PkgPackage.Open(packagePath);
            Assert.Equal(category, package.Category);
            Assert.Equal(isGame, package.IsGameApplication);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    private static byte[] BuildSfo(params (string Key, string Value)[] values)
    {
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var index = new MemoryStream();
        foreach (var (key, value) in values)
        {
            var entry = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)keys.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0x0204);
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(12), (uint)data.Length);
            index.Write(entry);
            keys.Write(Encoding.ASCII.GetBytes(key + "\0"));
            data.Write(bytes);
        }

        var keyTable = 0x14 + (int)index.Length;
        var dataTable = keyTable + (int)keys.Length;
        var sfo = new byte[dataTable + (int)data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(sfo, 0x46535000);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(4), 0x101);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(8), (uint)keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(0xC), (uint)dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(0x10), (uint)values.Length);
        index.ToArray().CopyTo(sfo, 0x14);
        keys.ToArray().CopyTo(sfo, keyTable);
        data.ToArray().CopyTo(sfo, dataTable);
        return sfo;
    }

    [Fact]
    public void PfsStreamSeekRejectsNegativePositions()
    {
        using var stream = new PkgPfsStream(new MemoryStream(new byte[PkgCrypto.SectorSize * 2]), 0, PkgCrypto.SectorSize * 2, null, null);
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Current));
        Assert.Throws<IOException>(() => stream.Seek(-(PkgCrypto.SectorSize * 2 + 1), SeekOrigin.End));
        Assert.Equal(PkgCrypto.SectorSize * 2 - 4, stream.Seek(-4, SeekOrigin.End));
    }

    [Theory]
    [InlineData(0x1201u, "icon0_01.png")]
    [InlineData(0x1259u, "pic1_25.png")]
    [InlineData(0x1400u, "trophy/trophy00.trp")]
    [InlineData(0x1463u, "trophy/trophy99.trp")]
    public void NumberedEntryNamesFollowTheirFamilies(uint id, string expected) =>
        Assert.Equal(expected, PkgEntryNames.Get(id));

    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x10u)]
    [InlineData(0x20u)]
    [InlineData(0x400u)]
    public void KeyAndLicenceEntriesAreNotExtractedAsMetadata(uint id) =>
        Assert.Null(PkgEntryNames.Get(id));

    // The first 0x50 bytes of the PFS superblock of a retail fake PKG (Bloodborne, CUSA00900).
    [Fact]
    public void RetailPfsHeaderReadsTheBlockCountAt0x38()
    {
        var block = Convert.FromHexString(
            "0100000000000000" + "0B2A330100000000" + "0000000000000000" + "0000010008000000" +
            "0000010000000000" + "0100000000000000" + "E942000000000000" + "DA53060000000000" +
            "2C00000000000000" + "0000000000000000");
        var header = PfsHeader.Parse(block);
        Assert.Equal(20130315, header.Magic);
        Assert.Equal(0x10000, header.BlockSize);
        Assert.Equal(414682, header.BlockCount);
        Assert.Equal(17129, header.DinodeCount);
        Assert.Equal(44, header.DinodeBlockCount);
        Assert.Equal(0, header.SuperrootInode);
    }

    [Theory]
    [InlineData("SIGNALIS", "SIGNALIS")]
    [InlineData("The Last of Us Part I: Remastered", "The Last of Us Part I Remastered")]
    [InlineData("OMORI\nDeluxe ", "OMORI Deluxe")]
    [InlineData("A/B\\C?*", "A B C")]
    [InlineData("Trailing...", "Trailing")]
    [InlineData("  ", null)]
    [InlineData("..", null)]
    [InlineData(null, null)]
    public void TitlesBecomeSafeFolderNames(string? title, string? expected) =>
        Assert.Equal(expected, PkgPackage.ToFolderName(title));

    [Fact]
    public void InodeFieldsAreDecoded()
    {
        var data = new byte[PfsInode.Stride];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 0x81ED);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x11);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), 123_456);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), 50_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x60), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x64), 77);

        var inode = PfsInode.Parse(data);

        Assert.True(inode.IsFile);
        Assert.False(inode.IsDirectory);
        Assert.Equal(0x11u, inode.Flags);
        Assert.Equal(123_456, inode.Size);
        Assert.Equal(50_000, inode.SizeCompressed);
        Assert.Equal(2u, inode.Blocks);
        Assert.Equal(77u, inode.Location);
        Assert.True(PfsInode.Parse(new byte[PfsInode.Stride]).IsEmpty);
    }

    [Fact]
    public void DirentsAreDecodedUntilTheFirstEmptyEntry()
    {
        var block = new byte[PfscImage.BlockSize];
        var offset = 0;
        offset = WriteDirent(block, offset, 5, PfsDirentType.CurrentDirectory, ".");
        offset = WriteDirent(block, offset, 1, PfsDirentType.ParentDirectory, "..");
        offset = WriteDirent(block, offset, 6, PfsDirentType.File, "eboot.bin");
        WriteDirent(block, offset, 7, PfsDirentType.Directory, "sce_sys");

        var entries = PfsDirent.ParseBlock(block);

        Assert.Equal(4, entries.Count);
        Assert.Equal(new PfsDirent(6, 2, "eboot.bin"), entries[2]);
        Assert.Equal(new PfsDirent(7, 3, "sce_sys"), entries[3]);
    }

    [Fact]
    public void ImageIsReadThroughPfscAndWalked()
    {
        var content = new byte[150_000];
        new System.Random(5).NextBytes(content);
        for (var i = 0; i < 100_000; i++)
        {
            content[i] = (byte)(i % 7); // compressible head, random tail
        }

        var pfsc = BuildSyntheticImage(content);

        var image = new PfsImage(new PfscImage(new MemoryStream(pfsc), 0));
        Assert.Equal(PfscImage.BlockSize, image.Header.BlockSize);

        var root = image.GetUserRoot();
        Assert.True(root.IsDirectory);
        var entries = image.ReadDirectory(root);
        var file = Assert.Single(entries, entry => entry.Type == (int)PfsDirentType.File);
        Assert.Equal("eboot.bin", file.Name);

        var output = new MemoryStream();
        long reported = 0;
        image.CopyFile(image.GetInode(file.Inode), output, bytes => reported += bytes);

        Assert.Equal(content, output.ToArray());
        Assert.Equal(content.Length, reported);
    }

    [Fact]
    public void PfscWithoutMagicIsRejected() =>
        Assert.Throws<InvalidDataException>(() => new PfscImage(new MemoryStream(new byte[0x100]), 0));

    [Fact]
    public void SectorStreamDecryptsAtAnyOffsetAndLeavesTheHeaderPlain()
    {
        var dataKey = RandomNumberGenerator.GetBytes(16);
        var tweakKey = RandomNumberGenerator.GetBytes(16);
        var plain = new byte[PkgCrypto.SectorSize * 20];
        new System.Random(11).NextBytes(plain);

        var encrypted = (byte[])plain.Clone();
        using (var data = Aes.Create())
        using (var tweak = Aes.Create())
        {
            data.Key = dataKey;
            tweak.Key = tweakKey;
            for (var sector = 16; sector < 20; sector++)
            {
                XtsEncryptSector(data, tweak, encrypted.AsSpan(sector * PkgCrypto.SectorSize, PkgCrypto.SectorSize), (ulong)sector);
            }
        }

        // The image sits at a non-zero offset inside a larger container.
        var container = new byte[100 + encrypted.Length + 50];
        encrypted.CopyTo(container, 100);
        using var stream = new PkgPfsStream(new MemoryStream(container), 100, encrypted.Length, dataKey, tweakKey);

        var whole = new byte[plain.Length];
        stream.ReadExactly(whole);
        Assert.Equal(plain, whole);

        var slice = new byte[9000];
        stream.Position = 16 * PkgCrypto.SectorSize + 4000;
        stream.ReadExactly(slice);
        Assert.Equal(plain.AsSpan(16 * PkgCrypto.SectorSize + 4000, 9000).ToArray(), slice);

        stream.Position = 5;
        var header = new byte[10];
        stream.ReadExactly(header);
        Assert.Equal(plain.AsSpan(5, 10).ToArray(), header);
        Assert.Equal(0, stream.Read(new byte[4], 0, 0));
    }

    [Fact]
    public void XtsMatchesTheIeee1619Vector()
    {
        using var data = Aes.Create();
        using var tweak = Aes.Create();
        data.Key = new byte[16];
        tweak.Key = new byte[16];
        var buffer = Convert.FromHexString("917cf69ebd68b2ec9b9fe9a3eadda692cd43d2f59598ed858c02c2652fbf922e");

        PkgCrypto.XtsDecrypt(data, tweak, buffer, 0);

        Assert.Equal(new byte[32], buffer);
    }

    [Fact]
    public void PfsKeysSplitTheHmacIntoTweakThenData()
    {
        var ekpfs = new byte[32];
        var seed = new byte[16];
        var (dataKey, tweakKey) = PkgCrypto.DerivePfsKeys(ekpfs, seed);

        var input = new byte[20];
        input[0] = 1;
        var hash = HMACSHA256.HashData(ekpfs, input);
        Assert.Equal(hash[..16], tweakKey);
        Assert.Equal(hash[16..], dataKey);
    }

    [Fact]
    public void Pkcs1UnpaddingReturnsTheMessageAndRejectsBadBlocks()
    {
        var block = new byte[64];
        block[1] = 2;
        Array.Fill(block, (byte)0xAA, 2, 20);
        block[22] = 0;
        Encoding.ASCII.GetBytes("secret").CopyTo(block, 23);
        Array.Resize(ref block, 29);

        Assert.Equal("secret", Encoding.ASCII.GetString(PkgCrypto.UnpadPkcs1(block)));
        Assert.Throws<InvalidDataException>(() => PkgCrypto.UnpadPkcs1(new byte[64]));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void UnsafePathComponentsAreRefused(string component) =>
        Assert.Throws<InvalidDataException>(() => PkgPackage.ResolveSafe("root", [component]));

    [Fact]
    public void SafePathComponentsStayBelowTheRoot() =>
        Assert.Equal(Path.Combine("root", "sce_sys", "param.sfo"), PkgPackage.ResolveSafe("root", ["sce_sys", "param.sfo"]));

    [Fact]
    public void ExtractWritesMetadataAndPfsTreeWithProgress()
    {
        var eboot = Encoding.ASCII.GetBytes("FAKE EBOOT BIN");
        var pkg = BuildSyntheticPkg(
            BuildSyntheticImage(eboot),
            (0x1000, Encoding.ASCII.GetBytes("PARAM")),
            (0x1200, Encoding.ASCII.GetBytes("PNG")));
        var packagePath = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-{Guid.NewGuid():N}.pkg");
        var destination = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-out-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(packagePath, pkg);
            using var package = PkgPackage.Open(packagePath);
            Assert.Equal("CUSA99999", package.TitleId);

            var reports = new List<PkgExtractProgress>();
            package.Extract(destination, new CollectingProgress<PkgExtractProgress>(reports.Add));

            Assert.Equal("PARAM", File.ReadAllText(Path.Combine(destination, "sce_sys", "param.sfo")));
            Assert.Equal("PNG", File.ReadAllText(Path.Combine(destination, "sce_sys", "icon0.png")));
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(destination, "eboot.bin")));
            Assert.NotEmpty(reports);
            Assert.Equal(reports[^1].BytesTotal, reports[^1].BytesDone);
            Assert.Equal(eboot.Length, reports[^1].BytesTotal);
            Assert.All(reports, report => Assert.True(report.BytesDone <= report.BytesTotal));
        }
        finally
        {
            File.Delete(packagePath);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public void ExtractReportsProgressInsideALargeFile()
    {
        var eboot = new byte[PfscImage.BlockSize * 4];
        System.Random.Shared.NextBytes(eboot);
        WithSyntheticPackage(BuildSyntheticPkg(BuildSyntheticImage(eboot)), (package, destination) =>
        {
            var reports = new List<PkgExtractProgress>();
            package.Extract(destination, new CollectingProgress<PkgExtractProgress>(reports.Add), default, PfscImage.BlockSize, null);

            var midFile = reports.Where(report => report.BytesDone > 0 && report.BytesDone < eboot.Length).Select(report => report.BytesDone).Distinct().ToList();
            Assert.True(midFile.Count >= 3, $"expected mid-file reports, got {midFile.Count}");
            Assert.Equal(eboot, File.ReadAllBytes(Path.Combine(destination, "eboot.bin")));
        });
    }

    [Fact]
    public void ExtractThrottlesProgressWithTheDefaultStep()
    {
        var eboot = new byte[PfscImage.BlockSize * 4];
        WithSyntheticPackage(BuildSyntheticPkg(BuildSyntheticImage(eboot)), (package, destination) =>
        {
            var reports = new List<PkgExtractProgress>();
            package.Extract(destination, new CollectingProgress<PkgExtractProgress>(reports.Add));
            Assert.DoesNotContain(reports, report => report.BytesDone > 0 && report.BytesDone < eboot.Length);
        });
    }

    [Fact]
    public void ExtractRefusesWhenTheDriveIsTooSmallAndWritesNothing()
    {
        var pkg = BuildSyntheticPkg(BuildSyntheticImage(new byte[1000]), (0x1000, Encoding.ASCII.GetBytes("PARAM")));
        WithSyntheticPackage(pkg, (package, destination) =>
        {
            Assert.Equal(1005, package.GetInstalledSize());
            var exception = Assert.Throws<IOException>(() =>
                package.Extract(destination, null, default, PkgPackage.ProgressStepBytes, availableBytes: 1004));
            Assert.Contains("Not enough free disk space", exception.Message);
            Assert.False(Directory.Exists(destination));

            package.Extract(destination, null, default, PkgPackage.ProgressStepBytes, availableBytes: 1005);
            Assert.True(File.Exists(Path.Combine(destination, "eboot.bin")));
        });
    }

    private static void WithSyntheticPackage(byte[] pkg, Action<PkgPackage, string> body)
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-{Guid.NewGuid():N}.pkg");
        var destination = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-out-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(packagePath, pkg);
            using var package = PkgPackage.Open(packagePath);
            body(package, destination);
        }
        finally
        {
            File.Delete(packagePath);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public void ExtractRefusesAnEntryLargerThanThePackage()
    {
        var pkg = BuildSyntheticPkg(
            BuildSyntheticImage(Encoding.ASCII.GetBytes("EBOOT")),
            (0x1000, Encoding.ASCII.GetBytes("PARAM")));
        BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(PkgHeader.Size + 20), uint.MaxValue);
        var packagePath = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-{Guid.NewGuid():N}.pkg");
        var destination = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-out-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(packagePath, pkg);
            using var package = PkgPackage.Open(packagePath);
            Assert.Throws<InvalidDataException>(() => package.Extract(destination));
        }
        finally
        {
            File.Delete(packagePath);
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
    }

    [Fact]
    public void ExtractRefusesAnImplausibleInodeTable()
    {
        const int imageOffset = 0x20000;
        var pkg = BuildSyntheticPkg(BuildSyntheticImage(Encoding.ASCII.GetBytes("EBOOT")));
        BinaryPrimitives.WriteInt64LittleEndian(pkg.AsSpan(imageOffset + 0x20000 + 0x30), 100_000);
        var packagePath = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-{Guid.NewGuid():N}.pkg");
        var destination = Path.Combine(Path.GetTempPath(), $"sharpemu-pkg-out-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(packagePath, pkg);
            using var package = PkgPackage.Open(packagePath);
            Assert.Throws<InvalidDataException>(() => package.Extract(destination));
        }
        finally
        {
            File.Delete(packagePath);
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

    // A minimal PKG with one unencrypted PFS image and a few sce_sys entries. No key entries:
    // the reader falls back to reading the image as-is, which keeps this fixture self-contained.
    private static byte[] BuildSyntheticPkg(byte[] image, params (uint Id, byte[] Data)[] entries)
    {
        const int imageOffset = 0x20000;
        // FindOffset starts looking for PFSC after the image's own header blocks.
        const int pfsPrefix = 0x20000;
        var storedImage = new byte[pfsPrefix + image.Length];
        image.CopyTo(storedImage, pfsPrefix);
        var tableOffset = PkgHeader.Size;
        var bodyOffset = tableOffset + entries.Length * PkgEntry.Size_;
        var package = new byte[imageOffset + storedImage.Length];

        BinaryPrimitives.WriteUInt32BigEndian(package, PkgHeader.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x10), (uint)entries.Length);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x18), (uint)tableOffset);
        BinaryPrimitives.WriteUInt64BigEndian(package.AsSpan(0x20), (ulong)bodyOffset);
        Encoding.ASCII.GetBytes("UP9000-CUSA99999_00-SYNTHETIC000000").CopyTo(package, 0x40);
        BinaryPrimitives.WriteUInt32BigEndian(package.AsSpan(0x404), 1);
        BinaryPrimitives.WriteUInt64BigEndian(package.AsSpan(0x410), imageOffset);
        BinaryPrimitives.WriteUInt64BigEndian(package.AsSpan(0x418), (ulong)storedImage.Length);
        BinaryPrimitives.WriteUInt64BigEndian(package.AsSpan(0x430), (ulong)package.Length);

        var body = bodyOffset;
        for (var i = 0; i < entries.Length; i++)
        {
            WriteEntry(package.AsSpan(tableOffset + i * PkgEntry.Size_, PkgEntry.Size_),
                entries[i].Id, 0, 0, 0, (uint)body, (uint)entries[i].Data.Length);
            entries[i].Data.CopyTo(package, body);
            body += entries[i].Data.Length;
        }

        storedImage.CopyTo(package, imageOffset);
        return package;
    }

    private static void WriteEntry(Span<byte> row, uint id, uint nameOffset, uint flags1, uint flags2, uint offset, uint size)
    {
        BinaryPrimitives.WriteUInt32BigEndian(row, id);
        BinaryPrimitives.WriteUInt32BigEndian(row[4..], nameOffset);
        BinaryPrimitives.WriteUInt32BigEndian(row[8..], flags1);
        BinaryPrimitives.WriteUInt32BigEndian(row[12..], flags2);
        BinaryPrimitives.WriteUInt32BigEndian(row[16..], offset);
        BinaryPrimitives.WriteUInt32BigEndian(row[20..], size);
    }

    private static int WriteDirent(byte[] block, int offset, int inode, PfsDirentType type, string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var entrySize = (16 + nameBytes.Length + 7) & ~7;
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(offset), inode);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(offset + 4), (int)type);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(offset + 8), nameBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(offset + 12), entrySize);
        nameBytes.CopyTo(block, offset + 16);
        return offset + entrySize;
    }

    private static void WriteInode(byte[] block, int slot, ushort mode, long size, uint blocks, uint location)
    {
        var span = block.AsSpan(slot * PfsInode.Stride, PfsInode.Stride);
        BinaryPrimitives.WriteUInt16LittleEndian(span, mode);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x60..], blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x64..], location);
    }

    // Logical blocks: 0 header, 1 inodes, 2 super root dir, 3 uroot dir, 4.. file data.
    // Inodes: 0 super root, 1 uroot, 2 eboot.bin. Block 4 is stored compressed, the rest raw.
    private static byte[] BuildSyntheticImage(byte[] content)
    {
        var fileBlocks = (content.Length + PfscImage.BlockSize - 1) / PfscImage.BlockSize;
        var logical = new List<byte[]>();

        var header = new byte[PfscImage.BlockSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x20), PfscImage.BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x28), 1); // as in retail images
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x38), 4 + fileBlocks);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x30), 3);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x40), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x48), 0);
        logical.Add(header);

        var inodes = new byte[PfscImage.BlockSize];
        WriteInode(inodes, 0, PfsInode.ModeDirectory, PfscImage.BlockSize, 1, 2);
        WriteInode(inodes, 1, PfsInode.ModeDirectory, PfscImage.BlockSize, 1, 3);
        WriteInode(inodes, 2, PfsInode.ModeFile, content.Length, (uint)fileBlocks, 4);
        logical.Add(inodes);

        var superRoot = new byte[PfscImage.BlockSize];
        var offset = WriteDirent(superRoot, 0, 1, PfsDirentType.File, "flat_path_table");
        WriteDirent(superRoot, offset, 1, PfsDirentType.Directory, "uroot");
        logical.Add(superRoot);

        var userRoot = new byte[PfscImage.BlockSize];
        offset = WriteDirent(userRoot, 0, 1, PfsDirentType.CurrentDirectory, ".");
        offset = WriteDirent(userRoot, offset, 0x7FFF, PfsDirentType.ParentDirectory, "..");
        WriteDirent(userRoot, offset, 2, PfsDirentType.File, "eboot.bin");
        logical.Add(userRoot);

        for (var i = 0; i < fileBlocks; i++)
        {
            var block = new byte[PfscImage.BlockSize];
            Array.Copy(content, i * PfscImage.BlockSize, block, 0, Math.Min(PfscImage.BlockSize, content.Length - i * PfscImage.BlockSize));
            logical.Add(block);
        }

        var stored = new List<byte[]>();
        foreach (var block in logical)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(block);
            }

            stored.Add(compressed.Length < PfscImage.BlockSize ? compressed.ToArray() : block);
        }

        var tableOffset = 0x30;
        var dataStart = tableOffset + (stored.Count + 1) * 8;
        var image = new MemoryStream();
        var pfscHeader = new byte[dataStart];
        BinaryPrimitives.WriteUInt32LittleEndian(pfscHeader, PfscImage.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(pfscHeader.AsSpan(0x0C), PfscImage.BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x10), PfscImage.BlockSize);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x18), tableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x20), dataStart);
        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(0x28), (long)stored.Count * PfscImage.BlockSize);
        long position = dataStart;
        for (var i = 0; i < stored.Count; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(tableOffset + i * 8), position);
            position += stored[i].Length;
        }

        BinaryPrimitives.WriteInt64LittleEndian(pfscHeader.AsSpan(tableOffset + stored.Count * 8), position);
        image.Write(pfscHeader);
        foreach (var block in stored)
        {
            image.Write(block);
        }

        return image.ToArray();
    }

    private static void XtsEncryptSector(Aes data, Aes tweakCipher, Span<byte> sector, ulong number)
    {
        Span<byte> tweak = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(tweak, number);
        tweakCipher.EncryptEcb(tweak, tweak, PaddingMode.None);
        Span<byte> block = stackalloc byte[16];
        for (var offset = 0; offset < sector.Length; offset += 16)
        {
            var current = sector.Slice(offset, 16);
            for (var i = 0; i < 16; i++) block[i] = (byte)(current[i] ^ tweak[i]);
            data.EncryptEcb(block, block, PaddingMode.None);
            for (var i = 0; i < 16; i++) current[i] = (byte)(block[i] ^ tweak[i]);
            PkgCrypto.MultiplyTweak(tweak);
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Np;
using SharpEmu.GUI;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Np;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmTrophyTests : IDisposable
{
    private const string Key = "00112233445566778899AABBCCDDEEFF";
    private const ulong Address = 0x100000000;
    private const string Xml = """
        <trophyconf><title-name>Hunters</title-name><title-detail>Test set</title-detail>
        <trophy id="0" ttype="P" pid="-1" hidden="no"><name>Platinum</name><detail>Base game</detail></trophy>
        <trophy id="1" ttype="G" pid="0" hidden="no"><name>First</name><detail>First hunt</detail></trophy>
        <trophy id="2" ttype="B" pid="0" hidden="yes"><name>Second</name><detail>Second hunt</detail></trophy>
        <trophy id="3" ttype="B" pid="-1" gid="1" hidden="no"><name>Expansion</name><detail>DLC hunt</detail></trophy>
        </trophyconf>
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SharpEmuGnmTrophies", Guid.NewGuid().ToString("N"));
    private readonly string? _savedRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
    private readonly string _oldTitle = KernelMemoryCompatExports.ApplicationTitleId;
    private readonly ITrophyPackageReader? _oldReader = TrophyCatalog.CompatibilityReader;
    private readonly string _oldApp0 = KernelMemoryCompatExports.ResolveGuestPath("/app0");
    private readonly FakeCpuMemory _memory = new(Address, 0x4000);
    private readonly CpuContext _cpu;
    private int _context;
    private int _handle;

    public GnmTrophyTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sce_sys", "trophy"));
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", Path.Combine(_root, "user", "savedata"));
        KernelMemoryCompatExports.ConfigureApplicationInfo("CUSA_TEST_TROPHIES");
        KernelMemoryCompatExports.RegisterGuestPathMount("/app0", _root);
        TrophyService.ResetForTests();
        GnmTrophyPackageReader.Configure(Key);
        _cpu = new CpuContext(_memory, Generation.Gen4);
        WriteFixture();
    }

    public void Dispose()
    {
        _cpu[CpuRegister.Rdi] = (ulong)_context;
        if (_context != 0) GnmTrophyExports.DestroyContext(_cpu);
        _cpu[CpuRegister.Rdi] = (ulong)_handle;
        if (_handle != 0) GnmTrophyExports.DestroyHandle(_cpu);
        KernelMemoryCompatExports.UnregisterGuestPathMount("/app0");
        if (Directory.Exists(_oldApp0)) KernelMemoryCompatExports.RegisterGuestPathMount("/app0", _oldApp0);
        KernelMemoryCompatExports.ConfigureApplicationInfo(_oldTitle);
        TrophyCatalog.CompatibilityReader = _oldReader;
        TrophyService.ResetForTests();
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _savedRoot);
        Directory.Delete(_root, true);
    }

    [Fact]
    public void DecryptsRetailMetadataAndLoadsLocalizedNamesAndIcons()
    {
        var trophies = TrophyCatalog.Load(_root, "CUSA_TEST_TROPHIES", language: "tr-TR");
        Assert.Equal(4, trophies.Count);
        Assert.Equal("Birinci", trophies[1].Name);
        Assert.Equal("First hunt", trophies[1].Description);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, trophies[1].IconPng);
        Assert.True(trophies[2].Hidden);
        Assert.Equal(-1, trophies[1].GroupId);
        Assert.Equal(1, trophies[3].GroupId);
        Assert.All(TrophyCatalog.Load(_root, "CUSA_TEST_TROPHIES", false), t => Assert.Null(t.IconPng));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FFEEDDCCBBAA99887766554433221100")]
    public void MissingOrWrongKeyReturnsNoFabricatedTrophies(string key)
    {
        GnmTrophyPackageReader.Configure(key);
        Assert.Empty(TrophyCatalog.Load(_root, "CUSA_TEST_TROPHIES"));
    }

    [Fact]
    public void KeyChangesInvalidateCachedMetadataAndRemovalHidesIt()
    {
        GnmTrophyPackageReader.Configure(null);
        var emptyStamp = TrophyCatalog.ContentStamp(_root);
        Assert.Empty(TrophyCache.Load(_root, "CUSA_TEST_TROPHIES", "en-US"));
        GnmTrophyPackageReader.Configure(Key);
        Assert.NotEqual(emptyStamp, TrophyCatalog.ContentStamp(_root));
        Assert.Equal(4, TrophyCache.Load(_root, "CUSA_TEST_TROPHIES", "en-US").Count);
        GnmTrophyPackageReader.Configure(null);
        Assert.Empty(TrophyCache.Load(_root, "CUSA_TEST_TROPHIES", "en-US"));
    }

    [Theory]
    [InlineData("short", "")]
    [InlineData("00112233445566778899aabbccddeeff", Key)]
    [InlineData(null, "")]
    public void GuiSettingsNormalizeTheKeyAndPreserveItOnReload(string? key, string expected)
    {
        var settings = GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(new { GnmTrophyKey = key }));
        Assert.Equal(expected, settings.GnmTrophyKey);
        Assert.Equal(expected, GuiSettings.NormalizeFromJson(JsonSerializer.Serialize(settings)).GnmTrophyKey);
    }

    [Fact]
    public void RejectsOutOfBoundsArchiveEntries()
    {
        var path = Path.Combine(_root, "sce_sys", "trophy", "trophy00.trp");
        var data = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(96 + 32), ulong.MaxValue);
        File.WriteAllBytes(path, data);
        Assert.Empty(TrophyCatalog.Load(_root, "CUSA_TEST_TROPHIES"));
    }

    [Fact]
    public void RejectsXmlExternalEntities()
    {
        WriteFixture("<!DOCTYPE trophyconf [<!ENTITY x SYSTEM 'file:///unavailable'>]>" + Xml.Replace("Hunters", "&x;"));
        Assert.Empty(TrophyCatalog.Load(_root, "CUSA_TEST_TROPHIES"));
    }

    [Fact]
    public void UnlocksPersistAndAwardPlatinumWithoutRequiringDlc()
    {
        CreateRegisteredContext();
        Assert.Equal(0, Unlock(1));
        Assert.Equal(-1, ReadInt(Address + 32));
        Assert.Equal(0, Unlock(2));
        Assert.Equal(0, ReadInt(Address + 32));
        Assert.Equal(unchecked((int)0x8055160C), Unlock(2));
        TrophyService.ResetForTests();
        Assert.Equal(3, TrophyService.Trophies.Count(t => t.Unlocked));
        var trophies = TrophyCatalog.Load(_root, "CUSA_TEST_TROPHIES");
        Assert.True(trophies[0].Unlocked);
        Assert.NotNull(trophies[1].UnlockedAtUtc);
        Assert.False(trophies[3].Unlocked);
        var progress = File.ReadAllText(TrophyService.UnlockFilePath("CUSA_TEST_TROPHIES"));
        Assert.DoesNotContain(Key, progress);
        _cpu[CpuRegister.Rdx] = Address + 64;
        _cpu[CpuRegister.Rcx] = Address + 80;
        Assert.Equal(0, GnmTrophyExports.GetTrophyUnlockState(_cpu));
        Assert.Equal(7, ReadInt(Address + 64));
        Assert.Equal(4, ReadInt(Address + 80));
    }

    [Fact]
    public void Ps4InfoUsesSizedStructuresAndPreservesSurroundingMemory()
    {
        CreateRegisteredContext();
        var details = new byte[1184];
        details.AsSpan().Fill(0xCD);
        BinaryPrimitives.WriteUInt64LittleEndian(details.AsSpan(0, 8), 1176);
        Assert.True(_memory.TryWrite(Address + 256, details));
        _cpu[CpuRegister.Rdx] = 1;
        _cpu[CpuRegister.Rcx] = Address + 256;
        _cpu[CpuRegister.R8] = 0;
        Assert.Equal(0, GnmTrophyExports.GetTrophyInfo(_cpu));
        Assert.True(_memory.TryRead(Address + 256, details));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(details.AsSpan(12)));
        Assert.Equal("First", Encoding.UTF8.GetString(details.AsSpan(24, 5)));
        Assert.All(details[1176..], b => Assert.Equal(0xCD, b));
        _cpu[CpuRegister.Rcx] = Address + 260;
        Assert.Equal(unchecked((int)0x80551604), GnmTrophyExports.GetTrophyInfo(_cpu));
    }

    [Fact]
    public void RegistryExposesPs4TrophyCallsOnlyInGnmMode()
    {
        var exports = GnmExportRegistry.CreateExports().Where(e => e.LibraryName == "libSceNpTrophy").ToArray();
        Assert.Contains(exports, e => e.Nid == "28xmRUFao68");
        Assert.All(exports, e => Assert.Equal(Generation.Gen4, e.Target));
    }

    [Fact]
    public void GameAndGroupInfoReportPs4CountsAndProgress()
    {
        CreateRegisteredContext();
        Assert.Equal(0, Unlock(1));
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(size, 1184);
        Assert.True(_memory.TryWrite(Address + 256, size));
        BinaryPrimitives.WriteUInt64LittleEndian(size, 32);
        Assert.True(_memory.TryWrite(Address + 2048, size));
        _cpu[CpuRegister.Rdx] = Address + 256;
        _cpu[CpuRegister.Rcx] = Address + 2048;
        Assert.Equal(0, GnmTrophyExports.GetGameInfo(_cpu));
        Assert.Equal(1, ReadInt(Address + 264));
        Assert.Equal(4, ReadInt(Address + 268));
        Assert.Equal(1, ReadInt(Address + 2056));
        Assert.Equal(25, ReadInt(Address + 2076));
        _cpu[CpuRegister.Rdx] = 1;
        _cpu[CpuRegister.Rcx] = Address + 256;
        _cpu[CpuRegister.R8] = 0;
        Assert.Equal(0, GnmTrophyExports.GetGroupInfo(_cpu));
        Assert.Equal(1, ReadInt(Address + 268));
    }

    [Fact]
    public void IconQueriesReportRequiredCapacityWithoutOverwritingTheBuffer()
    {
        CreateRegisteredContext();
        Span<byte> size = stackalloc byte[8];
        _cpu[CpuRegister.Rdx] = 1;
        _cpu[CpuRegister.Rcx] = 0;
        _cpu[CpuRegister.R8] = Address + 128;
        Assert.Equal(0, GnmTrophyExports.GetTrophyIcon(_cpu));
        Assert.Equal(4, ReadInt(Address + 128));
        BinaryPrimitives.WriteUInt64LittleEndian(size, 3);
        Assert.True(_memory.TryWrite(Address + 128, size));
        Assert.True(_memory.TryWrite(Address + 256, new byte[] { 0xCD, 0xCD, 0xCD, 0xCD }));
        _cpu[CpuRegister.Rcx] = Address + 256;
        Assert.Equal(unchecked((int)0x80551605), GnmTrophyExports.GetTrophyIcon(_cpu));
        Assert.Equal(unchecked((int)0xCDCDCDCD), ReadInt(Address + 256));
        Assert.Equal(0, GnmTrophyExports.GetTrophyIcon(_cpu));
        Assert.Equal(0x474E5089, ReadInt(Address + 256));
    }

    [Fact]
    public void AbortedAndDestroyedHandlesAreRejected()
    {
        CreateRegisteredContext();
        _cpu[CpuRegister.Rdi] = (ulong)_handle;
        Assert.Equal(0, GnmTrophyExports.AbortHandle(_cpu));
        _cpu[CpuRegister.Rdi] = (ulong)_context;
        Assert.Equal(unchecked((int)0x80551607), Unlock(1));
        _cpu[CpuRegister.Rdi] = (ulong)_handle;
        Assert.Equal(0, GnmTrophyExports.DestroyHandle(_cpu));
        _cpu[CpuRegister.Rdi] = (ulong)_context;
        Assert.Equal(unchecked((int)0x80551608), Unlock(1));
        _handle = 0;
    }

    private void CreateRegisteredContext()
    {
        _cpu[CpuRegister.Rdi] = Address;
        _cpu[CpuRegister.Rsi] = 1;
        _cpu[CpuRegister.Rdx] = 0;
        Assert.Equal(0, GnmTrophyExports.CreateContext(_cpu));
        _context = ReadInt(Address);
        _cpu[CpuRegister.Rdi] = Address + 4;
        Assert.Equal(0, GnmTrophyExports.CreateHandle(_cpu));
        _handle = ReadInt(Address + 4);
        _cpu[CpuRegister.Rdi] = (ulong)_context;
        _cpu[CpuRegister.Rsi] = (ulong)_handle;
        Assert.Equal(0, GnmTrophyExports.RegisterContext(_cpu));
    }

    private int Unlock(int id)
    {
        _cpu[CpuRegister.Rdx] = (ulong)id;
        _cpu[CpuRegister.Rcx] = Address + 32;
        return GnmTrophyExports.UnlockTrophy(_cpu);
    }

    private int ReadInt(ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private void WriteFixture(string xml = Xml)
    {
        var binding = new byte[144];
        BinaryPrimitives.WriteUInt32BigEndian(binding, 0xD294A018);
        BinaryPrimitives.WriteUInt64BigEndian(binding.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt16BigEndian(binding.AsSpan(128), 0x10);
        BinaryPrimitives.WriteUInt16BigEndian(binding.AsSpan(130), 12);
        Encoding.ASCII.GetBytes("NPWR05818_00").CopyTo(binding.AsSpan(132));
        File.WriteAllBytes(Path.Combine(_root, "sce_sys", "npbind.dat"), binding);
        var files = new Dictionary<string, byte[]>
        {
            ["TROPCONF.ESFM"] = Encrypt(xml),
            ["TROP_01.ESFM"] = Encrypt(xml),
            ["TROP_19.ESFM"] = Encrypt(xml.Replace(">First<", ">Birinci<")),
            ["TROP001.PNG"] = [137, 80, 78, 71],
        };
        var offset = 96 + files.Count * 64;
        var archive = new byte[offset + files.Sum(f => f.Value.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(archive, 0xDCA24D00);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt64BigEndian(archive.AsSpan(8), (ulong)archive.Length);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(16), (uint)files.Count);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(20), 64);
        var index = 0;
        foreach (var (name, bytes) in files)
        {
            var entry = archive.AsSpan(96 + index++ * 64, 64);
            Encoding.ASCII.GetBytes(name).CopyTo(entry);
            BinaryPrimitives.WriteUInt64BigEndian(entry[32..], (ulong)offset);
            BinaryPrimitives.WriteUInt64BigEndian(entry[40..], (ulong)bytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[48..], name.EndsWith(".ESFM") ? 3u : 0u);
            bytes.CopyTo(archive.AsSpan(offset));
            offset += bytes.Length;
        }
        File.WriteAllBytes(Path.Combine(_root, "sce_sys", "trophy", "trophy00.trp"), archive);
    }

    private static byte[] Encrypt(string xml)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromHexString(Key);
        var id = new byte[16];
        Encoding.ASCII.GetBytes("NPWR05818_00").CopyTo(id, 0);
        aes.Key = aes.EncryptEcb(id, PaddingMode.None);
        var iv = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        return [.. iv, .. aes.EncryptCbc(Encoding.UTF8.GetBytes(xml), iv, PaddingMode.PKCS7)];
    }
}

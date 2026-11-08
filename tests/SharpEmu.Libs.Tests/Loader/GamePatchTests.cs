// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using SharpEmu.Core;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Patches;
using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using SharpEmu.Libs.Tests.FileSystem;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed partial class GamePatchTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-patches-").FullName;
    private GamePatchRepository Repository => new(_directory);
    private string XmlPath => Path.Combine(_directory, "AnyGameName.xml");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Xml(string lines, string version = "01.004.000", string executable = "eboot.bin") => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Patch>
          <TitleID><ID>PPSA00001</ID><ID>PPSA00002</ID></TitleID>
          <Metadata Title="Test game" Name="Test patch" Author="Test author" PatchVer="1.0"
                    AppVer="{{version}}" ImageBase="0x10000000" AppElf="{{executable}}">
            <PatchList>{{lines}}</PatchList>
          </Metadata>
        </Patch>
        """;

    private GamePatch WritePatch(string lines = "<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"aabbcc\"/>")
    {
        File.WriteAllText(XmlPath, Xml(lines));
        return Assert.Single(Repository.Load("PPSA00001"));
    }

    private static ProgramHeader Segment(ulong address = 0, ulong size = 0x100,
        ProgramHeaderFlags flags = ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute)
    {
        var bytes = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), address);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), size);
        return MemoryMarshal.Read<ProgramHeader>(bytes);
    }

    private static VirtualMemory Memory(ulong address = 0x8_0000_0000)
    {
        var memory = new VirtualMemory();
        memory.Map(address, 0x100, 0, new byte[0x100], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute);
        return memory;
    }

    [Fact]
    public void TitleIdsComeFromXmlAndSelectionsArePerTitleAndOffByDefault()
    {
        var patch = WritePatch();
        Assert.Equal("1.0", patch.PatchVersion);
        Assert.Empty(Repository.Load("PPSA99999"));
        Assert.Single(Repository.Load("ppsa00002"));
        Assert.Empty(Repository.LoadEnabled("PPSA00001"));
        var original = File.ReadAllBytes(XmlPath);

        Repository.SetEnabled("PPSA00001", patch, true);

        Assert.Contains(patch.Id, new GamePatchRepository(_directory).LoadEnabled("PPSA00001"));
        Assert.Empty(Repository.LoadEnabled("PPSA00002"));
        Assert.Equal(original, File.ReadAllBytes(XmlPath));
        Repository.SetEnabled("PPSA00001", patch, false);
        Assert.Empty(Repository.LoadEnabled("PPSA00001"));
    }

    [Theory]
    [InlineData("01.004.000", true)]
    [InlineData("1.4.0", true)]
    [InlineData("01.007.000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EnabledPatchesStillRequireTheInstalledVersion(string? version, bool matches)
    {
        var patch = WritePatch();
        Repository.SetEnabled("PPSA00001", patch, true);
        Assert.Equal(matches, patch.MatchesVersion(version));
        Assert.Equal(matches ? 1 : 0, Repository.LoadActive("PPSA00001", version, _ => { }).Count);
    }

    [Fact]
    public void IdenticalNamesAndPatchRevisionsKeepTheirOwnTargetGameVersions()
    {
        const string line = "<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"90\"/>";
        var document = XDocument.Parse(Xml(line, "01.000.000"));
        document.Root!.Add(XDocument.Parse(Xml(line, "01.004.000")).Root!.Element("Metadata"));
        document.Save(XmlPath);

        var patches = Repository.Load("PPSA00001");
        Assert.Equal(2, patches.Count);
        Assert.Equal(patches[0].Name, patches[1].Name);
        Assert.Equal("1.0", patches[0].PatchVersion);
        Assert.Equal("1.0", patches[1].PatchVersion);
        Assert.Equal("01.000.000", patches[0].AppVersion);
        Assert.Equal("01.004.000", patches[1].AppVersion);
        Assert.NotEqual(patches[0].Id, patches[1].Id);
    }

    [Fact]
    public void ChangedPatchCodeNeedsToBeEnabledAgain()
    {
        var patch = WritePatch();
        Repository.SetEnabled("PPSA00001", patch, true);
        var changed = WritePatch("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"dd\"/>");
        Assert.NotEqual(patch.Id, changed.Id);
        Assert.Empty(Repository.LoadActive("PPSA00001", "01.004.000", _ => { }));
    }

    [Theory]
    [InlineData("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"z1\"/>")]
    [InlineData("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"a\"/>")]
    [InlineData("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"\"/>")]
    [InlineData("<Line Type=\"bytes\" Address=\"0x0fffffff\" Value=\"01\"/>")]
    [InlineData("<Line Type=\"mask\" Address=\"0x10000010\" Value=\"01\"/>")]
    [InlineData("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"01\" Offset=\"8\"/>")]
    [InlineData("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"01\"><Unknown/></Line>")]
    [InlineData("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"01\"/><Line Type=\"mask\" Address=\"0\" Value=\"01\"/>")]
    [InlineData("<Unknown/>")]
    [InlineData("")]
    public void InvalidOrUnsupportedLinesDisableTheWholePatch(string line)
    {
        var patch = WritePatch(line);
        Assert.NotNull(patch.Error);
        Assert.Empty(patch.Writes);
        Assert.Throws<InvalidOperationException>(() => Repository.SetEnabled("PPSA00001", patch, true));
    }

    [Fact]
    public void MalformedXmlAndExternalEntitiesDoNotHideValidFiles()
    {
        WritePatch();
        File.WriteAllText(Path.Combine(_directory, "broken.xml"), "<Patch>");
        File.WriteAllText(Path.Combine(_directory, "external.xml"), """
            <!DOCTYPE Patch [<!ENTITY external SYSTEM "file:///does-not-exist">]>
            <Patch><TitleID><ID>&external;</ID></TitleID></Patch>
            """);
        var warnings = new List<string>();
        Assert.Single(Repository.Load("PPSA00001", warnings.Add));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void InvalidSelectionJsonIsNotSilentlyOverwritten()
    {
        var patch = WritePatch();
        var path = Path.Combine(_directory, "PPSA00001.json");
        File.WriteAllText(path, "broken");
        var warnings = new List<string>();
        Assert.Empty(Repository.LoadActive("PPSA00001", "01.004.000", warnings.Add));
        Assert.Single(warnings);
        Assert.Throws<System.Text.Json.JsonException>(() => Repository.SetEnabled("PPSA00001", patch, true));
        Assert.Equal("broken", File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => Repository.LoadEnabled("../outside"));
    }

    [Fact]
    public void AddressesAreRelocatedFromTheXmlBaseToTheLoadedImage()
    {
        var patch = WritePatch();
        var memory = Memory();
        Assert.True(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error), error);
        var actual = new byte[3];
        Assert.True(memory.TryRead(0x8_0000_0010, actual));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, actual);
    }

    [Fact]
    public void RelocationUsesTheFirstLoadableSegmentAndCannotWriteIntoImageGaps()
    {
        var patch = WritePatch();
        var memory = Memory(0x8_0000_2000);
        Assert.True(patch.TryApply(memory, [Segment(0x2000)], 0x8_0000_0000, out var error), error);
        var actual = new byte[3];
        Assert.True(memory.TryRead(0x8_0000_2010, actual));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, actual);

        var gap = WritePatch("<Line Type=\"bytes\" Address=\"0x10000180\" Value=\"aa\"/>");
        memory.Map(0x8_0000_2200, 0x100, 0, [], ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        Assert.False(gap.TryApply(memory, [Segment(0x2000), Segment(0x2200)], 0x8_0000_0000, out _));
    }

    [Fact]
    public void AllRangesAreValidatedBeforeAnyBytesAreWritten()
    {
        var patch = WritePatch("""
            <Line Type="bytes" Address="0x10000010" Value="aa"/>
            <Line Type="bytes" Address="0x100000ff" Value="bbcc"/>
            """);
        var memory = Memory();
        Assert.False(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error));
        Assert.Contains("outside", error);
        var actual = new byte[1];
        Assert.True(memory.TryRead(0x8_0000_0010, actual));
        Assert.Equal(0, actual[0]);
    }

    [Fact]
    public void AddressOverflowCannotTargetUnrelatedMemory()
    {
        var patch = WritePatch("<Line Type=\"bytes\" Address=\"ffffffffffffffff\" Value=\"aa\"/>");
        Assert.False(patch.TryApply(Memory(), [Segment()], 0x8_0000_0000, out _));
    }

    [Fact]
    public void APartiallyFailingWriteRestoresAllEarlierWrites()
    {
        var patch = WritePatch("""
            <Line Type="bytes" Address="0x10000010" Value="aa"/>
            <Line Type="bytes" Address="0x10000020" Value="bbcc"/>
            """);
        var memory = new FailingMemory(Memory());
        Assert.False(patch.TryApply(memory, [Segment()], 0x8_0000_0000, out var error));
        Assert.Contains("restored", error);
        var actual = new byte[0x30];
        Assert.True(memory.TryRead(0x8_0000_0000, actual));
        Assert.All(actual, value => Assert.Equal(0, value));
    }

    private sealed class FailingMemory(ICpuMemory memory) : ICpuMemory
    {
        private int _writes;
        public bool TryRead(ulong address, Span<byte> destination) => memory.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (++_writes != 2) return memory.TryWrite(address, source);
            memory.TryWrite(address, source[..1]);
            return false;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoaderAppliesEnabledPatchesOnlyToTheirTargetExecutable(bool fromArchive)
    {
        WritePatch("""
            <Line Type="bytes" Address="0x10000010" Value="aabbcc"/>
            <Line Type="bytes32" Address="0x10000020" Value="0x12345678"/>
            <Line Type="utf16" Address="0x10000030" Value="Test"/>
            <Line Type="mask" Address="b0 b1 b2" Value="cc" Offset="+1"/>
            """);
        var moduleXml = Xml("<Line Type=\"bytes\" Address=\"0x10000010\" Value=\"ddeeff\"/>", executable: "libTest.sprx");
        File.WriteAllText(Path.Combine(_directory, "Module.xml"), moduleXml);
        foreach (var entry in Repository.Load("PPSA00001")) Repository.SetEnabled("PPSA00001", entry, true);
        var game = Path.Combine(_directory, "game");
        Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
        File.WriteAllText(Path.Combine(game, "sce_sys", "param.json"), """
            { "titleId": "PPSA00001", "contentVersion": "01.004.000", "titleName": "Test" }
            """);
        var original = Elf();
        new byte[] { 0xB0, 0xB1, 0xB2 }.CopyTo(original, 0x160);
        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), original);
        File.WriteAllBytes(Path.Combine(game, "libTest.sprx"), original);
        var archivePath = Path.Combine(_directory, "game.zar");
        byte[]? archiveBytes = null;
        if (fromArchive)
        {
            archiveBytes = ZArchiveTestWriter.Build(
                new[] { "eboot.bin", "libTest.sprx", "sce_sys/param.json" }
                    .Select(path => (path, File.ReadAllBytes(Path.Combine(game, path)))));
            File.WriteAllBytes(archivePath, archiveBytes);
            game = Assert.IsType<string>(ZArchiveCache.ExtractMetadata(archivePath, Path.Combine(_directory, "cache")));
            using var archive = ZArchive.Open(archivePath)!;
            foreach (var executable in new[] { "eboot.bin", "libTest.sprx" })
            {
                var path = Path.Combine(game, executable);
                using var source = new ZArchiveFileStream(archive, archive.LookUp(executable), path);
                App0Archive.WriteFile(source, path);
            }
        }
        var memory = new VirtualMemory();
        var loader = new SelfLoader(_directory);
        var modules = new ModuleManager();
        var elf = File.ReadAllBytes(Path.Combine(game, "eboot.bin"));
        var image = loader.Load(elf, memory, modules, new PhysicalFileSystem(), game, "eboot.bin");
        Assert.Equal("PPSA00001", image.TitleId);
        Assert.Equal("01.004.000", image.Version);
        var actual = new byte[3];
        Assert.True(memory.TryRead(image.EntryPoint - image.ElfHeader.EntryPoint + 0x10, actual));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, actual);
        var imageBase = image.EntryPoint - image.ElfHeader.EntryPoint;
        Assert.Equal(Convert.FromHexString("78563412"), Read(memory, imageBase + 0x20, 4));
        Assert.Equal(Convert.FromHexString("54006500730074000000"), Read(memory, imageBase + 0x30, 10));
        Assert.Equal(0xCC, Read(memory, imageBase + 0x61, 1)[0]);

        var module = loader.LoadAdditional(File.ReadAllBytes(Path.Combine(game, "libTest.sprx")),
            memory, modules, new PhysicalFileSystem(), game, "libTest.sprx");
        Assert.True(memory.TryRead(module.EntryPoint - module.ElfHeader.EntryPoint + 0x10, actual));
        Assert.Equal(new byte[] { 0xDD, 0xEE, 0xFF }, actual);
        Assert.True(memory.TryRead(image.EntryPoint - image.ElfHeader.EntryPoint + 0x10, actual));
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, actual);
        Assert.Equal(original, elf);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game, "eboot.bin")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(game, "libTest.sprx")));
        if (fromArchive) Assert.Equal(archiveBytes, File.ReadAllBytes(archivePath));
    }

    [Theory]
    [InlineData("01.004.000", "eboot.bin", false)]
    [InlineData("01.007.000", "eboot.bin", true)]
    [InlineData("01.004.000", "different.bin", true)]
    public void LoaderLeavesDisabledMismatchedAndOtherExecutablesUntouched(string version, string executable, bool enabled)
    {
        var patch = WritePatch();
        if (enabled) Repository.SetEnabled("PPSA00001", patch, true);
        var game = Path.Combine(_directory, "game");
        Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
        File.WriteAllText(Path.Combine(game, "sce_sys", "param.json"),
            $$"""{ "titleId": "PPSA00001", "contentVersion": "{{version}}" }""");
        var memory = new VirtualMemory();
        var image = new SelfLoader(_directory).Load(Elf(), memory, new ModuleManager(), new PhysicalFileSystem(), game, executable);
        var actual = new byte[3];
        Assert.True(memory.TryRead(image.EntryPoint - image.ElfHeader.EntryPoint + 0x10, actual));
        Assert.Equal(new byte[] { 0x90, 0x90, 0x90 }, actual);
    }

    private static byte[] Elf()
    {
        var bytes = new byte[0x180];
        bytes[0] = 0x7F;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        bytes[7] = 9;
        bytes[8] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 62);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 0x40);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 1);
        var segment = bytes.AsSpan(64, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(segment, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(segment[4..], 7);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[8..], 0x100);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[32..], 0x80);
        BinaryPrimitives.WriteUInt64LittleEndian(segment[40..], 0x1000);
        bytes.AsSpan(0x100).Fill(0x90);
        bytes[0x140] = 0xC3;
        return bytes;
    }
}

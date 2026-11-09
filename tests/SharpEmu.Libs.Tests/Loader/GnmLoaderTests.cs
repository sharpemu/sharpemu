// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Core;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.GnmMode.Loader;
using SharpEmu.GnmMode;
using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GnmLoaderTests : IDisposable
{
    public void Dispose() => GuestTlsTemplate.Reset();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadsUnmappedDynamicTablesImportsRelocationsExportsAndTls(bool self)
    {
        var bytes = CreateImage(self);
        var memory = new VirtualMemory();
        var image = new GnmSelfLoader().Load(bytes, memory);

        Assert.Equal(self, image.IsSelf);
        Assert.Equal(0, image.ElfHeader.AbiVersion);
        Assert.Equal(0x4000A0UL, image.EntryPoint);
        Assert.Equal(["libSceExample"], image.ImportedModuleNames);
        Assert.Equal("AAAAAAAAAAA", Assert.Single(image.ImportStubs).Value);
        Assert.Equal("AAAAAAAAAAA", Assert.Single(image.ImportedRelocations).Nid);
        Assert.Equal(0x400240UL, image.RuntimeSymbols["BBBBBBBBBBB"]);
        Assert.Equal(0x400260UL, image.InitFunctionEntryPoint);
        Assert.Equal(Assert.Single(image.ImportStubs).Key, Read64(memory, 0x400200));
        Assert.Equal(0x400240UL, Read64(memory, 0x400208));
        Assert.Equal(0x20UL, image.TlsMemorySize);
        Assert.Equal(0x12345678UL, Read64(memory, 0x400300));
    }

    [Fact]
    public void ZeroInitOffsetIsCallableButAMissingInitTagIsNot()
    {
        var bytes = CreateImage(false);
        Dynamic(bytes, 7, 12, 0);
        var image = new GnmSelfLoader().Load(bytes, new VirtualMemory());
        Assert.Equal(0x400000UL, image.InitFunctionEntryPoint);
        Assert.Contains(0x400000UL, image.InitializerFunctions);

        Dynamic(bytes, 7, 0, 0);
        image = new GnmSelfLoader().Load(bytes, new VirtualMemory());
        Assert.Equal(0UL, image.InitFunctionEntryPoint);
        Assert.Empty(image.InitializerFunctions);
    }

    [Fact]
    public void RelativeAddressesAreRebasedEvenWhenRawOffsetsAlsoPointIntoMappedMemory()
    {
        var data = CreateImage(false);
        Write64(data, 64 + 40, 0x500000);
        Write64(data, 0x10D0, 0x400200);
        Write64(data, 0x10E8, 0x400208);
        Write64(data, 0x1038, 0x400240);
        Write64(data, 0x1178, 0x400260);
        var memory = new VirtualMemory();
        var image = new GnmSelfLoader().Load(data, memory);

        Assert.Equal(0UL, Read64(memory, 0x400200));
        Assert.Equal(Assert.Single(image.ImportStubs).Key, Read64(memory, 0x800200));
        Assert.Equal(0x400240UL, Read64(memory, 0x800208));
        Assert.Equal(0x800240UL, image.RuntimeSymbols["BBBBBBBBBBB"]);
        Assert.Equal(0x800260UL, image.InitFunctionEntryPoint);
        Assert.Contains(0x800260UL, image.InitializerFunctions);
    }

    [Fact]
    public void UnresolvedWeakImportsRemainNull()
    {
        var data = CreateImage(false);
        data[0x101C] = 0x22;
        var memory = new VirtualMemory();
        var image = new GnmSelfLoader().Load(data, memory);
        Assert.Empty(image.ImportStubs);
        Assert.Equal(0UL, Read64(memory, 0x400200));
    }

    [Theory]
    [InlineData(1u, 0x1234UL)]
    [InlineData(10u, 0x1234UL)]
    [InlineData(11u, 0x1234UL)]
    public void DefinedAbsoluteSymbolsDoNotReceiveTheImageBase(uint type, ulong value)
    {
        var data = CreateImage(false);
        Write64(data, 0x10D8, (2UL << 32) | type);
        Write16(data, 0x1036, 0xFFF1);
        Write64(data, 0x1038, value);
        var memory = new VirtualMemory();
        var image = new GnmSelfLoader().Load(data, memory);
        Assert.Equal(value, image.RuntimeSymbols["BBBBBBBBBBB"]);
        Assert.Equal(value, Read64(memory, 0x400200));
    }

    [Theory]
    [InlineData(10u, 0x100000000UL)]
    [InlineData(11u, 0x80000000UL)]
    public void RejectsOverflowing32BitRelocations(uint type, ulong value)
    {
        var data = CreateImage(false);
        Write64(data, 0x10D8, (2UL << 32) | type);
        Write16(data, 0x1036, 0xFFF1);
        Write64(data, 0x1038, value);
        Assert.Throws<InvalidDataException>(() => new GnmSelfLoader().Load(data, new VirtualMemory()));
    }

    [Fact]
    public void KeepsEveryNeededModuleEntry()
    {
        var data = CreateImage(false);
        var extra = "libSceOther\0"u8.ToArray();
        var size = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x1138));
        extra.CopyTo(data, 0x1080 + (int)size);
        Write64(data, 0x1138, size + (ulong)extra.Length);
        Dynamic(data, 8, 0x6100000F, size);
        Write64(data, 64 + 2 * 56 + 32, 0xA0);
        var image = new GnmSelfLoader().Load(data, new VirtualMemory());
        Assert.Equal(["libSceExample", "libSceOther"], image.ImportedModuleNames);
    }

    [Theory]
    [InlineData(16u, 1UL)]
    [InlineData(17u, 4UL)]
    [InlineData(18u, 0xFFFFFFFFFFFFFFE4UL)]
    public void AppliesModuleAndStaticTlsRelocations(uint type, ulong expected)
    {
        var data = CreateImage(false);
        Write64(data, 0x10D8, (2UL << 32) | type);
        Write64(data, 0x1038, 4);
        var memory = new VirtualMemory();
        _ = new GnmSelfLoader().Load(data, memory);
        Assert.Equal(expected, Read64(memory, 0x400200));
    }

    [Fact]
    public void LoaderSelectionLeavesPs5Unchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharpemu-gnm-{Guid.NewGuid():N}.bin");
        try
        {
            var bytes = CreateImage(false);
            File.WriteAllBytes(path, bytes);
            Assert.IsType<GnmSelfLoader>(GnmRuntime.CreateLoader(path));
            bytes[8] = 2;
            File.WriteAllBytes(path, bytes);
            Assert.Null(GnmRuntime.CreateLoader(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ArchiveSelectionAndSfoMetadataUseTheEmbeddedExecutable(bool self, bool ps5)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-gnm-{Guid.NewGuid():N}");
        var source = Path.Combine(directory, "game");
        Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
        try
        {
            var bytes = CreateImage(self);
            if (ps5)
                bytes[(self ? 0x60 : 0) + 8] = 2;
            File.WriteAllBytes(Path.Combine(source, "eboot.bin"), bytes);
            File.WriteAllBytes(Path.Combine(source, "sce_sys", "param.sfo"), CreateSfo());
            var archive = Path.Combine(directory, "game.zar");
            ZArchiveWriter.Pack(source, archive);

            var info = GnmRuntime.ReadApplicationInfo(archive);
            if (ps5)
            {
                Assert.Null(GnmRuntime.CreateLoader(archive));
                Assert.Equal(default, info);
            }
            else
            {
                Assert.IsType<GnmSelfLoader>(GnmRuntime.CreateLoader(archive));
                Assert.Equal(("Bloodborne", "CUSA03173", "01.09"), info);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AdditionalModuleKeepsMainMappingsAndGetsItsOwnRelocations()
    {
        var memory = new VirtualMemory();
        var loader = new GnmSelfLoader();
        var main = loader.Load(CreateImage(true), memory);
        var module = loader.LoadAdditional(CreateImage(true), memory, new ModuleManager(), null, null);

        var mainBase = main.EntryPoint - main.ElfHeader.EntryPoint;
        var moduleBase = module.EntryPoint - module.ElfHeader.EntryPoint;
        Assert.NotEqual(mainBase, moduleBase);
        Assert.Equal(mainBase + 0x240, Read64(memory, mainBase + 0x208));
        Assert.Equal(moduleBase + 0x240, Read64(memory, moduleBase + 0x208));
        Assert.NotEqual(main.TlsModuleId, module.TlsModuleId);
        Assert.Equal("AAAAAAAAAAA", Assert.Single(module.ImportedRelocations).Nid);
    }

    [Theory]
    [InlineData(2UL)]
    [InlineData(8UL)]
    public void RejectsEncryptedOrCompressedPayloadInsteadOfReadingUnrelatedBytes(ulong flag)
    {
        var image = CreateImage(true);
        Write64(image, 0x40, 0x100800 | flag);
        Assert.Throws<NotSupportedException>(() => GnmLoader.Open(image));
    }

    [Fact]
    public void RejectsDynamicRangeWhichOnlyStartsInsideCarrier()
    {
        var image = CreateImage(true);
        Write64(image, 0x60 + 0x40 + 2 * 56 + 32, 0x400);
        Assert.Throws<InvalidDataException>(() => GnmLoader.Open(image));
    }

    [Fact]
    public void RejectsTablesOutsideDynlibEvenIfFileHasEnoughBytes()
    {
        var image = CreateImage(true);
        Write64(image, 0x1908, 0x3F0);
        var loader = GnmLoader.Open(image);
        Assert.Throws<InvalidDataException>(() => loader.TryReadDynamicTable(image, 0x61000039, 72, out _));
    }

    [Fact]
    public void RejectsPayloadShorterThanProgramHeader()
    {
        var image = CreateImage(true);
        Write64(image, 0x50, 0x100);
        Assert.Throws<InvalidDataException>(() => GnmLoader.Open(image));
    }

    [Fact]
    public void ReadsSfoMetadataThroughLoaderFileSystem()
    {
        var data = CreateSfo();
        var image = new GnmSelfLoader().Load(CreateImage(false), new VirtualMemory(),
            new SfoFileSystem(data), "app0");
        Assert.Equal("Bloodborne", image.Title);
        Assert.Equal("CUSA03173", image.TitleId);
        Assert.Equal("01.09", image.Version);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0x8000u, 0)]
    [InlineData(0x10000u, 5)]
    [InlineData(0x18000u, 2)]
    public void CpuModeUsesApplicationAttributesAndResetsForTheNextTitle(uint attributes, int expected)
    {
        var data = new byte[52];
        "\0PSF"u8.CopyTo(data);
        Write32(data, 4, 0x101);
        Write32(data, 8, 36);
        Write32(data, 12, 48);
        Write32(data, 16, 1);
        Write16(data, 22, 0x404);
        Write32(data, 24, 4);
        Write32(data, 28, 4);
        "ATTRIBUTE\0"u8.CopyTo(data.AsSpan(36));
        Write32(data, 48, attributes);
        var loader = new GnmSelfLoader();
        var memory = new VirtualMemory();
        _ = loader.Load(CreateImage(false), memory, new SfoFileSystem(data), "app0");
        var context = new CpuContext(memory, Generation.Gen4);
        Assert.Equal(expected, SharpEmu.GnmMode.Libraries.Kernel.GnmKernelExports.GetCpuMode(context));
        Assert.Equal((ulong)expected, context[CpuRegister.Rax]);
        _ = loader.Load(CreateImage(false), memory);
        Assert.Equal(0, SharpEmu.GnmMode.Libraries.Kernel.GnmKernelExports.GetCpuMode(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsAdjacentPs4MetadataForLogNames(bool self)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-gnm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sce_sys"));
        try
        {
            var eboot = Path.Combine(directory, "eboot.bin");
            File.WriteAllBytes(eboot, CreateImage(self));
            File.WriteAllBytes(Path.Combine(directory, "sce_sys", "param.sfo"), CreateSfo());

            var info = GnmRuntime.ReadApplicationInfo(eboot);
            Assert.Equal("Bloodborne", info.Title);
            Assert.Equal("CUSA03173", info.TitleId);
            Assert.Equal("01.09", info.Version);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsTruncatedSfoValues()
    {
        var data = CreateSfo();
        Assert.Throws<InvalidDataException>(() => GnmParamSfo.Read(data.AsSpan(0, data.Length - 1)));
    }

    [Fact]
    public void MalformedSfoDoesNotBreakMetadataDiscovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-gnm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "sce_sys"));
        try
        {
            var eboot = Path.Combine(directory, "eboot.bin");
            File.WriteAllBytes(eboot, CreateImage(false));
            File.WriteAllBytes(Path.Combine(directory, "sce_sys", "param.sfo"), new byte[20]);
            Assert.Equal(default, GnmRuntime.ReadApplicationInfo(eboot));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LaunchProbeRecognizesPs4SelfAndElfWithoutConsumingTheStream()
    {
        foreach (var self in new[] { false, true })
        {
            using var stream = new MemoryStream(CreateImage(self));
            stream.Position = 7;
            Assert.True(GnmLoader.IsGnmExecutable(stream));
            Assert.Equal(7, stream.Position);
        }
        var ps5 = CreateImage(false);
        ps5[8] = 2;
        using var ps5Stream = new MemoryStream(ps5);
        Assert.False(GnmLoader.IsGnmExecutable(ps5Stream));
        using var truncatedStream = new MemoryStream(new byte[12]);
        Assert.False(GnmLoader.IsGnmExecutable(truncatedStream));
    }

    [Fact]
    public void RejectsPs5ElfInGnmLoader()
    {
        var data = CreateImage(false);
        data[8] = 2;
        Assert.Throws<InvalidDataException>(() => GnmLoader.Open(data));
    }

    private sealed class SfoFileSystem(byte[] data) : IFileSystem
    {
        public bool Exists(string path) => path == "app0/sce_sys/param.sfo";
        public bool TryReadAllBytes(string path, out byte[] bytes)
        {
            bytes = Exists(path) ? data : [];
            return Exists(path);
        }
    }

    internal static byte[] CreateImage(bool self)
    {
        var elf = new byte[0x1400];
        "\u007fELF"u8.CopyTo(elf);
        elf[4] = 2;
        elf[5] = 1;
        elf[6] = 1;
        elf[7] = 9;
        Write16(elf, 16, 0xFE10);
        Write16(elf, 18, 62);
        Write32(elf, 20, 1);
        Write64(elf, 24, 0xA0);
        Write64(elf, 32, 64);
        Write16(elf, 52, 64);
        Write16(elf, 54, 56);
        Write16(elf, 56, 4);
        Header(elf, 0, 1, 0, 0, 0x400, 0x1000);
        Header(elf, 1, 0x61000000, 0x1000, 0, 0x400, 0);
        Header(elf, 2, 2, 0x1100, 0, 0x90, 0);
        Header(elf, 3, 7, 0x300, 0x300, 4, 0x20);
        Write32(elf, 0x300, 0x12345678);

        var strings = Encoding.ASCII.GetBytes("\0AAAAAAAAAAA#A#A\0BBBBBBBBBBB#A#A\0libSceExample\0");
        strings.CopyTo(elf, 0x1080);
        Write32(elf, 0x1018, 1);
        elf[0x101C] = 0x12;
        Write32(elf, 0x1030, 17);
        elf[0x1034] = 0x12;
        Write16(elf, 0x1036, 1);
        Write64(elf, 0x1038, 0x240);
        Write64(elf, 0x10D0, 0x200);
        Write64(elf, 0x10D8, (1UL << 32) | 7);
        Write64(elf, 0x10E8, 0x208);
        Write64(elf, 0x10F0, 8);
        Write64(elf, 0x10F8, 0x240);
        Dynamic(elf, 0, 0x61000039, 0);
        Dynamic(elf, 1, 0x6100003F, 72);
        Dynamic(elf, 2, 0x61000035, 0x80);
        Dynamic(elf, 3, 0x61000037, (ulong)strings.Length);
        Dynamic(elf, 4, 0x61000029, 0xD0);
        Dynamic(elf, 5, 0x6100002D, 48);
        Dynamic(elf, 6, 0x6100000F, 33);
        Dynamic(elf, 7, 12, 0x260);
        if (!self)
            return elf;

        var result = new byte[0x1C00];
        Write32(result, 0, 0x1D3D154F);
        result[5] = 1;
        result[6] = 1;
        result[7] = 0x12;
        Write64(result, 16, (ulong)result.Length);
        Write16(result, 24, 2);
        Write64(result, 0x20, 0x800);
        Write64(result, 0x28, 0x400);
        Write64(result, 0x30, 0x400);
        Write64(result, 0x38, 0x400);
        Write64(result, 0x40, 0x100800);
        Write64(result, 0x48, 0x1800);
        Write64(result, 0x50, 0x400);
        Write64(result, 0x58, 0x400);
        elf.AsSpan(0, 64 + 4 * 56).CopyTo(result.AsSpan(0x60));
        elf.AsSpan(0, 0x400).CopyTo(result.AsSpan(0x400));
        elf.AsSpan(0x1000, 0x400).CopyTo(result.AsSpan(0x1800));
        return result;
    }

    private static byte[] CreateSfo()
    {
        var keys = "TITLE\0TITLE_ID\0APP_VER\0"u8.ToArray();
        var values = new[] { "Bloodborne", "CUSA03173", "01.09" };
        var keyOffsets = new ushort[] { 0, 6, 15 };
        var valueOffset = 20 + 48 + keys.Length;
        var data = new byte[valueOffset + values.Sum(value => value.Length + 1)];
        "\0PSF"u8.CopyTo(data);
        Write32(data, 4, 0x101);
        Write32(data, 8, 68);
        Write32(data, 12, (uint)valueOffset);
        Write32(data, 16, 3);
        keys.CopyTo(data, 68);
        var offset = 0;
        for (var i = 0; i < 3; i++)
        {
            var at = 20 + i * 16;
            Write16(data, at, keyOffsets[i]);
            Write16(data, at + 2, 0x204);
            Write32(data, at + 4, (uint)(values[i].Length + 1));
            Write32(data, at + 8, (uint)(values[i].Length + 1));
            Write32(data, at + 12, (uint)offset);
            offset += Encoding.UTF8.GetBytes(values[i] + "\0", data.AsSpan(valueOffset + offset));
        }
        return data;
    }

    private static void Header(byte[] data, int index, uint type, ulong file, ulong address, ulong size, ulong memory)
    {
        var at = 64 + index * 56;
        Write32(data, at, type);
        Write32(data, at + 4, 6);
        Write64(data, at + 8, file);
        Write64(data, at + 16, address);
        Write64(data, at + 32, size);
        Write64(data, at + 40, memory);
        Write64(data, at + 48, 16);
    }

    private static void Dynamic(byte[] data, int index, ulong tag, ulong value)
    {
        Write64(data, 0x1100 + index * 16, tag);
        Write64(data, 0x1108 + index * 16, value);
    }

    private static void Write16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Write32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Write64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);

    private static ulong Read64(ICpuMemory memory, ulong address)
    {
        Span<byte> data = stackalloc byte[8];
        Assert.True(memory.TryRead(address, data));
        return BinaryPrimitives.ReadUInt64LittleEndian(data);
    }
}

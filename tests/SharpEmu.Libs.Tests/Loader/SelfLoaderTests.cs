// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class SelfLoaderTests
{
    private const uint Ps4SelfMagic = 0x4F153D1D;
    private const uint Ps5SelfMagic = 0x5414F5EE;
    private const int SelfHeaderSize = 0x20;
    private const int ElfHeaderSize = 0x40;

    [Theory]
    [InlineData(Ps4SelfMagic, (byte)0x00, 0x0000_0101u, (ushort)0x22)]
    [InlineData(Ps5SelfMagic, (byte)0x00, 0x0000_0101u, (ushort)0x22)]
    [InlineData(Ps5SelfMagic, (byte)0x10, 0x1000_0101u, (ushort)0x32)]
    public void Load_AcceptsSupportedSelfHeaderVariants(
        uint magic,
        byte version,
        uint keyType,
        ushort flags)
    {
        var imageData = CreateSelfImage(magic, version, keyType, flags);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.True(image.IsSelf);
        Assert.Equal(2, image.ElfHeader.AbiVersion);
        Assert.Empty(image.ProgramHeaders);
        Assert.Empty(image.MappedRegions);
    }

    [Theory]
    [InlineData(0x05, (byte)0x00)]
    [InlineData(0x06, (byte)0x02)]
    [InlineData(0x07, (byte)0x00)]
    public void Load_RejectsUnsupportedStructuralSelfHeaderValues(int offset, byte value)
    {
        var imageData = CreateSelfImage(Ps5SelfMagic, 0x10, 0x1000_0101, 0x32);
        imageData[offset] = value;

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    [Theory]
    [InlineData(0xDEADBEEF)]
    [InlineData(0x7F454C47)] // bare ELF magic read big-endian as a "SELF" candidate is not a SELF
    public void Load_RejectsUnrecognizedLeadingMagic(uint magic)
    {
        var imageData = new byte[SelfHeaderSize + ElfHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(imageData, magic);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    [Fact]
    public void Load_RejectsImageSmallerThanElfHeader()
    {
        // A few bytes short of ElfHeaderSize (0x40); ParseLayout guards this
        // before any magic dispatch, so the error is deterministic for both
        // SELF and ELF inputs.
        var imageData = new byte[ElfHeaderSize - 1];

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    [Fact]
    public void Load_RejectsTruncatedSelfHeader()
    {
        // SELF magic is present and recognized, but the image ends before the
        // SELF header + embedded ELF header can be read. ParseLayout computes
        // elfOffset = SelfHeaderSize + segments*SelfSegmentSize and then
        // EnsureRange must fail.
        var imageData = new byte[SelfHeaderSize + ElfHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(imageData, Ps5SelfMagic);
        imageData[0x05] = 0x01;
        imageData[0x06] = 0x01;
        imageData[0x07] = 0x12;
        var truncated = imageData.AsSpan(0, SelfHeaderSize + 0x10).ToArray();

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(truncated, new VirtualMemory()));
    }

    [Fact]
    public void Load_ParsesEmbeddedElfHeaderFromSelfContainer()
    {
        var imageData = CreateSelfImage(Ps5SelfMagic, 0x10, 0x1000_0101, 0x32);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.True(image.IsSelf);
        // The ELF header parsed out of the SELF container must be a valid x86-64
        // ELF64 little-endian image with the PS5 ABI marker that drives Gen5
        // selection in SharpEmuRuntime.
        Assert.True(image.ElfHeader.HasElfMagic);
        Assert.True(image.ElfHeader.Is64Bit);
        Assert.True(image.ElfHeader.IsLittleEndian);
        Assert.Equal(2, image.ElfHeader.AbiVersion);
        Assert.Equal(62, image.ElfHeader.Machine);
    }

    [Fact]
    public void Load_AcceptsBareDecryptedElf()
    {
        // A decrypted eboot that has already been stripped of its SELF wrapper
        // is accepted directly; IsSelf must be false and the ELF header is read
        // from offset 0.
        var imageData = new byte[ElfHeaderSize];
        WriteMinimalElfHeader(imageData);

        var image = new SelfLoader().Load(imageData, new VirtualMemory());

        Assert.False(image.IsSelf);
        Assert.True(image.ElfHeader.HasElfMagic);
        Assert.True(image.ElfHeader.Is64Bit);
        Assert.Equal(62, image.ElfHeader.Machine);
        Assert.Empty(image.ProgramHeaders);
        Assert.Empty(image.MappedRegions);
    }

    // Regression coverage for the loader address-overflow bug behind #235
    // ("Could not allocate main image at required base 0x0000000800000000"):
    // a PT_LOAD program header with a pathological VirtualAddress must be
    // rejected with a clear InvalidDataException instead of either crashing
    // with an unhandled OverflowException deep in VirtualMemory.Map, or
    // (worse) silently wrapping around and mapping the segment at the wrong
    // host address.
    [Fact]
    public void Load_RejectsProgramHeaderWhoseVirtualAddressPlusMemorySizeOverflows()
    {
        // VirtualAddress + MemorySize wraps past ulong.MaxValue.
        var imageData = CreateBareElfWithSingleLoadSegment(
            virtualAddress: ulong.MaxValue - 0x0FFF,
            memorySize: 0x2000);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    [Fact]
    public void Load_RejectsProgramHeaderWhoseVirtualAddressPlusImageBaseOverflows()
    {
        // MemorySize is small enough that VirtualAddress + MemorySize alone
        // does not overflow, but VirtualAddress is close enough to
        // ulong.MaxValue that adding the PS5 image base (AbiVersion == 2
        // selects 0x0000000800000000) wraps around.
        var imageData = CreateBareElfWithSingleLoadSegment(
            virtualAddress: ulong.MaxValue - 0x100,
            memorySize: 0x10);

        Assert.Throws<InvalidDataException>(() =>
            new SelfLoader().Load(imageData, new VirtualMemory()));
    }

    private static byte[] CreateBareElfWithSingleLoadSegment(ulong virtualAddress, ulong memorySize)
    {
        const int programHeaderSize = 0x38;
        var imageData = new byte[ElfHeaderSize + programHeaderSize];
        WriteMinimalElfHeader(imageData.AsSpan(0, ElfHeaderSize));
        BinaryPrimitives.WriteUInt16LittleEndian(imageData.AsSpan(0x38), 1); // e_phnum = 1

        var ph = imageData.AsSpan(ElfHeaderSize, programHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(ph, 1); // p_type = PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(ph[4..], 4); // p_flags = PF_R
        BinaryPrimitives.WriteUInt64LittleEndian(ph[8..], 0); // p_offset
        BinaryPrimitives.WriteUInt64LittleEndian(ph[16..], virtualAddress); // p_vaddr
        BinaryPrimitives.WriteUInt64LittleEndian(ph[24..], 0); // p_paddr
        BinaryPrimitives.WriteUInt64LittleEndian(ph[32..], 0); // p_filesz (no file-backed data)
        BinaryPrimitives.WriteUInt64LittleEndian(ph[40..], memorySize); // p_memsz
        BinaryPrimitives.WriteUInt64LittleEndian(ph[48..], 0); // p_align

        return imageData;
    }

    private static byte[] CreateSelfImage(uint magic, byte version, uint keyType, ushort flags)
    {
        var imageData = new byte[SelfHeaderSize + ElfHeaderSize];
        var selfHeader = imageData.AsSpan(0, SelfHeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(selfHeader, magic);
        selfHeader[0x04] = version;
        selfHeader[0x05] = 0x01;
        selfHeader[0x06] = 0x01;
        selfHeader[0x07] = 0x12;
        BinaryPrimitives.WriteUInt32LittleEndian(selfHeader[0x08..], keyType);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x0C..], SelfHeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(selfHeader[0x10..], (ulong)imageData.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x18..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(selfHeader[0x1A..], flags);

        WriteMinimalElfHeader(imageData.AsSpan(SelfHeaderSize, ElfHeaderSize));
        return imageData;
    }

    private static void WriteMinimalElfHeader(Span<byte> header)
    {
        header.Clear();
        header[0x00] = 0x7F;
        header[0x01] = (byte)'E';
        header[0x02] = (byte)'L';
        header[0x03] = (byte)'F';
        header[0x04] = 2;
        header[0x05] = 1;
        header[0x06] = 1;
        header[0x07] = 9;
        header[0x08] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x10..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x12..], 62);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x14..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x34..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x36..], 0x38);
    }
}

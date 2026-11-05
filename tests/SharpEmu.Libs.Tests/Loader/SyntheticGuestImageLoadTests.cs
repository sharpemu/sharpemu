// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

/// <summary>
/// End-to-end coverage of loading a complete guest image, as opposed to the header-parsing cases
/// in <see cref="SelfLoaderTests"/>, which all assert <c>ProgramHeaders</c> and
/// <c>MappedRegions</c> are empty and so never exercise the mapping path at all.
///
/// This is the regression class #388 describes: #157 added PS5 SELF support and #216 replaced it
/// with PS4-only recognition the next day, while CI stayed green throughout, because nothing
/// asserted that a PS5 image actually maps where a PS5 image belongs. These images are built here
/// from the ELF definition - no game data, no dumps.
/// </summary>
public sealed class SyntheticGuestImageLoadTests
{
    private const int ElfHeaderSize = 0x40;
    private const int ProgramHeaderSize = 0x38;
    private const uint PtLoad = 1;
    private const ulong Ps5MainImageBase = 0x0000_0008_0000_0000;
    private const ulong SegmentVirtualAddress = 0x1000;

    // xor eax, eax ; ret - a guest entry that returns to its caller immediately.
    private static readonly byte[] ReturnZeroPayload = [0x31, 0xC0, 0xC3];

    [Fact]
    public void BareElf_MapsItsLoadSegmentAtThePs5ImageBase()
    {
        var image = new SelfLoader().Load(BuildBareElf(ReturnZeroPayload), new VirtualMemory());

        var header = Assert.Single(image.ProgramHeaders);
        Assert.Equal(PtLoad, (uint)header.Type);

        var region = Assert.Single(image.MappedRegions);
        Assert.Equal(Ps5MainImageBase + SegmentVirtualAddress, region.VirtualAddress);
        Assert.Equal((ulong)ReturnZeroPayload.Length, region.MemorySize);
    }

    /// <summary>
    /// The entry point the backend is handed has to be the relocated one. A loader that returned
    /// the raw <c>e_entry</c> would send execution to 0x1000, which is unmapped.
    /// </summary>
    [Fact]
    public void BareElf_ResolvesTheEntryPointRelativeToTheImageBase()
    {
        var image = new SelfLoader().Load(BuildBareElf(ReturnZeroPayload), new VirtualMemory());

        Assert.Equal(SegmentVirtualAddress, image.ElfHeader.EntryPoint);
        Assert.Equal(Ps5MainImageBase + SegmentVirtualAddress, image.EntryPoint);
    }

    [Fact]
    public void BareElf_CopiesTheSegmentContentsIntoGuestMemory()
    {
        var virtualMemory = new VirtualMemory();

        var image = new SelfLoader().Load(BuildBareElf(ReturnZeroPayload), virtualMemory);

        var loaded = new byte[ReturnZeroPayload.Length];
        Assert.True(virtualMemory.TryRead(image.EntryPoint, loaded));
        Assert.Equal(ReturnZeroPayload, loaded);
    }

    /// <summary>
    /// A zero-filled tail (p_memsz &gt; p_filesz) is how .bss reaches the guest, and it has to be
    /// mapped and readable rather than left short.
    /// </summary>
    [Fact]
    public void BareElf_ZeroFillsTheSegmentTailBeyondTheFileContents()
    {
        const int zeroFill = 0x40;
        var virtualMemory = new VirtualMemory();

        var image = new SelfLoader().Load(
            BuildBareElf(ReturnZeroPayload, extraZeroFill: zeroFill),
            virtualMemory);

        var region = Assert.Single(image.MappedRegions);
        Assert.Equal((ulong)(ReturnZeroPayload.Length + zeroFill), region.MemorySize);
        Assert.Equal((ulong)ReturnZeroPayload.Length, region.FileSize);

        var tail = new byte[zeroFill];
        Assert.True(virtualMemory.TryRead(image.EntryPoint + (ulong)ReturnZeroPayload.Length, tail));
        Assert.All(tail, b => Assert.Equal(0, b));
    }

    [Fact]
    public void BareElf_IsNotReportedAsASelfContainer()
    {
        var image = new SelfLoader().Load(BuildBareElf(ReturnZeroPayload), new VirtualMemory());

        Assert.False(image.IsSelf);
        Assert.True(image.ElfHeader.HasElfMagic);
        Assert.True(image.ElfHeader.Is64Bit);
        Assert.True(image.ElfHeader.IsLittleEndian);
    }

    /// <summary>
    /// Builds a decrypted PS5-style ELF with one PT_LOAD segment. The identification bytes are the
    /// ones the loader's own acceptance tests use for a bare image (OSABI 9 / ABI version 2).
    /// </summary>
    private static byte[] BuildBareElf(ReadOnlySpan<byte> payload, int extraZeroFill = 0)
    {
        var headerBytes = ElfHeaderSize + ProgramHeaderSize;
        // Keep p_offset congruent to p_vaddr modulo the page size, as a loader expects.
        var padding = (int)((SegmentVirtualAddress - (ulong)headerBytes) % 0x1000);
        var fileOffset = headerBytes + padding;
        var image = new byte[fileOffset + payload.Length];

        var elf = image.AsSpan(0, ElfHeaderSize);
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2; // ELFCLASS64
        elf[5] = 1; // ELFDATA2LSB
        elf[6] = 1; // EV_CURRENT
        elf[7] = 9; // ELFOSABI_FREEBSD
        elf[8] = 2; // ABI version
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x10..], 3);  // ET_DYN
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x12..], 62); // EM_X86_64
        BinaryPrimitives.WriteUInt32LittleEndian(elf[0x14..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(elf[0x18..], SegmentVirtualAddress); // e_entry
        BinaryPrimitives.WriteUInt64LittleEndian(elf[0x20..], ElfHeaderSize);         // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x34..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x36..], ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(elf[0x38..], 1); // e_phnum

        var programHeader = image.AsSpan(ElfHeaderSize, ProgramHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader, PtLoad);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[0x04..], 7); // R+W+X
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x08..], (ulong)fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x10..], SegmentVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x18..], SegmentVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x20..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(
            programHeader[0x28..],
            (ulong)(payload.Length + extraZeroFill));
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x30..], 0x1000);

        payload.CopyTo(image.AsSpan(fileOffset));
        return image;
    }
}

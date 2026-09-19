// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Tests.Loader;

/// <summary>
/// Builds a minimal synthetic PS5 SELF segment-table fixture from public ELF and SELF layouts.
/// The fixture has a coherent minimal header, one data entry, and an embedded ELF.
/// <c>SelfLoader</c> consumes only the recognition and segment-routing subset. The output
/// omits extended and signing metadata, so it is not a format-complete or console-installable fSELF.
/// </summary>
internal static class SyntheticGen5SelfImageBuilder
{
    public const ulong ImageBase = 0x0000_0008_0000_0000UL;
    public const ulong PayloadVirtualAddress = 0x1000;
    public const int ElfPayloadFileOffset = 0x1000;
    public const ulong DefaultMemorySize = 0x1000;

    private const uint Ps5SelfMagic = 0x5414F5EE;
    private const int SelfHeaderSize = 0x20;
    private const int SelfSegmentSize = 0x20;
    private const int SelfEntryCount = 1;
    private const int ElfHeaderSize = 0x40;
    private const int ProgramHeaderSize = 0x38;
    private const int ElfOffset = SelfHeaderSize + (SelfEntryCount * SelfSegmentSize);
    private const int SelfEmbeddedHeadersEnd = ElfOffset + ElfHeaderSize + ProgramHeaderSize;

    public const int SelfPayloadFileOffset = (SelfEmbeddedHeadersEnd + 0xF) & ~0xF;

    // Public SELF entry bitfields: has_blocks, a 16 KiB block-size exponent, and
    // program-header index zero. SelfLoader currently exposes has_blocks as IsBlocked.
    private const int SelfBlockSizeFieldShift = 12;
    private const int SelfBlockSizeExponent = 2;
    private const ulong SelfHasBlocksEntryFlag = 1UL << 11;
    private const ulong SelfDataEntryProperties =
        SelfHasBlocksEntryFlag |
        ((ulong)SelfBlockSizeExponent << SelfBlockSizeFieldShift);

    public static byte[] BuildMinimalSelfSegmentTableImage(
        ReadOnlySpan<byte> payload,
        ulong memorySize = DefaultMemorySize)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException("Synthetic payload must not be empty.", nameof(payload));
        }

        if (memorySize < (ulong)payload.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memorySize),
                "Memory size must be at least as large as the payload.");
        }

        var imageLength = checked((SelfPayloadFileOffset + payload.Length + 0xF) & ~0xF);
        var image = new byte[imageLength];

        WriteSelfContainer(image, payload.Length);
        WriteElfHeader(image.AsSpan(ElfOffset, ElfHeaderSize));
        WriteProgramHeader(
            image.AsSpan(ElfOffset + ElfHeaderSize, ProgramHeaderSize),
            memorySize,
            payload.Length);
        payload.CopyTo(image.AsSpan(SelfPayloadFileOffset));
        return image;
    }

    private static void WriteSelfContainer(Span<byte> image, int payloadLength)
    {
        var header = image[..SelfHeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, Ps5SelfMagic);
        header[0x04] = 0x00;
        header[0x05] = 0x01;
        header[0x06] = 0x01;
        header[0x07] = 0x12;
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x08..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x0C..], SelfPayloadFileOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x0E..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], (ulong)image.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x18..], SelfEntryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1A..], 0);

        // The physical data offset intentionally differs from ELF p_offset so
        // the test fails if SELF segment-table resolution regresses to fallback.
        var dataEntry = image.Slice(SelfHeaderSize, SelfSegmentSize);
        BinaryPrimitives.WriteUInt64LittleEndian(dataEntry, SelfDataEntryProperties);
        BinaryPrimitives.WriteUInt64LittleEndian(dataEntry[0x08..], SelfPayloadFileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(dataEntry[0x10..], (ulong)payloadLength);
        BinaryPrimitives.WriteUInt64LittleEndian(dataEntry[0x18..], (ulong)payloadLength);
    }

    private static void WriteElfHeader(Span<byte> header)
    {
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
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], PayloadVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x34..], ElfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x36..], ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x38..], 1);
    }

    private static void WriteProgramHeader(Span<byte> header, ulong memorySize, int payloadLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x08..], ElfPayloadFileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x10..], PayloadVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x18..], PayloadVirtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x20..], (ulong)payloadLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x28..], memorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[0x30..], 0x1000);
    }
}

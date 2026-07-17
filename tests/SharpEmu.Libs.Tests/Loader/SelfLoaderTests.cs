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

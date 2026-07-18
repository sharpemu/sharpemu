// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRegisterDefaultsTests
{
    private const ulong BlobAddress = 0x12_3400_0000;

    [Fact]
    public void Version13_UsesVersion11Fallback()
    {
        Assert.Equal(11u, AgcExports.NormalizeRegisterDefaultsVersion(13));
        Assert.Equal(11u, AgcExports.NormalizeRegisterDefaultsVersion(uint.MaxValue));
        Assert.Equal(10u, AgcExports.NormalizeRegisterDefaultsVersion(10));
    }

    [Fact]
    public void PublicV11Blob_ContainsCompleteTablesAndTypeMetadata()
    {
        Assert.Equal(84, AgcRegisterDefaultsV11.Public.Table0.PointerOffsets.Length);
        Assert.Equal(32, AgcRegisterDefaultsV11.Public.Table1.PointerOffsets.Length);
        Assert.Equal(21, AgcRegisterDefaultsV11.Public.Table2.PointerOffsets.Length);
        Assert.Empty(AgcRegisterDefaultsV11.Public.Table3.PointerOffsets);

        var blob = BuildBlob(AgcRegisterDefaultsV11.Public);

        var table0 = ReadUInt64(blob, 0x00);
        var table1 = ReadUInt64(blob, 0x08);
        var table2 = ReadUInt64(blob, 0x10);
        Assert.InRange(table0, BlobAddress, BlobAddress + (ulong)blob.Length - 1);
        Assert.InRange(table1, BlobAddress, BlobAddress + (ulong)blob.Length - 1);
        Assert.InRange(table2, BlobAddress, BlobAddress + (ulong)blob.Length - 1);
        Assert.Equal(0UL, ReadUInt64(blob, 0x18));

        Assert.Equal(488u, ReadUInt32(blob, 0x20));
        Assert.Equal(162u, ReadUInt32(blob, 0x24));
        Assert.Equal(56u, ReadUInt32(blob, 0x28));
        Assert.Equal(0u, ReadUInt32(blob, 0x2C));
        Assert.Equal(137u, ReadUInt32(blob, 0x38));

        var types = ReadUInt64(blob, 0x30);
        AssertRegister(blob, ReadPointer(blob, table1, 0), 0, 0x0212, 0);
        AssertRegister(blob, ReadPointer(blob, table2, 0), 0, 0x041F, 0);
        AssertRegister(blob, ReadPointer(blob, table0, 14), 0, 0x0203, 0x00000000);

        var screenScissor = ReadPointer(blob, table0, 79);
        AssertRegister(blob, screenScissor, 0, 0x000C, 0x00000000);
        AssertRegister(blob, screenScissor, 1, 0x000D, 0x40004000);

        var viewport = ReadPointer(blob, table0, 82);
        AssertRegister(blob, viewport, 0, 0x010F, 0x3F800000);
        AssertRegister(blob, viewport, 1, 0x0111, 0x3F800000);
        AssertRegister(blob, viewport, 2, 0x0113, 0x3F800000);
        AssertRegister(blob, viewport, 6, 0x0094, 0x80000000);
        AssertRegister(blob, viewport, 7, 0x0095, 0x40004000);
        AssertRegister(blob, viewport, 8, 0x00B4, 0x00000000);
        AssertRegister(blob, viewport, 9, 0x00B5, 0x00000000);

        AssertTypeRecord(blob, types, 0, 0xE24F806D, 0x00040400, 0);
        AssertTypeRecord(blob, types, 79, 0x0B177B43, 0x0004093C, 0);
        AssertTypeRecord(blob, types, 82, 0x7690AF6F, 0x00402948, 0);
        AssertTypeRecord(blob, types, 136, 0x036AC8A6, 0x000C0452, 0);
    }

    [Fact]
    public void InternalV11Blob_PreservesFourthTableAndSparsePointerOffsets()
    {
        Assert.Equal(8, AgcRegisterDefaultsV11.Internal.Table0.PointerOffsets.Length);
        Assert.Equal(12, AgcRegisterDefaultsV11.Internal.Table1.PointerOffsets.Length);
        Assert.Single(AgcRegisterDefaultsV11.Internal.Table2.PointerOffsets);
        Assert.Equal(3, AgcRegisterDefaultsV11.Internal.Table3.PointerOffsets.Length);

        var blob = BuildBlob(AgcRegisterDefaultsV11.Internal);

        for (var tableIndex = 0; tableIndex < 4; tableIndex++)
        {
            var pointer = ReadUInt64(blob, tableIndex * sizeof(ulong));
            Assert.InRange(pointer, BlobAddress, BlobAddress + (ulong)blob.Length - 1);
        }

        Assert.Equal(8u, ReadUInt32(blob, 0x20));
        Assert.Equal(12u, ReadUInt32(blob, 0x24));
        Assert.Equal(1u, ReadUInt32(blob, 0x28));
        Assert.Equal(6u, ReadUInt32(blob, 0x2C));
        Assert.Equal(24u, ReadUInt32(blob, 0x38));

        var table3 = ReadUInt64(blob, 0x18);
        AssertRegister(blob, ReadPointer(blob, table3, 0), 0, 0x026C, 0);
        AssertRegister(blob, ReadPointer(blob, table3, 1), 0, 0x0094, 0);
        AssertRegister(blob, ReadPointer(blob, table3, 2), 0, 0x026E, 0);

        var types = ReadUInt64(blob, 0x30);
        AssertTypeRecord(blob, types, 0, 0x8FB4EDB5, 0x00040400, 0);
        AssertTypeRecord(blob, types, 23, 0x929FD95D, 0x00040C0B, 0);
    }

    private static byte[] BuildBlob(AgcRegisterDefaultsData defaults)
    {
        Assert.True(AgcExports.TryBuildRegisterDefaultsBlob(defaults, BlobAddress, out var blob));
        return blob;
    }

    private static ulong ReadPointer(byte[] blob, ulong table, int pointerIndex) =>
        ReadUInt64(blob, ToOffset(table) + (pointerIndex * sizeof(ulong)));

    private static void AssertRegister(
        byte[] blob,
        ulong registerAddress,
        int registerIndex,
        uint expectedOffset,
        uint expectedValue)
    {
        var offset = ToOffset(registerAddress) + (registerIndex * 2 * sizeof(uint));
        Assert.Equal(expectedOffset, ReadUInt32(blob, offset));
        Assert.Equal(expectedValue, ReadUInt32(blob, offset + sizeof(uint)));
    }

    private static void AssertTypeRecord(
        byte[] blob,
        ulong typesAddress,
        int typeIndex,
        uint expectedType,
        uint expectedMetadata,
        uint expectedReserved)
    {
        var offset = ToOffset(typesAddress) + (typeIndex * 3 * sizeof(uint));
        Assert.Equal(expectedType, ReadUInt32(blob, offset));
        Assert.Equal(expectedMetadata, ReadUInt32(blob, offset + sizeof(uint)));
        Assert.Equal(expectedReserved, ReadUInt32(blob, offset + (2 * sizeof(uint))));
    }

    private static int ToOffset(ulong address) => checked((int)(address - BlobAddress));

    private static uint ReadUInt32(byte[] blob, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset, sizeof(uint)));

    private static ulong ReadUInt64(byte[] blob, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(offset, sizeof(ulong)));
}

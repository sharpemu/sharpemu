// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // The open-source version 9 data is a small change from version 8.
    // Apply only the changed registers, pointers, and type entries.
    private static CompactRegisterDefaults PublicRegisterDefaultsVersion9 =>
        CreatePublicRegisterDefaultsVersion9();

    private static CompactRegisterDefaults InternalRegisterDefaultsVersion9 =>
        CreateInternalRegisterDefaultsVersion9();

    private static CompactRegisterDefaults CreatePublicRegisterDefaultsVersion9()
    {
        var table0 = (uint[])PublicRegisterDefaultsVersion8.Table0Registers.Clone();
        SetRegisterValue(table0, 0x0109u, 0x00000010u, "version 9 public table 0");

        var sourceTable1 = PublicRegisterDefaultsVersion8.Table1Registers;
        const int insertionIndex = 17 * 2;
        var table1 = new uint[sourceTable1.Length + 2];
        Array.Copy(sourceTable1, 0, table1, 0, insertionIndex);
        table1[insertionIndex] = 0x0081u;
        table1[insertionIndex + 1] = 0x00000000u;
        Array.Copy(
            sourceTable1,
            insertionIndex,
            table1,
            insertionIndex + 2,
            sourceTable1.Length - insertionIndex);

        ushort[] table1Pointers =
        [
            0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006, 0x0007,
            0x0008, 0x0009, 0x000A, 0x000B, 0x000F, 0x0013, 0x0015, 0x0017,
            0x0019, 0x001B, 0x001D, 0x001F, 0x0020, 0x0024, 0x0028, 0x002C,
            0x002E, 0x0030, 0x0040, 0x0060, 0x0080,
        ];

        var types = (uint[])PublicRegisterDefaultsVersion8.Types.Clone();
        types[271] = 0x00041031u;

        return PublicRegisterDefaultsVersion8 with
        {
            Table0Registers = table0,
            Table1Registers = table1,
            Table1PointerOffsets = table1Pointers,
            Types = types,
        };
    }

    private static CompactRegisterDefaults CreateInternalRegisterDefaultsVersion9()
    {
        var sourceTable1 = InternalRegisterDefaultsVersion8.Table1Registers;
        const int removalIndex = 12 * 2;
        if (sourceTable1[removalIndex] != 0x0081u)
        {
            throw new InvalidOperationException("Version 8 internal table 1 has an unexpected layout.");
        }

        var table1 = new uint[sourceTable1.Length - 2];
        Array.Copy(sourceTable1, 0, table1, 0, removalIndex);
        Array.Copy(
            sourceTable1,
            removalIndex + 2,
            table1,
            removalIndex,
            sourceTable1.Length - removalIndex - 2);

        ushort[] table1Pointers =
        [
            0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006,
            0x0007, 0x0008, 0x0009, 0x000A, 0x000B, 0x000C, 0x000D,
        ];
        uint[] table2 = [0x0260u, 0x00000000u];
        ushort[] table2Pointers = [0x0000];

        var types = (uint[])InternalRegisterDefaultsVersion8.Types.Clone();
        types[48] = 0xC67EFACFu;
        types[49] = 0x00040431u;
        types[51] = 0xD9E6D9F7u;
        types[52] = 0x00040435u;
        types[54] = 0x60289246u;
        types[55] = 0x00040402u;

        return InternalRegisterDefaultsVersion8 with
        {
            Table1Registers = table1,
            Table1PointerOffsets = table1Pointers,
            Table2Registers = table2,
            Table2PointerOffsets = table2Pointers,
            Types = types,
        };
    }

    private static void SetRegisterValue(
        uint[] registers,
        uint offset,
        uint value,
        string tableName)
    {
        for (var index = 0; index < registers.Length; index += 2)
        {
            if (registers[index] != offset)
            {
                continue;
            }

            registers[index + 1] = value;
            return;
        }

        throw new InvalidOperationException($"{tableName} does not contain register 0x{offset:X4}.");
    }
}

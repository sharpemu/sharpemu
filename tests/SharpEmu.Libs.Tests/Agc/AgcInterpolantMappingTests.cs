// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcInterpolantMappingTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong RegistersAddress = BaseAddress + 0x100;
    private const ulong GeometryShaderAddress = BaseAddress + 0x400;
    private const ulong PixelShaderAddress = BaseAddress + 0x500;
    private const ulong GeometrySemanticsAddress = BaseAddress + 0x800;
    private const ulong PixelSemanticsAddress = BaseAddress + 0x900;
    private const uint SpiPsInputCntl0 = 0x191;

    [Fact]
    public void CreateInterpolantMapping2_WritesIdentityMappingWithoutPixelInputs()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = RegistersAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.CreateInterpolantMapping2(ctx));

        for (uint index = 0; index < 32; index++)
        {
            Assert.Equal(SpiPsInputCntl0 + index, ReadUInt32(memory, RegistersAddress + (index * 8)));
            Assert.Equal(index, ReadUInt32(memory, RegistersAddress + (index * 8) + 4));
        }
    }

    [Fact]
    public void CreateInterpolantMapping2_PacksNativeSemanticModes()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        WriteUInt64(memory, GeometryShaderAddress + 0x38, GeometrySemanticsAddress);
        WriteUInt16(memory, GeometryShaderAddress + 0x56, 3);
        WriteUInt32(memory, GeometrySemanticsAddress + 0, Semantic(7, hardwareMapping: 5));
        WriteUInt32(memory, GeometrySemanticsAddress + 4, Semantic(9, hardwareMapping: 12));
        WriteUInt32(memory, GeometrySemanticsAddress + 8, Semantic(10, hardwareMapping: 4, isF16: 1));

        WriteUInt64(memory, PixelShaderAddress + 0x30, PixelSemanticsAddress);
        WriteUInt32(memory, PixelShaderAddress + 0x50, 6);
        WriteUInt32(memory, PixelSemanticsAddress + 0, Semantic(7));
        WriteUInt32(memory, PixelSemanticsAddress + 4, Semantic(8, defaultValue: 2));
        WriteUInt32(
            memory,
            PixelSemanticsAddress + 8,
            Semantic(9, flatShaded: true, custom: true, defaultValue: 1));
        WriteUInt32(
            memory,
            PixelSemanticsAddress + 12,
            Semantic(10, isF16: 1, defaultValue: 3, defaultValueHigh: 2));
        WriteUInt32(
            memory,
            PixelSemanticsAddress + 16,
            Semantic(10, isF16: 2, defaultValueHigh: 3));
        WriteUInt32(
            memory,
            PixelSemanticsAddress + 20,
            Semantic(11, isF16: 1, defaultValue: 2, defaultValueHigh: 1));

        ctx[CpuRegister.Rdi] = RegistersAddress;
        ctx[CpuRegister.Rsi] = GeometryShaderAddress;
        ctx[CpuRegister.Rdx] = PixelShaderAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.CreateInterpolantMapping2(ctx));

        uint[] expectedValues =
        [
            0x0000_0005,
            0x0000_0220,
            0x0000_052C,
            0x0158_0304,
            0x0268_0324,
            0x0138_0220,
        ];
        for (uint index = 0; index < (uint)expectedValues.Length; index++)
        {
            Assert.Equal(SpiPsInputCntl0 + index, ReadUInt32(memory, RegistersAddress + (index * 8)));
            Assert.Equal(expectedValues[index], ReadUInt32(memory, RegistersAddress + (index * 8) + 4));
        }

        Assert.Equal(SpiPsInputCntl0 + 6, ReadUInt32(memory, RegistersAddress + 48));
        Assert.Equal(6u, ReadUInt32(memory, RegistersAddress + 52));
    }

    private static uint Semantic(
        uint semantic,
        uint hardwareMapping = 0,
        uint isF16 = 0,
        bool flatShaded = false,
        bool custom = false,
        uint defaultValue = 0,
        uint defaultValueHigh = 0)
    {
        return semantic |
               (hardwareMapping << 8) |
               (isF16 << 20) |
               (flatShaded ? 1u << 22 : 0) |
               (custom ? 1u << 24 : 0) |
               (defaultValue << 28) |
               (defaultValueHigh << 30);
    }

    private static void WriteUInt16(FakeCpuMemory memory, ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}

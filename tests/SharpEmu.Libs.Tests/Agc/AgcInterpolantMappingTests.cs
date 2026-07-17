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
    private const ulong PixelShaderAddress = BaseAddress + 0x600;
    private const ulong OutputSemanticsAddress = BaseAddress + 0x800;
    private const ulong InputSemanticsAddress = BaseAddress + 0x900;

    [Fact]
    public void CreateInterpolantMappingMatchesSemanticIdsAndCustomFlags()
    {
        var memory = CreateMemory(
            geometrySemantics:
            [
                0x0000_0000u,
                0x0000_0101u,
                0x0000_0202u,
                0x0000_0303u,
            ],
            pixelSemantics:
            [
                0x0000_0000u,
                0x0000_0002u,
                0x4100_0003u,
            ]);

        var result = Invoke(memory);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        AssertRegister(memory, 0, 0x000u);
        AssertRegister(memory, 1, 0x002u);
        AssertRegister(memory, 2, 0x423u);
        AssertRegister(memory, 3, 0x003u);
        AssertRegister(memory, 31, 0x01Fu);
    }

    [Fact]
    public void CreateInterpolantMappingPreservesF16Mode()
    {
        var memory = CreateMemory(
            geometrySemantics: [0x0010_0705u],
            pixelSemantics: [0x0010_0005u]);

        var result = Invoke(memory);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        AssertRegister(memory, 0, 0x0118_0007u);
    }

    private static FakeCpuMemory CreateMemory(
        IReadOnlyList<uint> geometrySemantics,
        IReadOnlyList<uint> pixelSemantics)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        WriteUInt64(
            memory,
            GeometryShaderAddress + 0x38,
            OutputSemanticsAddress);
        WriteUInt32(
            memory,
            GeometryShaderAddress + 0x56,
            (uint)geometrySemantics.Count);
        WriteUInt64(
            memory,
            PixelShaderAddress + 0x30,
            InputSemanticsAddress);
        WriteUInt32(
            memory,
            PixelShaderAddress + 0x50,
            (uint)pixelSemantics.Count);

        for (var index = 0; index < geometrySemantics.Count; index++)
        {
            WriteUInt32(
                memory,
                OutputSemanticsAddress + (ulong)(index * sizeof(uint)),
                geometrySemantics[index]);
        }

        for (var index = 0; index < pixelSemantics.Count; index++)
        {
            WriteUInt32(
                memory,
                InputSemanticsAddress + (ulong)(index * sizeof(uint)),
                pixelSemantics[index]);
        }

        return memory;
    }

    private static int Invoke(FakeCpuMemory memory)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = RegistersAddress;
        ctx[CpuRegister.Rsi] = GeometryShaderAddress;
        ctx[CpuRegister.Rdx] = PixelShaderAddress;
        return AgcExports.CreateInterpolantMapping(ctx);
    }

    private static void AssertRegister(
        FakeCpuMemory memory,
        uint index,
        uint expectedValue)
    {
        var address = RegistersAddress + (index * 8);
        Assert.Equal(0x191u + index, ReadUInt32(memory, address));
        Assert.Equal(expectedValue, ReadUInt32(memory, address + sizeof(uint)));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static void WriteUInt32(
        FakeCpuMemory memory,
        ulong address,
        uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(
        FakeCpuMemory memory,
        ulong address,
        ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}

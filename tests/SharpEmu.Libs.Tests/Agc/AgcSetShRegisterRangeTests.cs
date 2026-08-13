// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSetShRegisterRangeTests
{
    private const ulong BaseAddress = 0x2_3000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong ValuesAddress = BaseAddress + 0x800;

    [Theory]
    [InlineData(1u, 20u)]
    [InlineData(2u, 24u)]
    [InlineData(64u, 272u)]
    [InlineData(0x3FFFu, 65_548u)]
    public void GetSize_ReturnsEmitterByteCount(uint valueCount, uint expectedSize)
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = valueCount;

        var result = AgcExports.CbSetShRegisterRangeDirectGetSize(ctx);

        Assert.Equal((int)expectedSize, result);
        Assert.Equal(expectedSize, ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x4000u)]
    [InlineData(uint.MaxValue)]
    public void GetSize_ReturnsZeroForUnsupportedCount(uint valueCount)
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = valueCount;

        var result = AgcExports.CbSetShRegisterRangeDirectGetSize(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetSize_EqualsBytesConsumedByEmitter()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        WriteUInt32(memory, ValuesAddress, 0x1122_3344);
        WriteUInt32(memory, ValuesAddress + 4, 0x5566_7788);

        ctx[CpuRegister.Rdi] = 2;
        var size = AgcExports.CbSetShRegisterRangeDirectGetSize(ctx);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0x40;
        ctx[CpuRegister.Rdx] = ValuesAddress;
        ctx[CpuRegister.Rcx] = 2;
        var result = AgcExports.CbSetShRegisterRangeDirect(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(PacketAddress + 8, ctx[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + (ulong)size, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, PacketAddress + 20));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

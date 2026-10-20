// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSetNopTests
{
    private const ulong BaseAddress = 0x2_4000_0000;
    private const ulong PacketAddress = BaseAddress + 0x400;

    [Fact]
    public void SetNop_TurnsAType3PacketIntoANop()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, PacketAddress, 0xC001_2D00u);
        WriteUInt32(memory, PacketAddress + 4, 0x1234_5678);
        WriteUInt32(memory, PacketAddress + 8, 0x9ABC_DEF0);

        ctx[CpuRegister.Rdi] = PacketAddress;
        var result = AgcExports.SetNop(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0xC001_1000u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x1234_5678u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0x9ABC_DEF0u, ReadUInt32(memory, PacketAddress + 8));
    }

    [Fact]
    public void SetNop_TruncatesAnExecutableMarkerPayload()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, PacketAddress, 0xC001_2D00u);
        WriteUInt32(memory, PacketAddress + 4, 0x6875_ABCDu);

        ctx[CpuRegister.Rdi] = PacketAddress;
        AgcExports.SetNop(ctx);

        Assert.Equal(0x0000_ABCDu, ReadUInt32(memory, PacketAddress + 4));
    }

    [Fact]
    public void SetNop_LeavesThePayloadOfANonType3PacketAlone()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, PacketAddress, 0x0000_002Du);
        WriteUInt32(memory, PacketAddress + 4, 0x6875_ABCDu);

        ctx[CpuRegister.Rdi] = PacketAddress;
        AgcExports.SetNop(ctx);

        Assert.Equal(0x0000_1001u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x6875_ABCDu, ReadUInt32(memory, PacketAddress + 4));
    }

    [Fact]
    public void SetNop_ReturnsInvalidArgumentForANullPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, AgcExports.SetNop(ctx));
    }

    private static uint ReadUInt32(ICpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteUInt32(ICpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

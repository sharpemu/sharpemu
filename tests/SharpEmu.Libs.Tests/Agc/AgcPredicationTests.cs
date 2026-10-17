// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcPredicationTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong PredicateAddress = BaseAddress + 0x800;

    [Fact]
    public void DcbSetPredication_EmitsGen5Packet()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = PredicateAddress + 7;
        ctx[CpuRegister.R9] = 2;

        var result = AgcExports.DcbSetPredication(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC002_2000u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x0003_1100u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(unchecked((uint)PredicateAddress), ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal((uint)(PredicateAddress >> 32), ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(PacketAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void SetPacketPredication_TogglesPacketHeaderBit()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const uint header = 0xC003_1500;
        WriteUInt32(memory, PacketAddress, header);

        ctx[CpuRegister.Rdi] = PacketAddress;
        ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetPacketPredication(ctx));
        Assert.Equal(header | 1u, ReadUInt32(memory, PacketAddress));

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetPacketPredication(ctx));
        Assert.Equal(header, ReadUInt32(memory, PacketAddress));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcCondExecTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong PredicateAddress = BaseAddress + 0x800;

    [Fact]
    public void DcbCondExec_EmitsPm4CondExecPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = PredicateAddress + 3; // must be dword-aligned in the packet
        ctx[CpuRegister.Rdx] = 0x24;

        var result = AgcExports.DcbCondExec(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC003_2200u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(unchecked((uint)PredicateAddress), ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal((uint)(PredicateAddress >> 32), ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0x24u, ReadUInt32(memory, PacketAddress + 16));
    }

    [Fact]
    public void DcbCondExec_NullBufferReturnsNullPacket()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;

        AgcExports.DcbCondExec(ctx);

        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDcbEventWriteTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong CounterAddress = 0x20_0000_03A8;

    [Fact]
    public void DcbEventWrite_EmitsTheAddressOfACounterDump()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = NewContext(memory);
        ctx[CpuRegister.Rsi] = 0x39;
        ctx[CpuRegister.Rdx] = CounterAddress;

        AgcExports.DcbEventWrite(ctx);

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC002_4600u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x39u | 0x100u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(unchecked((uint)CounterAddress) & ~7u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal((uint)(CounterAddress >> 32), ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(PacketAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void DcbEventWrite_EmitsACounterSelectionWithoutAnAddress()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = NewContext(memory);
        ctx[CpuRegister.Rsi] = 0x38;
        ctx[CpuRegister.Rdx] = 0;

        AgcExports.DcbEventWrite(ctx);

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0x38u | 0x100u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(PacketAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void DcbEventWrite_KeepsTheTwoDwordPacketForOtherEvents()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = NewContext(memory);
        ctx[CpuRegister.Rsi] = 0x16;
        ctx[CpuRegister.Rdx] = 0;

        AgcExports.DcbEventWrite(ctx);

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0x16u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(PacketAddress + 8, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void DcbEventWrite_IgnoresAnAddressForAnEventWithoutAPayload()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = NewContext(memory);
        ctx[CpuRegister.Rsi] = 0x16;
        ctx[CpuRegister.Rdx] = CounterAddress;

        AgcExports.DcbEventWrite(ctx);

        // The address belongs to counter events only: the packet stays two dwords without it.
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0x16u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(PacketAddress + 8, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    private static CpuContext NewContext(FakeCpuMemory memory)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        return ctx;
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

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

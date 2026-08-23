// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndexSizeTests
{
    private const ulong BaseAddress = 0x2_2000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong SubmitPacketAddress = BaseAddress + 0x800;
    private const uint ItSetUconfigRegIndex = 0x7A;

    [Fact]
    public void DcbSetIndexSize_WritesIndexedUconfigPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 2;

        Assert.Equal(0, AgcExports.DcbSetIndexSize(ctx));

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + 12, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(Pm4Header(3, ItSetUconfigRegIndex), ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x2000_0243u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0x481u, ReadUInt32(memory, PacketAddress + 8));
    }

    [Fact]
    public void FourArgumentSetIndexSize_WritesPerInstanceControl()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 1;

        Assert.Equal(0, AgcExports.DriverUnknownKRzWekV120(ctx));

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0x4401u, ReadUInt32(memory, PacketAddress + 8));
    }

    [Fact]
    public void DcbSetIndexSizeGetSize_ReturnsThreeDwords()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Assert.Equal(12, AgcExports.DcbSetIndexSizeGetSize(ctx));
        Assert.Equal(12UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SubmitDcb_AppliesIndexedUconfigIndexType()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, PacketAddress, Pm4Header(3, ItSetUconfigRegIndex));
        WriteUInt32(memory, PacketAddress + 4, 0x2000_0243u);
        WriteUInt32(memory, PacketAddress + 8, 0x401u);
        WriteUInt64(memory, SubmitPacketAddress, PacketAddress);
        WriteUInt32(memory, SubmitPacketAddress + 8, 3);
        ctx[CpuRegister.Rdi] = SubmitPacketAddress;

        Assert.Equal(0, AgcExports.DriverSubmitDcb(ctx));

        Assert.True(AgcExports.TryGetGraphicsIndexSizeForTests(ctx, out var indexSize));
        Assert.Equal(1u, indexSize);
    }

    private static void InitializeCommandBuffer(FakeCpuMemory memory)
    {
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
    }

    private static uint Pm4Header(uint dwords, uint opcode) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8);

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
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
}

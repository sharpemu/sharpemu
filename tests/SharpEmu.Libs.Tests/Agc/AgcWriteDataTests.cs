// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcWriteDataTests
{
    private const ulong BaseAddress = 0x2_3000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong DataAddress = BaseAddress + 0x800;
    private const ulong StackAddress = BaseAddress + 0xC00;
    private const ulong LabelAddress = BaseAddress + 0xE00;

    // Demon's Souls builds WRITE_DATA with a zero address into a template, copies
    // it into the ring and only then patches the destination to its label.
    [Fact]
    public void DcbWriteData_WithZeroDestination_EmitsPacketThatPatchTargets()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        WriteUInt32(memory, DataAddress, 0);
        WriteUInt64(memory, StackAddress + 8, 0);
        WriteUInt64(memory, StackAddress + 16, 1);
        WriteUInt32(memory, LabelAddress, 0x5A5A_5A5A);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 4;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = DataAddress;
        ctx[CpuRegister.R9] = 1;
        ctx[CpuRegister.Rsp] = StackAddress;
        AgcExports.DcbWriteData(ctx);

        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC0031054u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0UL, ReadUInt64(memory, PacketAddress + 8));

        ctx[CpuRegister.Rdi] = PacketAddress;
        ctx[CpuRegister.Rsi] = LabelAddress;
        AgcExports.WriteDataPatchSetAddressOrOffset(ctx);

        Assert.Equal(LabelAddress, ReadUInt64(memory, PacketAddress + 8));
        Assert.Equal(0x5A5A_5A5Au, ReadUInt32(memory, LabelAddress));
    }

    private static uint ReadUInt32(ICpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(ICpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt32(ICpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(ICpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcWaitRegMemTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong StackAddress = BaseAddress + 0x800;

    [Fact]
    public void DcbWaitRegMem32_EmitsGen5PacketLayout()
    {
        var memory = CreateMemory(out var ctx);
        var waitAddress = BaseAddress + 0xC03;

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 4;
        ctx[CpuRegister.R8] = 2;
        ctx[CpuRegister.R9] = waitAddress;
        WriteUInt64(memory, StackAddress + 8, 0x1122_3344_5566_7788);
        WriteUInt64(memory, StackAddress + 16, 0xAABB_CCDD_EEFF_0011);
        WriteUInt32(memory, StackAddress + 24, 0x123456);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbWaitRegMem(ctx));
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC005_1028u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x0000_0C00u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0xEEFF_0011u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0x0400_0053u, ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal(0xFFFFu, ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(PacketAddress + 28, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void DcbWaitRegMem64_EmitsGen5PacketLayout()
    {
        var memory = CreateMemory(out var ctx);
        var waitAddress = BaseAddress + 0xC07;

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 6;
        ctx[CpuRegister.Rcx] = 3;
        ctx[CpuRegister.R8] = 1;
        ctx[CpuRegister.R9] = waitAddress;
        WriteUInt64(memory, StackAddress + 8, 0x1122_3344_5566_7788);
        WriteUInt64(memory, StackAddress + 16, 0xAABB_CCDD_EEFF_0011);
        WriteUInt32(memory, StackAddress + 24, 0x320);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbWaitRegMem(ctx));
        Assert.Equal(0xC007_1058u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0x0000_0C00u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));
        Assert.Equal(0xEEFF_0011u, ReadUInt32(memory, PacketAddress + 12));
        Assert.Equal(0xAABB_CCDDu, ReadUInt32(memory, PacketAddress + 16));
        Assert.Equal(0x5566_7788u, ReadUInt32(memory, PacketAddress + 20));
        Assert.Equal(0x1122_3344u, ReadUInt32(memory, PacketAddress + 24));
        Assert.Equal(0x0200_0156u, ReadUInt32(memory, PacketAddress + 28));
        Assert.Equal(0x32u, ReadUInt32(memory, PacketAddress + 32));
    }

    [Fact]
    public void WaitRegMemPatchFunctions_UseGen5Fields()
    {
        var memory = CreateMemory(out var ctx);
        WriteUInt32(memory, PacketAddress, 0xC005_1028);
        WriteUInt32(memory, PacketAddress + 20, 0x0400_0153);

        ctx[CpuRegister.Rdi] = PacketAddress;
        ctx[CpuRegister.Rsi] = BaseAddress + 0xD07;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.WaitRegMemPatchAddress(ctx));
        Assert.Equal(0x0000_0D04u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(1u, ReadUInt32(memory, PacketAddress + 8));

        ctx[CpuRegister.Rsi] = 5;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.WaitRegMemPatchCompareFunction(ctx));
        Assert.Equal(0x0400_0155u, ReadUInt32(memory, PacketAddress + 20));

        ctx[CpuRegister.Rsi] = 0xDEAD_BEEF;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.WaitRegMemPatchReference(ctx));
        Assert.Equal(0xDEAD_BEEFu, ReadUInt32(memory, PacketAddress + 16));
    }

    private static FakeCpuMemory CreateMemory(out CpuContext ctx)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        return memory;
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

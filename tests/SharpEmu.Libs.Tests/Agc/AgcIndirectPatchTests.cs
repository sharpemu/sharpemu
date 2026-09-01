// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectPatchTests
{
    private const ulong BaseAddress = 0x2_2000_0000;
    private const ulong CommandAddress = BaseAddress + 0x100;
    private const uint SetContextRegIndirectHeader = 0xC003_9F00u;

    [Fact]
    public void SetCxRegisterCount_ReplacesCountAndPreservesOtherFields()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt64(memory, CommandAddress + 4, 0x5566_7788_99AA_BBCC);
        WriteUInt32(memory, CommandAddress + 12, 0x8000_0000u);
        WriteUInt32(memory, CommandAddress + 16, 0xCAFE_0003u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x4C;

        var result = AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(SetContextRegIndirectHeader, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x5566_7788_99AA_BBCCUL, ReadUInt64(memory, CommandAddress + 4));
        Assert.Equal(0x8000_0000u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0xCAFE_004Cu, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetCxRegisterCount_AllowsZeroAndAddRegistersUsesNewCount()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 16, 9);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
        Assert.Equal(0u, ReadUInt32(memory, CommandAddress + 16));

        ctx[CpuRegister.Rsi] = 7;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.SetCxRegIndirectPatchAddRegisters(ctx));
        Assert.Equal(7u, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetCxAddress_PatchesNativeAddressAndPreservesLowControlBits()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, CommandAddress, SetContextRegIndirectHeader);
        WriteUInt32(memory, CommandAddress + 4, 3u);
        WriteUInt32(memory, CommandAddress + 8, 0u);
        ctx[CpuRegister.Rdi] = CommandAddress;
        ctx[CpuRegister.Rsi] = 0x0000_0002_3456_789CUL;

        var result = AgcExports.SetCxRegIndirectPatchSetAddress(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x0000_0002_3456_789FUL, ReadUInt64(memory, CommandAddress + 4));
    }

    [Fact]
    public void SetCxRegisterCount_RejectsNullCommandAddress()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 4;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
    }

    [Fact]
    public void SetCxRegisterCount_ReturnsMemoryFaultForInvalidGuestMemory()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0x1000;
        ctx[CpuRegister.Rsi] = 4;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.SetCxRegIndirectPatchSetNumRegisters(ctx));
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

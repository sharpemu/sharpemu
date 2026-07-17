// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcShRegisterRangeTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong CommandBufferAddress = MemoryBase + 0x100;
    private const ulong ValuesAddress = MemoryBase + 0x300;
    private const ulong CommandAddress = MemoryBase + 0x400;

    [Fact]
    public void SetShRegisterRangeConsumesOnlyAdvertisedPacketSpace()
    {
        var memory = CreateRequest(commandDwordsAvailable: 5);
        var context = CreateContext(memory);

        _ = AgcExports.CbSetShRegisterRangeDirect(context);

        Assert.Equal(CommandAddress, context[CpuRegister.Rax]);
        Assert.Equal(
            CommandAddress + (5 * sizeof(uint)),
            ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(0xC003_7600u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x20u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0x1111_1111u, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(0x2222_2222u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0x3333_3333u, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void SetShRegisterRangeShortBufferDoesNotPartiallyConsumeSpace()
    {
        var memory = CreateRequest(commandDwordsAvailable: 4);
        for (ulong offset = 0; offset < 5 * sizeof(uint); offset += sizeof(uint))
        {
            WriteUInt32(memory, CommandAddress + offset, 0xA5A5_A5A5);
        }
        var context = CreateContext(memory);

        _ = AgcExports.CbSetShRegisterRangeDirect(context);

        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(CommandAddress, ReadUInt64(memory, CommandBufferAddress + 0x10));
        for (ulong offset = 0; offset < 5 * sizeof(uint); offset += sizeof(uint))
        {
            Assert.Equal(0xA5A5_A5A5u, ReadUInt32(memory, CommandAddress + offset));
        }
    }

    private static FakeCpuMemory CreateRequest(ulong commandDwordsAvailable)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        WriteUInt32(memory, ValuesAddress, 0x1111_1111);
        WriteUInt32(memory, ValuesAddress + 4, 0x2222_2222);
        WriteUInt32(memory, ValuesAddress + 8, 0x3333_3333);
        WriteUInt64(memory, CommandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(
            memory,
            CommandBufferAddress + 0x18,
            CommandAddress + (commandDwordsAvailable * sizeof(uint)));
        WriteUInt64(memory, CommandBufferAddress + 0x20, 0);
        WriteUInt64(memory, CommandBufferAddress + 0x28, 0);
        WriteUInt32(memory, CommandBufferAddress + 0x30, 0);
        return memory;
    }

    private static CpuContext CreateContext(FakeCpuMemory memory)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = 0x20;
        context[CpuRegister.Rdx] = ValuesAddress;
        context[CpuRegister.Rcx] = 3;
        return context;
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
}

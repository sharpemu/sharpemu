// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2SpeakerArrayExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutHandleAddress = MemoryBase + 0x100;
    private const ulong ReservedAddress = MemoryBase + 0x120;
    private const ulong ParamAddress = MemoryBase + 0x200;
    private const ulong SpeakerMemoryAddress = MemoryBase + 0x400;

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x2000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void WriteU64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static ulong ReadU64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static uint ReadU32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [Fact]
    public void GetSpeakerArrayMemorySize_NeverReturnsTheNotFoundSentinel()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 8;

        var result = AudioOut2Exports.AudioOut2GetSpeakerArrayMemorySize(ctx);

        Assert.NotEqual((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0x40 + 8 * 0x100 + 0x400, result);
        Assert.Equal((ulong)result, ctx[CpuRegister.Rax]);
        Assert.True(result < 0x10000);
    }

    [Fact]
    public void GetSpeakerArrayMemorySize_TwoChannelsIsExactChannelScaledSize()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 2;

        var result = AudioOut2Exports.AudioOut2GetSpeakerArrayMemorySize(ctx);

        Assert.Equal(0x40 + 2 * 0x100 + 0x400, result);
        Assert.Equal(0x640UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SpeakerArrayCreate_PublishesObjectPointerAndLeavesReservedSizeAlone()
    {
        var ctx = CreateContext(out var memory);
        // Stage a size in the reserved slot the way callers do before Create.
        WriteU64(memory, ReservedAddress, 0x100);
        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        ctx[CpuRegister.Rdx] = ReservedAddress;
        ctx[CpuRegister.Rcx] = 2;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(0, result);
        Assert.NotEqual(0UL, ctx[CpuRegister.Rax]);
        Assert.NotEqual(0x100UL, ctx[CpuRegister.Rax]);
        Assert.Equal(ctx[CpuRegister.Rax], ReadU64(memory, OutHandleAddress));
        // Reserved/size slot must remain untouched — writing it corrupted canaries.
        Assert.Equal(0x100UL, ReadU64(memory, ReservedAddress));
    }

    [Fact]
    public void SpeakerArrayCreate_PublishesHandleForTypicalCallShape()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        ctx[CpuRegister.Rdx] = ReservedAddress;
        ctx[CpuRegister.Rcx] = 2;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(0, result);
        Assert.NotEqual(0UL, ctx[CpuRegister.Rax]);
        Assert.NotEqual(0x10000UL, ctx[CpuRegister.Rax]);
        Assert.NotEqual((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void SpeakerArrayCreate_IgnoresCorruptedParamBufferFields()
    {
        var ctx = CreateContext(out var memory);
        // Simulate PortGetState having overwritten param+0x18 (size) with a
        // state blob — Create must NOT adopt that as an in-place buffer.
        WriteU64(memory, ParamAddress + 0x10, SpeakerMemoryAddress);
        WriteU64(memory, ParamAddress + 0x18, 0x100);
        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = OutHandleAddress;
        ctx[CpuRegister.Rdx] = ReservedAddress;
        ctx[CpuRegister.Rcx] = 2;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(0, result);
        Assert.NotEqual(SpeakerMemoryAddress, ctx[CpuRegister.Rax]);
        Assert.NotEqual(0x100UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SpeakerArrayDestroy_UnknownHandleStillSucceeds()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 0xDEAD_BEEF;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayDestroy(ctx);

        Assert.Equal(0, result);
    }
}

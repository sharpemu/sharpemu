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

    private static void WriteU32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
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

    // ABI: sceAudioOut2SpeakerArrayCreate(SceAudioOut2SpeakerArrayHandle *pHandle,
    // const SceAudioOut2SpeakerArrayParam *pVbapParams,
    // const SceAudioOut2AmbisonicsDecodeParam *pAmbiParams).
    [Fact]
    public void SpeakerArrayCreate_PublishesTheHandleToRdi()
    {
        var ctx = CreateContext(out var memory);
        // pVbapParams: uiNumSpeakers@0x08 = 2, pBuffer@0x10 = 0 (no caller buffer).
        WriteU32(memory, ParamAddress + 0x08, 2);
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = ParamAddress;
        ctx[CpuRegister.Rdx] = 0;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(0, result);
        var handle = ReadU64(memory, OutHandleAddress);
        Assert.NotEqual(0UL, handle);
        Assert.NotEqual(0x10000UL, handle);
    }

    [Fact]
    public void SpeakerArrayCreate_UsesTheCallerSuppliedBufferAsTheHandle()
    {
        var ctx = CreateContext(out var memory);
        WriteU32(memory, ParamAddress + 0x08, 2);
        WriteU64(memory, ParamAddress + 0x10, SpeakerMemoryAddress);
        WriteU64(memory, ParamAddress + 0x18, 0x1000);
        ctx[CpuRegister.Rdi] = OutHandleAddress;
        ctx[CpuRegister.Rsi] = ParamAddress;
        ctx[CpuRegister.Rdx] = 0;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(0, result);
        Assert.Equal(SpeakerMemoryAddress, ReadU64(memory, OutHandleAddress));
        // The object header records the speaker count the param block declared.
        Assert.Equal(2u, ReadU32(memory, SpeakerMemoryAddress + 4));
    }

    [Fact]
    public void SpeakerArrayCreate_RejectsANullHandleSlot()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = ParamAddress;
        ctx[CpuRegister.Rdx] = 0;

        var result = AudioOut2Exports.AudioOut2SpeakerArrayCreate(ctx);

        Assert.Equal(unchecked((int)0x8026800C), result);
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

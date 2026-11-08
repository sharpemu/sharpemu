// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2ContextGetQueueLevelExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ParamAddress = MemoryBase + 0x100;
    private const ulong ContextMemoryAddress = MemoryBase + 0x200;
    private const ulong OutContextAddress = MemoryBase + 0x300;
    private const ulong LevelAddress = MemoryBase + 0x400;
    private const ulong AvailableAddress = MemoryBase + 0x404;
    private const uint QueueDepth = 7;

    private static (CpuContext Context, FakeCpuMemory Memory, ulong Handle) CreateContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], QueueDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], 512);
        Assert.True(memory.TryWrite(ParamAddress, param));

        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = ContextMemoryAddress;
        ctx[CpuRegister.Rdx] = 0x1000;
        ctx[CpuRegister.Rcx] = OutContextAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(ctx));

        Span<byte> handleBytes = stackalloc byte[8];
        Assert.True(memory.TryRead(OutContextAddress, handleBytes));
        return (ctx, memory, BinaryPrimitives.ReadUInt64LittleEndian(handleBytes));
    }

    private static uint ReadU32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[4];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    [Fact]
    public void ContextGetQueueLevel_AvailableOnlyWritesQueueCapacity()
    {
        var (ctx, memory, handle) = CreateContext();
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = AvailableAddress;

        var result = AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);

        Assert.Equal(0, result);
        Assert.Equal(QueueDepth, ReadU32(memory, AvailableAddress));
    }

    [Fact]
    public void ContextGetQueueLevel_LevelOnlyWritesZero()
    {
        var (ctx, memory, handle) = CreateContext();
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = LevelAddress;
        ctx[CpuRegister.Rdx] = 0;

        var result = AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0u, ReadU32(memory, LevelAddress));
    }

    [Fact]
    public void ContextGetQueueLevel_BothOutputsRemainDistinct()
    {
        var (ctx, memory, handle) = CreateContext();
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = LevelAddress;
        ctx[CpuRegister.Rdx] = AvailableAddress;

        var result = AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0u, ReadU32(memory, LevelAddress));
        Assert.Equal(QueueDepth, ReadU32(memory, AvailableAddress));
    }
}

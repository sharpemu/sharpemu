// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AudioOut2QueueState", DisableParallelization = true)]
public sealed class AudioOut2QueueStateCollection
{
    public const string Name = "AudioOut2QueueState";
}

[Collection(AudioOut2QueueStateCollection.Name)]
public sealed class AudioOut2QueueLevelExportsTests
{
    private const ulong MemoryBase = 0x1_3000_0000;
    private const ulong ParamAddress = MemoryBase + 0x100;
    private const ulong ContextMemoryAddress = MemoryBase + 0x200;
    private const ulong ContextOutAddress = MemoryBase + 0x300;
    private const ulong QueuedAddress = MemoryBase + 0x400;
    private const ulong FreeAddress = MemoryBase + 0x404;

    [Fact]
    public void ContextGetQueueLevelReportsFreeGrainsAsSecondValue()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Span<byte> parameters = stackalloc byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x0C..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x10..], 256);
        Assert.True(memory.TryWrite(ParamAddress, parameters));

        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = ContextMemoryAddress;
        ctx[CpuRegister.Rdx] = 0x4000;
        ctx[CpuRegister.Rcx] = ContextOutAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(ctx));
        var handle = ReadUInt64(memory, ContextOutAddress);

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = QueuedAddress;
        ctx[CpuRegister.Rdx] = FreeAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx));
        Assert.Equal(0u, ReadUInt32(memory, QueuedAddress));
        Assert.Equal(4u, ReadUInt32(memory, FreeAddress));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextDestroy(ctx));
    }

    [Fact]
    public void ContextGetQueueLevelUsesSoftwarePacingWithoutBackend()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Span<byte> parameters = stackalloc byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x0C..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x10..], 4096);
        Assert.True(memory.TryWrite(ParamAddress, parameters));

        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = ContextMemoryAddress;
        ctx[CpuRegister.Rdx] = 0x4000;
        ctx[CpuRegister.Rcx] = ContextOutAddress;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(ctx));
        var handle = ReadUInt64(memory, ContextOutAddress);

        // No port is attached, so ContextPush cannot bind a host backend and
        // must account for the grain through the software pacing clock.
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextPush(ctx));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = QueuedAddress;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx));
        Assert.InRange(ReadUInt32(memory, QueuedAddress), 1u, 4u);

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextDestroy(ctx));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }
}

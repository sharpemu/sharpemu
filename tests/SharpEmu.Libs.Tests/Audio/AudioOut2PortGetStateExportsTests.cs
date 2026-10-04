// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2PortGetStateExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StateAddress = MemoryBase + 0x100;

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    [Fact]
    public void PortGetState_WritesExactlySizeofPortStateIgnoringPollutedR9()
    {
        var ctx = CreateContext(out var memory);
        // Paint the buffer so we can see the write footprint.
        Span<byte> paint = stackalloc byte[0x100];
        paint.Fill(0xAB);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = 0xDE1FF6800001UL;
        ctx[CpuRegister.Rsi] = StateAddress;
        ctx[CpuRegister.Rdx] = StateAddress + 0x200;
        // Polluted GetSize leftover — must NOT enlarge the write.
        ctx[CpuRegister.R9] = 0x180;

        var result = AudioOut2Exports.AudioOut2PortGetState(ctx);

        Assert.Equal(0, result);
        Span<byte> state = stackalloc byte[0x100];
        Assert.True(memory.TryRead(StateAddress, state));
        // SceAudioOut2PortState: output@0, numChannels@2, volume@4.
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(state));
        Assert.Equal(2, state[2]);
        Assert.Equal(0, state[3]);
        Assert.Equal(127, BinaryPrimitives.ReadInt16LittleEndian(state[4..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(state[6..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(state[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(state[0x0C..]));
        Assert.All(state[0x10..0x40].ToArray(), value => Assert.Equal(0, value));
        // Bytes past the fixed 0x40 structure must remain untouched.
        Assert.Equal(0xAB, state[0x40]);
        Assert.Equal(0xAB, state[0x7F]);
    }

    [Fact]
    public void PortGetState_WritesTheOutBufferOnTheGuestStack()
    {
        // The call takes one out pointer. Which host address range it lands in is
        // not a signal, and guessing per host silently changed behaviour between
        // Windows (stacks at 0x7FFF_xxxx_xxxx) and macOS (0x6FFF_xxxx_xxxx).
        var ctx = CreateContext(out var memory);
        ctx[CpuRegister.Rdi] = 0xDE1FF688004DUL;
        ctx[CpuRegister.Rsi] = StateAddress;
        ctx[CpuRegister.Rdx] = 0;

        var result = AudioOut2Exports.AudioOut2PortGetState(ctx);

        Assert.Equal(0, result);
        Span<byte> state = stackalloc byte[0x40];
        Assert.True(memory.TryRead(StateAddress, state));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(state));
    }

    [Fact]
    public void PortGetState_NullOutPointerIsInvalidPointer()
    {
        var ctx = CreateContext(out _);
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 0;
        // rdx used to be adopted as a fallback out pointer; it must be ignored.
        ctx[CpuRegister.Rdx] = StateAddress;

        var result = AudioOut2Exports.AudioOut2PortGetState(ctx);

        Assert.Equal(unchecked((int)0x8026800C), result);
    }

    [Fact]
    public void GetSpeakerInfo_WritesSizeofSpeakerInfoToRdiOnly()
    {
        var ctx = CreateContext(out var memory);
        Span<byte> paint = stackalloc byte[0x80];
        paint.Fill(0xCD);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = StateAddress;
        // rsi is the flags word, not a pointer.
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = StateAddress + 0x200;
        ctx[CpuRegister.R8] = 0x840;
        ctx[CpuRegister.R9] = 0x10C;

        var result = AudioOut2Exports.AudioOut2GetSpeakerInfo(ctx);

        Assert.Equal(0, result);
        Span<byte> info = stackalloc byte[0x80];
        Assert.True(memory.TryRead(StateAddress, info));
        Assert.Equal(0, info[0]);
        Assert.Equal(0, info[1]);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(info[2..]));
        Assert.Equal(0x3u, BinaryPrimitives.ReadUInt32LittleEndian(info[4..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info[8..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info[0x0C..]));
        Assert.Equal(-30, BinaryPrimitives.ReadInt16LittleEndian(info[0x10..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(info[0x12..]));
        Assert.Equal(30, BinaryPrimitives.ReadInt16LittleEndian(info[0x14..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(info[0x16..]));
        Assert.All(info[0x18..0x50].ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(0xCD, info[0x50]);
        Assert.Equal(0xCD, info[0x7F]);
    }

    [Fact]
    public void GetSystemState_WritesSizeofSystemStateToRdi()
    {
        var ctx = CreateContext(out var memory);
        Span<byte> paint = stackalloc byte[0x80];
        paint.Fill(0xEE);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = StateAddress;

        var result = AudioOut2Exports.AudioOut2GetSystemState(ctx);

        Assert.Equal(0, result);
        Span<byte> state = stackalloc byte[0x80];
        Assert.True(memory.TryRead(StateAddress, state));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(state));
        // sizeof(SceAudioOut2SystemState) is 0x40.
        Assert.Equal(0xEE, state[0x40]);
    }

    [Fact]
    public void GetSpeakerArrayCoefficients_WritesOnlyTheRequestedCount()
    {
        var ctx = CreateContext(out var memory);
        Span<byte> paint = stackalloc byte[0x80];
        paint.Fill(0x77);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = StateAddress;
        ctx[CpuRegister.Rdx] = 4;

        var result = AudioOut2Exports.AudioOut2GetSpeakerArrayCoefficients(ctx);

        Assert.Equal(0, result);
        Span<byte> coefficients = stackalloc byte[0x80];
        Assert.True(memory.TryRead(StateAddress, coefficients));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(coefficients));
        // Four floats, not a fixed 0x400-byte slab.
        Assert.Equal(0x77, coefficients[0x10]);
    }

    [Fact]
    public void GetSpeakerArrayAmbisonicsCoefficients_TakesItsBufferFromRdx()
    {
        var ctx = CreateContext(out var memory);
        Span<byte> paint = stackalloc byte[0x80];
        paint.Fill(0x33);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = 1;
        // eAmbiChannel, not a pointer.
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = StateAddress;
        ctx[CpuRegister.Rcx] = 2;

        var result = AudioOut2Exports.AudioOut2GetSpeakerArrayAmbisonicsCoefficients(ctx);

        Assert.Equal(0, result);
        Span<byte> coefficients = stackalloc byte[0x80];
        Assert.True(memory.TryRead(StateAddress, coefficients));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(coefficients));
        Assert.Equal(0x33, coefficients[8]);
    }

    [Fact]
    public void ContextGetQueueLevel_WritesFourBytesPerOutPointer()
    {
        var ctx = CreateContext(out var memory);
        Span<byte> paint = stackalloc byte[0x20];
        paint.Fill(0x11);
        Assert.True(memory.TryWrite(StateAddress, paint));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = StateAddress;
        ctx[CpuRegister.Rdx] = StateAddress + 8;

        var result = AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx);

        Assert.Equal(0, result);
        Span<byte> written = stackalloc byte[0x20];
        Assert.True(memory.TryRead(StateAddress, written));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(written));
        Assert.Equal(0x11, written[4]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(written[8..]));
        Assert.Equal(0x11, written[0x0C]);
    }

    [Fact]
    public void ContextGetQueueLevel_ReportsGrainsQueuedByAdvanceAndPush()
    {
        // Wwise renders exactly puiAvailableQueues grains per update and pushes them
        // non-blocking; a queue that never fills made it block in Push under its lock.
        var ctx = CreateContext(out var memory);
        var param = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x0C), 4);
        // 16384-sample grains (341 ms) keep the queue from draining during the test.
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x10), 0x4000);
        Assert.True(memory.TryWrite(StateAddress, param));
        ctx[CpuRegister.Rdi] = StateAddress;
        ctx[CpuRegister.Rsi] = StateAddress + 0x100;
        ctx[CpuRegister.Rdx] = 0x1000;
        ctx[CpuRegister.Rcx] = StateAddress + 0x200;
        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextCreate(ctx));
        Span<byte> handleBytes = stackalloc byte[8];
        Assert.True(memory.TryRead(StateAddress + 0x200, handleBytes));
        var handle = BinaryPrimitives.ReadUInt64LittleEndian(handleBytes);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Span<byte> level = stackalloc byte[0x0C];
        for (var grain = 1u; grain <= 4; grain++)
        {
            ctx[CpuRegister.Rdi] = handle;
            AudioOut2Exports.AudioOut2ContextAdvance(ctx);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = 0;
            AudioOut2Exports.AudioOut2ContextPush(ctx);

            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = StateAddress + 0x300;
            ctx[CpuRegister.Rdx] = StateAddress + 0x308;
            Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(ctx));
            Assert.True(memory.TryRead(StateAddress + 0x300, level));
            Assert.Equal(grain, BinaryPrimitives.ReadUInt32LittleEndian(level));
            Assert.Equal(4 - grain, BinaryPrimitives.ReadUInt32LittleEndian(level[8..]));
        }

        // Filling the queue up to its depth never blocks.
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(300));
        ctx[CpuRegister.Rdi] = handle;
        AudioOut2Exports.AudioOut2ContextDestroy(ctx);
    }
}

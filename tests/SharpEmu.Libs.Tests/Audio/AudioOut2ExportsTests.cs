// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2ExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong ParamAddress = Base + 0x100;
    private const ulong OutMemorySizeAddress = Base + 0x200;
    private const ulong OutQueueLevelAddress = Base + 0x300;
    private const ulong OutQueueAvailableAddress = Base + 0x400;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public AudioOut2ExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void ContextQueryMemory_WritesOnlyEightByteSizeOutput()
    {
        Span<byte> canary = stackalloc byte[24];
        canary.Fill(0xA5);
        Assert.True(_memory.TryWrite(OutMemorySizeAddress + 8, canary));

        _ctx[CpuRegister.Rdi] = ParamAddress;
        _ctx[CpuRegister.Rsi] = OutMemorySizeAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextQueryMemory(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutMemorySizeAddress, out var memorySize));
        Assert.Equal(0x10000UL, memorySize);

        Span<byte> canaryAfterCall = stackalloc byte[24];
        Assert.True(_memory.TryRead(OutMemorySizeAddress + 8, canaryAfterCall));
        Assert.True(canary.SequenceEqual(canaryAfterCall));
    }

    [Fact]
    public void ContextGetQueueLevel_WritesTwoFourByteOutputsWithoutTouchingCanary()
    {
        const ulong canary = 0xC0DE_C0DE_CAFE_BA00;
        Assert.True(_ctx.TryWriteUInt64(OutQueueLevelAddress + 4, canary));

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = OutQueueLevelAddress;
        _ctx[CpuRegister.Rdx] = OutQueueAvailableAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextGetQueueLevel(_ctx));
        Assert.True(_ctx.TryReadUInt32(OutQueueLevelAddress, out var queueLevel));
        Assert.True(_ctx.TryReadUInt32(OutQueueAvailableAddress, out var queueAvailable));
        Assert.True(_ctx.TryReadUInt64(OutQueueLevelAddress + 4, out var canaryAfterCall));
        Assert.Equal(0U, queueLevel);
        Assert.Equal(4U, queueAvailable);
        Assert.Equal(canary, canaryAfterCall);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2ContextQueryMemoryExportsTests
{
    private const int InvalidPointer = unchecked((int)0x8026_800C);
    private const ulong MemoryBase = 0x10_0000_0000;
    private const ulong ParamAddress = MemoryBase + 0x100;
    private const ulong StackPointer = MemoryBase + 0x400;
    private const ulong MemorySizeAddress = StackPointer + 0x10;
    private const ulong StaleThirdArgument = MemoryBase + 0x800;

    [Fact]
    public void ContextQueryMemory_WritesOnlyOneSizeTAndIgnoresRdx()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 4);
        Assert.True(memory.TryWrite(ParamAddress, param));

        Span<byte> guard = stackalloc byte[0x10];
        guard.Fill(0xA5);
        Assert.True(memory.TryWrite(MemorySizeAddress, guard));
        Assert.True(memory.TryWrite(StaleThirdArgument, guard));

        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = MemorySizeAddress;
        ctx[CpuRegister.Rdx] = StaleThirdArgument;
        ctx[CpuRegister.Rsp] = StackPointer;
        ctx[CpuRegister.Rbp] = StackPointer + 0x48;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextQueryMemory(ctx));

        Span<byte> output = stackalloc byte[0x10];
        Assert.True(memory.TryRead(MemorySizeAddress, output));
        Assert.Equal(0x11640UL, BinaryPrimitives.ReadUInt64LittleEndian(output));
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5 }, output[8..].ToArray());

        Span<byte> stale = stackalloc byte[0x10];
        Assert.True(memory.TryRead(StaleThirdArgument, stale));
        Assert.Equal(guard.ToArray(), stale.ToArray());
    }

    [Fact]
    public void ContextQueryMemory_RejectsMissingSecondArgument()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = StaleThirdArgument;

        Assert.Equal(
            InvalidPointer,
            AudioOut2Exports.AudioOut2ContextQueryMemory(ctx));
    }
}

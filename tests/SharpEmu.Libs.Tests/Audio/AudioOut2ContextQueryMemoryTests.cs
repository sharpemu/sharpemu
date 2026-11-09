// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

public sealed class AudioOut2ContextQueryMemoryTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ParamAddress = MemoryBase + 0x100;
    private const ulong SizeAddress = MemoryBase + 0x200;

    [Fact]
    public void ContextQueryMemory_WritesOneSizeAndNothingAfterIt()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Span<byte> param = stackalloc byte[0x40];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 8);
        Assert.True(memory.TryWrite(ParamAddress, param));
        Span<byte> paint = stackalloc byte[0x20];
        paint.Fill(0xAB);
        Assert.True(memory.TryWrite(SizeAddress, paint));

        ctx[CpuRegister.Rdi] = ParamAddress;
        ctx[CpuRegister.Rsi] = SizeAddress;

        Assert.Equal(0, AudioOut2Exports.AudioOut2ContextQueryMemory(ctx));
        Span<byte> written = stackalloc byte[0x20];
        Assert.True(memory.TryRead(SizeAddress, written));
        Assert.Equal(0x10000UL + (8 * 0x590UL), BinaryPrimitives.ReadUInt64LittleEndian(written));
        // The caller's locals after the size_t stay untouched.
        Assert.True(written[8..].ToArray().All(static value => value == 0xAB));
    }
}

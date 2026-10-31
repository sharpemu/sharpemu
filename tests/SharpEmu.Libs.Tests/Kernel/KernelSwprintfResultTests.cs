// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// vswprintf differs from vsnprintf: when the output does not fit in the buffer it answers -1,
// not the length it would have written. UE's string formatting grows its buffer on -1.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelSwprintfResultTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong Dest = Base + 0x100;
    private const ulong FormatAddress = Base + 0x400;

    [Theory]
    [InlineData(64UL, 5L, "hello")]
    [InlineData(6UL, 5L, "hello")]
    [InlineData(5UL, -1L, "hell")]
    [InlineData(3UL, -1L, "he")]
    [InlineData(1UL, -1L, "")]
    public void TruncatedOutputAnswersMinusOne(ulong count, long expected, string written)
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(Dest, Enumerable.Repeat((byte)0xAA, 64).ToArray());
        memory.TryWrite(FormatAddress, Encoding.Unicode.GetBytes("hello\0"));
        context[CpuRegister.Rdi] = Dest;
        context[CpuRegister.Rsi] = count;
        context[CpuRegister.Rdx] = FormatAddress;
        context[CpuRegister.Rcx] = 0;

        KernelMemoryCompatExports.Vswprintf(context);

        Assert.Equal(unchecked((ulong)expected), context[CpuRegister.Rax]);
        var buffer = new byte[(written.Length + 2) * 2];
        Assert.True(memory.TryRead(Dest, buffer));
        Assert.Equal(written + "\0", Encoding.Unicode.GetString(buffer, 0, (written.Length + 1) * 2));
        // Nothing past the terminator is touched.
        Assert.Equal(new byte[] { 0xAA, 0xAA }, buffer[((written.Length + 1) * 2)..]);
    }

    [Fact]
    public void ZeroCountWritesNothingAndAnswersMinusOne()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(Dest, Enumerable.Repeat((byte)0xAA, 8).ToArray());
        memory.TryWrite(FormatAddress, Encoding.Unicode.GetBytes("hello\0"));
        context[CpuRegister.Rdi] = Dest;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = FormatAddress;
        context[CpuRegister.Rcx] = 0;

        KernelMemoryCompatExports.Vswprintf(context);

        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        var buffer = new byte[8];
        Assert.True(memory.TryRead(Dest, buffer));
        Assert.All(buffer, value => Assert.Equal(0xAA, value));
    }
}

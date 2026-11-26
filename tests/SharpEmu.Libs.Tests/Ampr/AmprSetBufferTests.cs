// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AmprSetBufferTests
{
    private const ulong MemoryBase = 0x3_0000_0000;
    private const ulong CommandBuffer = MemoryBase + 0x100;
    private const ulong Records = MemoryBase + 0x200;
    private const ulong Output = MemoryBase + 0x800;
    private const ulong SecondOutput = MemoryBase + 0x808;

    // Death Stranding sets the same buffer before every command it appends; the commands
    // already queued must survive, or only the last one runs.
    [Fact]
    public void SettingAnAttachedBufferAgainKeepsItsQueuedCommands()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        Construct(context);
        Assert.Equal(0, SetBuffer(context));
        Write(context, Output, 1);

        Assert.Equal(unchecked((int)0x80020010), SetBuffer(context));
        Write(context, SecondOutput, 2);

        Assert.Equal(0, AmprExports.SubmitCommandBuffer(context, CommandBuffer, AmprExports.AmprEngine.Apr, 0x7201, null, out var submission));
        Assert.True(submission!.CompletedInline);
        Assert.Equal(1UL, Read(context, Output));
        Assert.Equal(2UL, Read(context, SecondOutput));
    }

    [Fact]
    public void AClearedBufferCanBeSetAgain()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        Construct(context);
        Assert.Equal(0, SetBuffer(context));

        context[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferClearBuffer(context));

        Assert.Equal(0, SetBuffer(context));
    }

    // The APR and AMM constructors leave the header alone, so a recycled object can hold an old
    // pointer there: only a buffer this module attached makes SetBuffer busy.
    [Fact]
    public void LeftoverBytesInAnObjectNeverSetAreNotBusy()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        Construct(context);
        Assert.True(context.TryWriteUInt64(CommandBuffer + 0x10, 0x1234_5000));

        Assert.Equal(0, SetBuffer(context));
        Assert.Equal(Records, Read(context, CommandBuffer + 0x10));
    }

    private static void Construct(CpuContext context)
    {
        context[CpuRegister.Rdi] = CommandBuffer;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
    }

    private static int SetBuffer(CpuContext context)
    {
        context[CpuRegister.Rdi] = CommandBuffer;
        context[CpuRegister.Rsi] = Records;
        context[CpuRegister.Rdx] = 0x100;
        return AmprExports.CommandBufferSetBuffer(context);
    }

    private static void Write(CpuContext context, ulong address, ulong value)
    {
        context[CpuRegister.Rdi] = CommandBuffer;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = value;
        Assert.Equal(0, AmprExports.CommandBufferWriteAddress0400(context));
    }

    private static ulong Read(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt64(address, out var value));
        return value;
    }
}

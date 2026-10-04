// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestExceptionContextLayoutTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x100;
    private const ulong ContextSize = 0x500;

    [Fact]
    public void SavedContinuation_IsWrittenAsBareMcontextWithSonyStackAlias()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var continuation = new GuestCpuContinuation
        {
            Rip = 0x101000,
            Rsp = 0x202000,
            Rflags = 0x203,
            FsBase = 0x303000,
            GsBase = 0x404000,
            Rax = 0x11,
            Rcx = 0x22,
            Rdx = 0x33,
            Rbx = 0x44,
            Rbp = 0x55,
            Rsi = 0x66,
            Rdi = 0x77,
            R8 = 0x88,
            R9 = 0x99,
            R10 = 0xAA,
            R11 = 0xBB,
            R12 = 0xCC,
            R13 = 0xDD,
            R14 = 0xEE,
            R15 = 0xFF,
        };

        Assert.True(DirectExecutionBackend.TryWriteGuestExceptionContext(
            context,
            ContextAddress,
            continuation,
            ContextSize));

        Assert.Equal(continuation.Rdi, Read64(memory, 0x08));
        Assert.Equal(continuation.Rsi, Read64(memory, 0x10));
        Assert.Equal(continuation.Rdx, Read64(memory, 0x18));
        Assert.Equal(continuation.Rcx, Read64(memory, 0x20));
        Assert.Equal(continuation.R8, Read64(memory, 0x28));
        Assert.Equal(continuation.R9, Read64(memory, 0x30));
        Assert.Equal(continuation.Rax, Read64(memory, 0x38));
        Assert.Equal(continuation.Rbx, Read64(memory, 0x40));
        Assert.Equal(continuation.Rbp, Read64(memory, 0x48));
        Assert.Equal(continuation.R10, Read64(memory, 0x50));
        Assert.Equal(continuation.R11, Read64(memory, 0x58));
        Assert.Equal(continuation.R12, Read64(memory, 0x60));
        Assert.Equal(continuation.R13, Read64(memory, 0x68));
        Assert.Equal(continuation.R14, Read64(memory, 0x70));
        Assert.Equal(continuation.R15, Read64(memory, 0x78));
        Assert.Equal(0UL, Read64(memory, 0x80));
        Assert.Equal(continuation.Rip, Read64(memory, 0xA0));
        Assert.Equal(continuation.Rflags, Read64(memory, 0xB0));
        Assert.Equal(continuation.Rsp, Read64(memory, 0xB8));
        Assert.Equal(0x480UL, Read64(memory, 0xC8));
        Assert.Equal(continuation.Rsp, Read64(memory, 0xF8));
        Assert.Equal(continuation.FsBase, Read64(memory, 0x440));
        Assert.Equal(continuation.GsBase, Read64(memory, 0x448));
    }

    [Fact]
    public void LiveContext_PublishesFlagsAndBothStackPointerOffsets()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            Rip = 0x505000,
            Rflags = 0x246,
            FsBase = 0x606000,
            GsBase = 0x707000,
        };
        context[CpuRegister.Rsp] = 0x808000;
        context[CpuRegister.Rdi] = 0x909000;

        Assert.True(DirectExecutionBackend.TryWriteGuestExceptionContext(
            context,
            ContextAddress,
            default,
            ContextSize));

        Assert.Equal(context[CpuRegister.Rdi], Read64(memory, 0x08));
        Assert.Equal(context.Rip, Read64(memory, 0xA0));
        Assert.Equal(context.Rflags, Read64(memory, 0xB0));
        Assert.Equal(context[CpuRegister.Rsp], Read64(memory, 0xB8));
        Assert.Equal(context[CpuRegister.Rsp], Read64(memory, 0xF8));
        Assert.Equal(context.FsBase, Read64(memory, 0x440));
        Assert.Equal(context.GsBase, Read64(memory, 0x448));
    }

    private static ulong Read64(FakeCpuMemory memory, ulong offset)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(ContextAddress + offset, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}

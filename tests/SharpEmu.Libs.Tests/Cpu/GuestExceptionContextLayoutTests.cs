// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestExceptionContextLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExceptionContext_UsesBareMcontextAndSonyStackAlias(bool saved)
    {
        const ulong address = 0x1_0000_0100;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            Rip = 0x101000,
            Rflags = 0x246,
            FsBase = 0x303000,
            GsBase = 0x404000,
        };
        context[CpuRegister.Rdi] = 0x11;
        context[CpuRegister.Rbx] = 0x22;
        context[CpuRegister.Rsp] = 0x202000;
        var continuation = saved ? new GuestCpuContinuation
        {
            Rip = 0x505000,
            Rsp = 0x606000,
            Rflags = 0x203,
            Rdi = 0x33,
            Rbx = 0x44,
            FsBase = 0x707000,
            GsBase = 0x808000,
        } : default;

        Assert.True(DirectExecutionBackend.TryWriteGuestExceptionContext(context, address, continuation, 0x500));
        var bytes = new byte[0x500];
        Assert.True(memory.TryRead(address, bytes));

        Assert.Equal(saved ? continuation.Rdi : context[CpuRegister.Rdi], Read64(0x08));
        Assert.Equal(saved ? continuation.Rbx : context[CpuRegister.Rbx], Read64(0x40));
        Assert.Equal(saved ? continuation.Rip : context.Rip, Read64(0xA0));
        Assert.Equal(saved ? continuation.Rflags : context.Rflags, Read64(0xB0));
        Assert.Equal(saved ? continuation.Rsp : context[CpuRegister.Rsp], Read64(0xB8));
        Assert.Equal(Read64(0xB8), Read64(0xF8));
        Assert.Equal(0x480UL, Read64(0xC8));
        Assert.Equal(saved ? continuation.FsBase : context.FsBase, Read64(0x440));
        Assert.Equal(saved ? continuation.GsBase : context.GsBase, Read64(0x448));

        ulong Read64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, sizeof(ulong)));
    }
}

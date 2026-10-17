// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelPosixSocketOptionTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong ValueAddress = BaseAddress + 0x100;
    private const ulong LengthAddress = BaseAddress + 0x200;

    [Theory]
    [InlineData(28, 41, 27, 1)]
    [InlineData(2, 0xFFFF, 0x20, 1)]
    [InlineData(2, 0xFFFF, 0x1002, 262144)]
    public void PosixSetAndGetSocketOption_UsesKernelSocketDescriptor(
        int family,
        int level,
        int option,
        int expectedValue)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var fd = CreateDatagramSocket(ctx, family);

        try
        {
            WriteInt32(memory, ValueAddress, expectedValue);
            ctx[CpuRegister.Rdi] = unchecked((uint)fd);
            ctx[CpuRegister.Rsi] = unchecked((uint)level);
            ctx[CpuRegister.Rdx] = unchecked((uint)option);
            ctx[CpuRegister.Rcx] = ValueAddress;
            ctx[CpuRegister.R8] = sizeof(int);

            Assert.Equal(0, NetExports.PosixSetsockopt(ctx));
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);

            WriteInt32(memory, ValueAddress, 0);
            WriteInt32(memory, LengthAddress, sizeof(int));
            ctx[CpuRegister.Rdi] = unchecked((uint)fd);
            ctx[CpuRegister.Rsi] = unchecked((uint)level);
            ctx[CpuRegister.Rdx] = unchecked((uint)option);
            ctx[CpuRegister.Rcx] = ValueAddress;
            ctx[CpuRegister.R8] = LengthAddress;

            Assert.Equal(0, NetExports.PosixGetsockopt(ctx));
            Assert.Equal(expectedValue, ReadInt32(memory, ValueAddress));
            Assert.Equal(sizeof(int), ReadInt32(memory, LengthAddress));
        }
        finally
        {
            KernelSocketCompatExports.TryCloseSocketFd(fd);
        }
    }

    [Fact]
    public void PosixSetSocketOption_RejectsUnknownOptionOnKnownDescriptor()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var fd = CreateDatagramSocket(ctx, 2);

        try
        {
            WriteInt32(memory, ValueAddress, 1);
            ctx[CpuRegister.Rdi] = unchecked((uint)fd);
            ctx[CpuRegister.Rsi] = 0xFFFF;
            ctx[CpuRegister.Rdx] = 2;
            ctx[CpuRegister.Rcx] = ValueAddress;
            ctx[CpuRegister.R8] = sizeof(int);

            Assert.Equal(-1, NetExports.PosixSetsockopt(ctx));
            Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
        }
        finally
        {
            KernelSocketCompatExports.TryCloseSocketFd(fd);
        }
    }

    private static int CreateDatagramSocket(CpuContext ctx, int family)
    {
        ctx[CpuRegister.Rdi] = unchecked((uint)family);
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelSocketCompatExports.Socket(ctx));
        return checked((int)ctx[CpuRegister.Rax]);
    }

    private static void WriteInt32(FakeCpuMemory memory, ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static int ReadInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}

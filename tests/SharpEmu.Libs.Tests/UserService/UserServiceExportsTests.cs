// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests.UserService;

public sealed class UserServiceExportsTests
{
    [Theory]
    [InlineData(0x10000000)]
    [InlineData(1)]
    public void GetUserNumberWritesOneBasedSlotWithoutOverwritingAdjacentData(int userId)
    {
        const ulong address = 0x1_0000_0000;
        var memory = new FakeCpuMemory(address, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(address, new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xAA, 0xBB, 0xCC, 0xDD }));
        ctx[CpuRegister.Rdi] = unchecked((ulong)userId);
        ctx[CpuRegister.Rsi] = address;
        Assert.Equal(0, UserServiceExports.UserServiceGetUserNumber(ctx));
        Span<byte> actual = stackalloc byte[8];
        Assert.True(memory.TryRead(address, actual));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0xAA, 0xBB, 0xCC, 0xDD }, actual.ToArray());
    }

    [Theory]
    [InlineData(0x10000001, 0x1_0000_0000UL, unchecked((int)0x80960009))]
    [InlineData(0x10000000, 0UL, unchecked((int)0x80960005))]
    [InlineData(0x10000000, 0x2_0000_0000UL, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)]
    public void GetUserNumberValidatesUserAndOutput(int userId, ulong address, int expected)
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rdi] = unchecked((ulong)userId);
        ctx[CpuRegister.Rsi] = address;
        Assert.Equal(expected, UserServiceExports.UserServiceGetUserNumber(ctx));
    }

    [Fact]
    public void TerminateResetsTheLoginEvent()
    {
        const ulong address = 0x1_0000_0000;
        var memory = new FakeCpuMemory(address, 0x100);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.Equal(0, UserServiceExports.UserServiceTerminate(ctx));
        Assert.Equal(0, UserServiceExports.UserServiceInitialize(ctx));
        ctx[CpuRegister.Rdi] = address;
        Assert.Equal(0, UserServiceExports.UserServiceGetEvent(ctx));
        Assert.Equal(unchecked((int)0x80960007), UserServiceExports.UserServiceGetEvent(ctx));
        Assert.Equal(0, UserServiceExports.UserServiceTerminate(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, UserServiceExports.UserServiceInitialize(ctx));
        Assert.Equal(0, UserServiceExports.UserServiceGetEvent(ctx));
        Assert.Equal(0, UserServiceExports.UserServiceTerminate(ctx));
    }
}

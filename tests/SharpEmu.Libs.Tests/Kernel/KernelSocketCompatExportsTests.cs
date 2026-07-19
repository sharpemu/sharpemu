// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSocketCompatExportsTests
{
    [Fact]
    public void Connect_InvalidSockaddrLeavesFdOpenForGuestClose()
    {
        const ulong memoryBase = 0x0000_7FFF_3000_0000;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 6;

        Assert.Equal(0, KernelSocketCompatExports.Socket(context));
        Assert.NotEqual(ulong.MaxValue, context[CpuRegister.Rax]);
        var guestFd = checked((int)context[CpuRegister.Rax]);

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            context[CpuRegister.Rsi] = memoryBase;
            context[CpuRegister.Rdx] = 0;

            Assert.Equal(0, KernelSocketCompatExports.Connect(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);

            context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.PosixClose(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            // A second close of an already-closed fd fails per the POSIX ABI:
            // -1 with errno set, not the raw Orbis NOT_FOUND sentinel.
            context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            Assert.Equal(-1, KernelMemoryCompatExports.PosixClose(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        }
        finally
        {
            KernelSocketCompatExports.TryCloseSocketFd(guestFd);
        }
    }
}

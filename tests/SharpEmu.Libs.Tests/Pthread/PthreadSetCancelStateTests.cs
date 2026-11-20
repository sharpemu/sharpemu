// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadSetCancelStateTests
{
    [Fact]
    public void SetCancelState_ReturnsPreviousStateAndRejectsInvalid()
    {
        const ulong memoryBase = 0x4_0000_0000;
        const ulong oldAddress = memoryBase + 0x100;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);

        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = oldAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadSetcancelstate(context));
        Assert.True(context.TryReadUInt32(oldAddress, out var old));
        Assert.Equal(0U, old);

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadSetcancelstate(context));
        Assert.True(context.TryReadUInt32(oldAddress, out old));
        Assert.Equal(1U, old);

        context[CpuRegister.Rdi] = 2;
        Assert.Equal(22, KernelPthreadCompatExports.PosixPthreadSetcancelstate(context));
    }
}

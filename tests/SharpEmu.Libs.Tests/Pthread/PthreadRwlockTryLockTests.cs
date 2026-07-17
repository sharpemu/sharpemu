// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

public sealed class PthreadRwlockTryLockTests
{
    [Fact]
    public void TryWriteLockReportsBusyWhileReadLockIsHeld()
    {
        var memory = new FakeCpuMemory(0x1000, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong rwlockAddress = 0x2000;
        context[CpuRegister.Rdi] = rwlockAddress;

        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(context));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
            KernelPthreadExtendedCompatExports.PthreadRwlockTrywrlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockTrywrlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(context));
    }

    [Fact]
    public void TryReadLockReportsBusyWhileWriteLockIsHeld()
    {
        var memory = new FakeCpuMemory(0x1000, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong rwlockAddress = 0x3000;
        context[CpuRegister.Rdi] = rwlockAddress;

        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(context));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
            KernelPthreadExtendedCompatExports.PthreadRwlockTryrdlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(context));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockDestroy(context));
    }
}

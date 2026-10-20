// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[Collection(PthreadExceptionStateCollection.Name)]
public sealed class PthreadCondRecursiveMutexTests
{
    [Fact]
    public void TimedCondWaitReleasesAndRestoresARecursiveMutexHeldTwice()
    {
        const ulong memoryBase = 0x1_0007_0000;
        const ulong attrAddress = memoryBase + 0x100;
        const ulong mutexAddress = memoryBase + 0x200;
        const ulong condAddress = memoryBase + 0x300;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrInit(context));
        context[CpuRegister.Rsi] = 2;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrSettype(context));
        context[CpuRegister.Rdi] = mutexAddress;
        context[CpuRegister.Rsi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
        Assert.True(context.TryWriteUInt64(mutexAddress + 8, KernelPthreadState.GetCurrentThreadHandle()));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));

        context[CpuRegister.Rdi] = condAddress;
        context[CpuRegister.Rsi] = mutexAddress;
        context[CpuRegister.Rdx] = 20_000;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            KernelPthreadCompatExports.PthreadCondTimedwait(context));

        // The wait gave the mutex up completely and took both levels back.
        context[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
        Assert.NotEqual(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
    }
}

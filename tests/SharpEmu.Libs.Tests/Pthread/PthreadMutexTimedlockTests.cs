// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[Collection(PthreadExceptionStateCollection.Name)]
public sealed class PthreadMutexTimedlockTests
{
    [Fact]
    public void TimedlockTimesOutWhileAnotherThreadHoldsTheMutexAndAcquiresAfterRelease()
    {
        const ulong memoryBase = 0x1_0006_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext));

        var first = int.MinValue;
        var second = int.MinValue;
        using var timedOut = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var contender = new Thread(() =>
        {
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = mutexAddress;
            context[CpuRegister.Rsi] = 20_000;
            first = KernelPthreadCompatExports.PthreadMutexTimedlock(context);
            timedOut.Set();
            Assert.True(released.Wait(TimeSpan.FromSeconds(5)));
            context[CpuRegister.Rsi] = 1_000_000;
            second = KernelPthreadCompatExports.PthreadMutexTimedlock(context);
            _ = KernelPthreadCompatExports.PthreadMutexUnlock(context);
        }) { IsBackground = true };
        contender.Start();
        Assert.True(timedOut.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, first);

        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext));
        released.Set();
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, second);
    }
}

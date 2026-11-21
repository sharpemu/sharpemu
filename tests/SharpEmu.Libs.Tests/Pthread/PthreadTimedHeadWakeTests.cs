// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[Collection(PthreadExceptionStateCollection.Name)]
public sealed class PthreadTimedHeadWakeTests
{
    [Fact]
    public void ExpiredHeadDropWakesNextHostWaiterWithoutPolling()
    {
        const ulong memoryBase = 0x6_0300_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext));

        using var headParked = new ManualResetEventSlim();
        using var releaseHead = new ManualResetEventSlim();
        using var nextParked = new ManualResetEventSlim();
        using var nextAcquired = new ManualResetEventSlim();
        var deliveryGate = new object();
        var armed = false;
        var nextPolls = 0;
        CpuContext? headContext = null;

        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, PollCountingScheduler>();
        var proxy = (PollCountingScheduler)(object)scheduler;
        proxy.OnDeliver = context =>
        {
            if (ReferenceEquals(context, Volatile.Read(ref headContext)))
            {
                headParked.Set();
                releaseHead.Wait(TimeSpan.FromSeconds(10));
                return;
            }

            lock (deliveryGate)
            {
                nextParked.Set();
                if (armed)
                {
                    nextPolls++;
                }
            }
        };

        var headResult = -1;
        var nextResult = -1;
        Exception? failure = null;

        var head = new Thread(() =>
        {
            try
            {
                var context = new CpuContext(memory, Generation.Gen5);
                Volatile.Write(ref headContext, context);
                context[CpuRegister.Rdi] = mutexAddress;
                context[CpuRegister.Rsi] = 200_000;
                headResult = KernelPthreadCompatExports.PthreadMutexRelativeTimedlock(context);
            }
            catch (Exception error)
            {
                failure = error;
            }
        }) { IsBackground = true };

        var next = new Thread(() =>
        {
            try
            {
                var context = new CpuContext(memory, Generation.Gen5);
                context[CpuRegister.Rdi] = mutexAddress;
                nextResult = KernelPthreadCompatExports.PthreadMutexLock(context);
                nextAcquired.Set();
                KernelPthreadCompatExports.PthreadMutexUnlock(context);
            }
            catch (Exception error)
            {
                failure = error;
            }
        }) { IsBackground = true };

        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            head.Start();
            Assert.True(headParked.Wait(TimeSpan.FromSeconds(5)));
            next.Start();
            Assert.True(nextParked.Wait(TimeSpan.FromSeconds(5)));
            Thread.Sleep(350);

            lock (deliveryGate)
            {
                nextPolls = 0;
                armed = true;
                Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext));
            }

            Assert.True(nextAcquired.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref nextPolls));
        }
        finally
        {
            releaseHead.Set();
            GuestThreadExecution.Scheduler = previousScheduler;
            Assert.True(head.Join(TimeSpan.FromSeconds(5)));
            Assert.True(next.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(failure);
        Assert.NotEqual(0, headResult);
        Assert.Equal(0, nextResult);
    }

    public class PollCountingScheduler : DispatchProxy
    {
        public Action<CpuContext>? OnDeliver { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGuestThreadScheduler.DeliverPendingGuestExceptionIfReady))
            {
                OnDeliver!((CpuContext)args![0]!);
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[Collection(PthreadExceptionStateCollection.Name)]
public sealed class PthreadAbandonWakeTests
{
    [Fact]
    public void AbandonWakesNonCooperativeHeadWaiterWithoutPolling()
    {
        const ulong memoryBase = 0x6_0200_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext));
        var ownerHandle = KernelPthreadState.GetCurrentThreadHandle();

        using var parked = new ManualResetEventSlim();
        using var acquired = new ManualResetEventSlim();
        var deliveryGate = new object();
        var armed = false;
        var pollsAfterAbandon = 0;

        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, PollCountingScheduler>();
        var proxy = (PollCountingScheduler)(object)scheduler;
        proxy.OnDeliver = _ =>
        {
            lock (deliveryGate)
            {
                parked.Set();
                if (armed)
                {
                    pollsAfterAbandon++;
                }
            }
        };

        var lockResult = -1;
        Exception? failure = null;
        var waiter = new Thread(() =>
        {
            try
            {
                var context = new CpuContext(memory, Generation.Gen5);
                context[CpuRegister.Rdi] = mutexAddress;
                lockResult = KernelPthreadCompatExports.PthreadMutexLock(context);
                acquired.Set();
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
            waiter.Start();
            Assert.True(parked.Wait(TimeSpan.FromSeconds(5)));

            int released;
            lock (deliveryGate)
            {
                pollsAfterAbandon = 0;
                armed = true;
                released = KernelPthreadCompatExports.AbandonMutexesOwnedByThread(ownerHandle, "regression");
            }

            Assert.Equal(1, released);
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, Volatile.Read(ref pollsAfterAbandon));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            Assert.True(waiter.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Null(failure);
        Assert.Equal(0, lockResult);
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

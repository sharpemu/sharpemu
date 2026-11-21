// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

[Collection(PthreadExceptionStateCollection.Name)]
public sealed class PthreadStaleWaiterTests
{
    [Fact]
    public void StaleWaiterReacquisitionReleasesTheHostWaiter()
    {
        const ulong memoryBase = 0x6_0100_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext));

        using var reacquired = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        var reacquireResult = -1;
        var outerResult = -1;
        Exception? failure = null;
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, ReacquireScheduler>();
        var proxy = (ReacquireScheduler)(object)scheduler;
        proxy.OnDeliver = context =>
        {
            context[CpuRegister.Rdi] = mutexAddress;
            context[CpuRegister.Rsi] = 1;
            reacquireResult = KernelPthreadCompatExports.PthreadMutexRelativeTimedlock(context);
            reacquired.Set();
        };

        var previousScheduler = GuestThreadExecution.Scheduler;
        var waiter = new Thread(() =>
        {
            try
            {
                var context = new CpuContext(memory, Generation.Gen5);
                context[CpuRegister.Rdi] = mutexAddress;
                outerResult = KernelPthreadCompatExports.PthreadMutexLock(context);
                returned.Set();
            }
            catch (Exception error)
            {
                failure = error;
            }
        }) { IsBackground = true };

        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            waiter.Start();
            Assert.True(reacquired.Wait(TimeSpan.FromSeconds(5)));

            // The stale waiter shared this thread's identity with the new
            // acquisition. It must have been released instead of being left
            // queued with no hand-off path.
            Assert.True(returned.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext));
            var joined = waiter.Join(TimeSpan.FromSeconds(5));
            GuestThreadExecution.Scheduler = previousScheduler;
            Assert.True(joined);
        }

        Assert.Null(failure);
        Assert.NotEqual(0, reacquireResult);
        Assert.NotEqual(0, outerResult);
    }

    public class ReacquireScheduler : DispatchProxy
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

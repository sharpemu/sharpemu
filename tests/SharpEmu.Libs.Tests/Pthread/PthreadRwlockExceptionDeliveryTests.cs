// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// The last of the four host-side parks a non-cooperative guest thread can stop in. Both rwlock
/// waits were bare untimed <c>Monitor.Wait(rwlock.SyncRoot)</c> calls, so a thread parked in one
/// could not run a kernel exception raised on it at all - the queue is only drained at an import
/// boundary, and a thread parked inside this export never reaches one (#254).
/// </summary>
[Collection("GuestThreadSchedulerState")]
public sealed class PthreadRwlockExceptionDeliveryTests
{
    private const ulong MemoryBase = 0x4_0000_0000;
    private const ulong RwlockAddress = MemoryBase + 0x100;

    /// <summary>
    /// A writer parks behind a held read lock and is released only by the unlock the delivery hook
    /// performs, so a build whose park never drops the gate to consult that hook fails on timeout.
    /// </summary>
    [Fact]
    public void HostRwlockWriterParkRunsAKernelExceptionRaisedWhileItIsParked()
    {
        var context = CreateContext();
        InitializeRwlock(context);
        var scheduler = new DeliveryStubScheduler();

        // Reader counts are per-thread, so the read lock has to be released by the same thread
        // that took it. Park a dedicated one on the two events below.
        using var releaseReader = new ManualResetEventSlim(false);
        using var readerReleased = new ManualResetEventSlim(false);
        using var readerHeld = new ManualResetEventSlim(false);
        var readerThread = new Thread(() =>
        {
            var readerContext = CreateContext();
            readerContext[CpuRegister.Rdi] = RwlockAddress;
            if (KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(readerContext) != 0)
            {
                return;
            }

            readerHeld.Set();
            releaseReader.Wait(TimeSpan.FromSeconds(25));
            readerContext[CpuRegister.Rdi] = RwlockAddress;
            KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(readerContext);
            readerReleased.Set();
        })
        {
            IsBackground = true,
        };
        readerThread.Start();
        Assert.True(readerHeld.Wait(TimeSpan.FromSeconds(10)), "the read lock was never taken");

        // Release it from the delivery hook. The unlock runs on the reader thread, so if the
        // parked writer still held rwlock.SyncRoot when it called us, that thread could not enter
        // PthreadRwlockUnlock and this wait would time out.
        scheduler.OnDeliver = () =>
        {
            releaseReader.Set();
            scheduler.SignalCompleted = readerReleased.Wait(TimeSpan.FromSeconds(10));
        };

        using var _ = new SchedulerScope(scheduler);
        var writer = Task.Run(() => WriteLock(context));

        Assert.True(writer.Wait(TimeSpan.FromSeconds(20)), "the parked rwlock writer was never released");
        Assert.Equal(0, writer.Result);
        Assert.True(scheduler.DeliverCalls > 0, "the park never offered to run a queued exception");
        Assert.True(scheduler.SignalCompleted, "the handler ran while the rwlock gate was held");

        DestroyRwlock(context);
    }

    [Fact]
    public void AnUncontendedRwlockAcquireNeverConsultsTheDeliveryHook()
    {
        var context = CreateContext();
        InitializeRwlock(context);
        var scheduler = new DeliveryStubScheduler();

        using var _ = new SchedulerScope(scheduler);
        context[CpuRegister.Rdi] = RwlockAddress;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(context));
        Assert.Equal(0, scheduler.DeliverCalls);

        context[CpuRegister.Rdi] = RwlockAddress;
        KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(context);
        DestroyRwlock(context);
    }

    private static int WriteLock(CpuContext template)
    {
        var context = new CpuContext(template.Memory, Generation.Gen5);
        context[CpuRegister.Rdi] = RwlockAddress;
        return KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(context);
    }

    private static CpuContext CreateContext() => new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static void InitializeRwlock(CpuContext context)
    {
        context[CpuRegister.Rdi] = RwlockAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(context));
    }

    private static void DestroyRwlock(CpuContext context)
    {
        context[CpuRegister.Rdi] = RwlockAddress;
        KernelPthreadExtendedCompatExports.PthreadRwlockDestroy(context);
    }
}

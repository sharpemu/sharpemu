// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// The host-side condition-variable park is the other place a non-cooperative guest thread can
/// stop indefinitely inside an HLE export, so it needs the same escape hatch the semaphore wait
/// has: a queued kernel exception is only ever delivered at an import boundary, and a thread
/// parked in here never reaches one. Before this the untimed park was a bare
/// <c>Monitor.Wait(SyncRoot)</c> with no timeout at all, so the thread could not be reached even
/// in principle.
/// </summary>
[Collection("GuestThreadSchedulerState")]
public sealed class PthreadCondExceptionDeliveryTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong MutexAddress = MemoryBase + 0x100;
    private const ulong CondAddress = MemoryBase + 0x200;

    /// <summary>
    /// The waiter is released only by the signal the delivery hook posts, so on a build whose
    /// park never drops the lock to consult that hook this fails on its timeout.
    /// </summary>
    [Fact]
    public void UntimedHostCondWaitRunsAKernelExceptionRaisedWhileItIsParked()
    {
        var context = CreateContext();
        InitializeCondAndMutex(context);
        var scheduler = new DeliveryStubScheduler();
        var signalContext = CreateContext();

        // Signal from another thread, mirroring a guest handler that runs off the waiter's stack.
        // If the waiter still held state.SyncRoot when it called us, PthreadCondSignal could not
        // take that lock and the join below would time out.
        scheduler.OnDeliver = () => scheduler.SignalCompleted = Task
            .Run(() =>
            {
                signalContext[CpuRegister.Rdi] = CondAddress;
                return KernelPthreadCompatExports.PthreadCondSignal(signalContext);
            })
            .Wait(TimeSpan.FromSeconds(10));

        using var _ = new SchedulerScope(scheduler);
        var waiter = Task.Run(() => WaitOnCond(context));

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(20)), "the parked condition wait was never released");
        Assert.True(scheduler.DeliverCalls > 0, "the park never offered to run a queued exception");
        Assert.True(scheduler.SignalCompleted, "the handler ran while the condvar lock was held");

        DestroyCondAndMutex(context);
    }

    [Fact]
    public void OrdinaryCondSignalStillReleasesTheHostPark()
    {
        var context = CreateContext();
        InitializeCondAndMutex(context);
        var scheduler = new DeliveryStubScheduler();
        var signalContext = CreateContext();

        using var _ = new SchedulerScope(scheduler);
        var waiter = Task.Run(() => WaitOnCond(context));

        // Signal repeatedly: a condition variable is an edge, so a signal delivered before the
        // waiter has registered is correctly lost and has to be repeated.
        var deadline = Environment.TickCount64 + 20_000;
        var released = false;
        while (!released && Environment.TickCount64 < deadline)
        {
            signalContext[CpuRegister.Rdi] = CondAddress;
            KernelPthreadCompatExports.PthreadCondSignal(signalContext);
            released = waiter.Wait(TimeSpan.FromMilliseconds(250));
        }

        Assert.True(released, "an ordinary signal never released the host park");

        DestroyCondAndMutex(context);
    }

    /// <summary>
    /// Slicing the park must not turn a finite timeout into a longer one: the deadline is
    /// re-derived from the original timeout on every slice, so a slice expiring is not the
    /// overall timeout.
    /// </summary>
    [Fact]
    public void TimedHostCondWaitStillHonoursItsOverallDeadline()
    {
        var context = CreateContext();
        InitializeCondAndMutex(context);
        var scheduler = new DeliveryStubScheduler();

        using var _ = new SchedulerScope(scheduler);
        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = MutexAddress;
        context[CpuRegister.Rdx] = 300_000;

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        KernelPthreadCompatExports.PthreadCondTimedwait(context);
        elapsed.Stop();

        // The 300 ms request must not have been rounded up to anything near the slice-driven
        // worst case, and must not have returned instantly either.
        Assert.InRange(elapsed.Elapsed.TotalMilliseconds, 150, 5_000);

        DestroyCondAndMutex(context);
    }

    private static int WaitOnCond(CpuContext template)
    {
        var context = new CpuContext(template.Memory, Generation.Gen5);
        context[CpuRegister.Rdi] = MutexAddress;
        KernelPthreadCompatExports.PthreadMutexLock(context);

        context[CpuRegister.Rdi] = CondAddress;
        context[CpuRegister.Rsi] = MutexAddress;
        return KernelPthreadCompatExports.PthreadCondWait(context);
    }

    private static CpuContext CreateContext() => new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static void InitializeCondAndMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = MutexAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(context));

        context[CpuRegister.Rdi] = CondAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondInit(context));
    }

    private static void DestroyCondAndMutex(CpuContext context)
    {
        context[CpuRegister.Rdi] = CondAddress;
        KernelPthreadCompatExports.PthreadCondDestroy(context);
        context[CpuRegister.Rdi] = MutexAddress;
        KernelPthreadCompatExports.PthreadMutexDestroy(context);
    }
}

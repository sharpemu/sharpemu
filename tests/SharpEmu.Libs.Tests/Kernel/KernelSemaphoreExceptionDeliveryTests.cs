// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// A guest thread that blocks in sceKernelWaitSema without a cooperative identity parks on the
/// host inside the HLE export, so it never reaches the import boundary where queued kernel
/// exceptions are delivered. IL2CPP's stop-the-world collector suspends threads by raising an
/// exception on them and then waits for the acknowledgement the handler posts, so a thread that
/// cannot run its handler while blocked strands the collection and hangs the title (#254).
///
/// These drive the real export against a stub scheduler, with no CPU backend and no guest image.
/// </summary>
[Collection("GuestThreadSchedulerState")]
public sealed class KernelSemaphoreExceptionDeliveryTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong HandleAddress = MemoryBase + 0x100;
    private const ulong NameAddress = MemoryBase + 0x200;
    private const ulong TimeoutAddress = MemoryBase + 0x300;

    /// <summary>
    /// The headline regression. The waiter parks with no tokens available and is released only by
    /// work the delivery hook performs, so on a build where the wait never consults that hook the
    /// task never completes and this fails on its timeout.
    /// </summary>
    [Fact]
    public void HostThreadWaitRunsAKernelExceptionRaisedWhileItIsParked()
    {
        var context = CreateContext();
        var handle = CreateSemaphore(context, initialCount: 0);
        var scheduler = new DeliveryStubScheduler();
        var signalContext = CreateContext();

        // Post the token from the delivery hook, standing in for a guest handler that
        // acknowledges its suspension. Doing it from another thread is the point: if the waiter
        // were still holding the semaphore gate when it called us, that thread could not enter
        // KernelSignalSema and the join below would time out.
        scheduler.OnDeliver = () => scheduler.SignalCompleted = Task
            .Run(() => KernelSemaphoreCompatExports.KernelSignalSema(signalContext, handle, 1))
            .Wait(TimeSpan.FromSeconds(10));

        using var _ = new SchedulerScope(scheduler);
        var waiter = Task.Run(() => WaitForever(context, handle));

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(20)), "the parked wait was never released");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Result);
        Assert.True(scheduler.DeliverCalls > 0, "the wait never offered to run a queued exception");
        Assert.True(scheduler.SignalCompleted, "the handler ran while the semaphore gate was held");

        DeleteSemaphore(context, handle);
    }

    /// <summary>
    /// The delivery hook is an addition to the wait, not a replacement for it: an ordinary
    /// sceKernelSignalSema from another thread must still release the waiter with a token
    /// consumed, whether or not anything is ever delivered.
    /// </summary>
    [Fact]
    public void OrdinarySignalStillWakesTheHostThreadWaitAndConsumesOneToken()
    {
        var context = CreateContext();
        var handle = CreateSemaphore(context, initialCount: 0);
        var scheduler = new DeliveryStubScheduler();
        var signalContext = CreateContext();

        using var _ = new SchedulerScope(scheduler);
        var waiter = Task.Run(() => WaitForever(context, handle));

        // Signal repeatedly until the waiter observes it, so the test cannot race the park.
        Assert.True(
            SpinUntil(
                () =>
                {
                    if (waiter.IsCompleted)
                    {
                        return true;
                    }

                    KernelSemaphoreCompatExports.KernelSignalSema(signalContext, handle, 1);
                    return waiter.Wait(TimeSpan.FromMilliseconds(250));
                },
                TimeSpan.FromSeconds(20)),
            "an ordinary signal never released the wait");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Result);

        DeleteSemaphore(context, handle);
    }

    [Fact]
    public void HostThreadWaitStillTimesOutAndWritesBackTheRemainingTimeout()
    {
        var context = CreateContext();
        var handle = CreateSemaphore(context, initialCount: 0);
        var scheduler = new DeliveryStubScheduler();

        using var _ = new SchedulerScope(scheduler);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = TimeoutAddress;
        WriteUInt32(context, TimeoutAddress, 5_000);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            KernelSemaphoreCompatExports.KernelWaitSema(context));
        Assert.Equal(0u, ReadUInt32(context, TimeoutAddress));

        DeleteSemaphore(context, handle);
    }

    /// <summary>
    /// A wait that can be satisfied immediately must not take the parking path at all, so the
    /// added hook costs an uncontended acquire nothing.
    /// </summary>
    [Fact]
    public void AnImmediatelySatisfiableWaitNeverConsultsTheDeliveryHook()
    {
        var context = CreateContext();
        var handle = CreateSemaphore(context, initialCount: 1);
        var scheduler = new DeliveryStubScheduler();

        using var _ = new SchedulerScope(scheduler);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelWaitSema(context));
        Assert.Equal(0, scheduler.DeliverCalls);

        DeleteSemaphore(context, handle);
    }

    private static int WaitForever(CpuContext template, uint handle)
    {
        // A private context per thread: CpuContext holds the guest register file and the export
        // writes RAX into it.
        var context = new CpuContext(template.Memory, Generation.Gen5);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;
        return KernelSemaphoreCompatExports.KernelWaitSema(context);
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
        }

        return false;
    }

    private static CpuContext CreateContext() => new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static uint CreateSemaphore(CpuContext context, int initialCount)
    {
        var name = Encoding.UTF8.GetBytes("test_sema\0");
        Assert.True(context.Memory.TryWrite(NameAddress, name));

        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = unchecked((ulong)(long)initialCount);
        context[CpuRegister.R8] = 16;
        context[CpuRegister.R9] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSemaphoreCompatExports.KernelCreateSema(context));
        return ReadUInt32(context, HandleAddress);
    }

    private static void DeleteSemaphore(CpuContext context, uint handle)
    {
        context[CpuRegister.Rdi] = handle;
        KernelSemaphoreCompatExports.KernelDeleteSema(context);
    }

    private static void WriteUInt32(CpuContext context, ulong address, uint value) =>
        Assert.True(context.Memory.TryWrite(address, BitConverter.GetBytes(value)));

    private static uint ReadUInt32(CpuContext context, ulong address)
    {
        var bytes = new byte[sizeof(uint)];
        Assert.True(context.Memory.TryRead(address, bytes));
        return BitConverter.ToUInt32(bytes);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>
/// The event-flag host park already leaves its gate to pump the scheduler, so it is the one place
/// that needed nothing restructured - only the delivery call added alongside the pump. These pin
/// that it is actually made, since a thread parked here is inside an import and never reaches the
/// boundary where a queued kernel exception would otherwise be delivered (#254).
/// </summary>
[Collection("GuestThreadSchedulerState")]
public sealed class KernelEventFlagExceptionDeliveryTests
{
    private const ulong MemoryBase = 0x3_0000_0000;
    private const ulong HandleAddress = MemoryBase + 0x100;
    private const ulong NameAddress = MemoryBase + 0x200;
    private const ulong ResultAddress = MemoryBase + 0x300;

    private const uint WaitOr = 0x02;
    private const ulong WaitedPattern = 0x4;

    [Fact]
    public void HostEventFlagParkRunsAKernelExceptionRaisedWhileItIsParked()
    {
        var context = CreateContext();
        var handle = CreateEventFlag(context);
        var scheduler = new DeliveryStubScheduler();
        var setContext = CreateContext();

        // Raise the awaited bits from the delivery hook, on another thread. If the park still held
        // state.Gate when it called us, KernelSetEventFlag could not take that lock and the join
        // below would time out.
        scheduler.OnDeliver = () => scheduler.SignalCompleted = Task
            .Run(() =>
            {
                setContext[CpuRegister.Rdi] = handle;
                setContext[CpuRegister.Rsi] = WaitedPattern;
                return KernelEventFlagCompatExports.KernelSetEventFlag(setContext);
            })
            .Wait(TimeSpan.FromSeconds(10));

        using var _ = new SchedulerScope(scheduler);
        var waiter = Task.Run(() => WaitOnEventFlag(context, handle));

        Assert.True(waiter.Wait(TimeSpan.FromSeconds(20)), "the parked event-flag wait was never released");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Result);
        Assert.True(scheduler.DeliverCalls > 0, "the park never offered to run a queued exception");
        Assert.True(scheduler.SignalCompleted, "the handler ran while the event-flag gate was held");

        DeleteEventFlag(context, handle);
    }

    [Fact]
    public void AnAlreadySatisfiedEventFlagWaitNeverConsultsTheDeliveryHook()
    {
        var context = CreateContext();
        var handle = CreateEventFlag(context);
        var scheduler = new DeliveryStubScheduler();

        using var _ = new SchedulerScope(scheduler);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = WaitedPattern;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelSetEventFlag(context));

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, WaitOnEventFlag(context, handle));
        Assert.Equal(0, scheduler.DeliverCalls);

        DeleteEventFlag(context, handle);
    }

    private static int WaitOnEventFlag(CpuContext template, ulong handle)
    {
        var context = new CpuContext(template.Memory, Generation.Gen5);
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = WaitedPattern;
        context[CpuRegister.Rdx] = WaitOr;
        context[CpuRegister.Rcx] = ResultAddress;
        context[CpuRegister.R8] = 0;
        return KernelEventFlagCompatExports.KernelWaitEventFlag(context);
    }

    private static CpuContext CreateContext() => new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static ulong CreateEventFlag(CpuContext context)
    {
        Assert.True(context.Memory.TryWrite(NameAddress, Encoding.UTF8.GetBytes("test_evf\0")));

        context[CpuRegister.Rdi] = HandleAddress;
        context[CpuRegister.Rsi] = NameAddress;
        context[CpuRegister.Rdx] = 0x20; // multi-thread wait
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(context));

        var bytes = new byte[sizeof(ulong)];
        Assert.True(context.Memory.TryRead(HandleAddress, bytes));
        return BitConverter.ToUInt64(bytes);
    }

    private static void DeleteEventFlag(CpuContext context, ulong handle)
    {
        context[CpuRegister.Rdi] = handle;
        KernelEventFlagCompatExports.KernelDeleteEventFlag(context);
    }
}

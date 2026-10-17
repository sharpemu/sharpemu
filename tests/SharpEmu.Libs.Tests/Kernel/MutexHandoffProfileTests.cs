// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Pthread;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection("GuestThreadFlowProfile")]
public sealed class MutexHandoffProfileTests
{
    [Fact]
    public void BufferRetainsWholeRecordsAndClosesUntilReset()
    {
        var buffer = new MutexHandoffProfile.EventBuffer(2);
        for (var index = 0; index < 4; index++) buffer.Record(CreateEvent(index));
        var snapshot = buffer.Close();
        Assert.Equal(4, snapshot.TotalEvents);
        Assert.Equal(new[] { CreateEvent(2), CreateEvent(3) }, snapshot.Events);
        buffer.Record(CreateEvent(4));
        Assert.Empty(buffer.Close().Events);
        buffer.Reset();
        buffer.Record(CreateEvent(5));
        Assert.Equal(CreateEvent(5), Assert.Single(buffer.Close().Events));
    }

    [Fact]
    public void ConcurrentWritesKeepIdentityAndOwnershipTogether()
    {
        var buffer = new MutexHandoffProfile.EventBuffer(128);
        Parallel.For(0, 10000, index => buffer.Record(CreateEvent(index)));
        var snapshot = buffer.Close();
        Assert.Equal(10000, snapshot.TotalEvents);
        Assert.Equal(128, snapshot.Events.Length);
        Assert.All(snapshot.Events, item => Assert.Equal(CreateEvent((int)item.Timestamp), item));
    }

    [Fact]
    public void MutexHandoffKeepsFifoOwnershipAndWakeArguments()
    {
        var exports = typeof(KernelPthreadCompatExports);
        var stateType = exports.GetNestedType("PthreadMutexState", BindingFlags.NonPublic)!;
        var state = Activator.CreateInstance(stateType, nonPublic: true)!;
        var owner = stateType.GetProperty("OwnerThreadId")!;
        var enqueue = exports.GetMethod("EnqueueMutexWaiterLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        var grant = exports.GetMethod("TryGrantMutexWaiterLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        var wake = exports.GetMethod("WakeProfiledMutexWaiter", BindingFlags.Static | BindingFlags.NonPublic)!;
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, MutexWakeScheduler>();
        var previousScheduler = GuestThreadExecution.Scheduler;
        var previousGuest = GuestThreadExecution.EnterGuestThread(0x10);
        MutexHandoffProfile.StartSession();
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            owner.SetValue(state, 0x10UL);
            var first = enqueue.Invoke(null, [state, 0x20UL, true, "mutex-test:first"])!;
            var second = enqueue.Invoke(null, [state, 0x30UL, true, "mutex-test:second"])!;
            Assert.False((bool)grant.Invoke(null, [state, first])!);
            owner.SetValue(state, 0UL);
            Assert.False((bool)grant.Invoke(null, [state, second])!);
            Assert.True((bool)grant.Invoke(null, [state, first])!);
            Assert.Equal(0x20UL, owner.GetValue(state));
            wake.Invoke(null, [state, first]);
            Assert.Equal(1, ((MutexWakeScheduler)(object)scheduler).Calls);
            Assert.Equal(0x20UL, owner.GetValue(state));
            var snapshot = MutexHandoffProfile.Close();
            if (!MutexHandoffProfile.Enabled)
            {
                Assert.Empty(snapshot.Events);
                return;
            }
            Assert.Equal(new[] { "Queued", "Queued", "Granted", "WakeStarted", "WakeFinished" },
                snapshot.Events.Select(item => item.Stage));
            Assert.Equal(0x10UL, snapshot.Events[0].Owner);
            Assert.Equal(0x20UL, snapshot.Events[2].Owner);
            Assert.Equal(0x20UL, snapshot.Events[2].Waiter);
            Assert.Equal("mutex-test:first", snapshot.Events[2].WakeKey);
            Assert.Equal(1, snapshot.Events[2].Waiting);
            Assert.Equal(1, snapshot.Events[^1].Result);
            Assert.All(snapshot.Events, item =>
            {
                Assert.Equal(snapshot.Events[0].MutexIdentity, item.MutexIdentity);
                Assert.Equal(0x10UL, item.GuestThread);
            });
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            GuestThreadExecution.RestoreGuestThread(previousGuest);
            MutexHandoffProfile.Close();
        }
    }

    [Fact]
    public void PublicMutexOperationsRecordAddressOnlyWhenDetailedTraceIsEnabled()
    {
        const ulong memoryBase = 0x6A0000000;
        const ulong address = memoryBase + 0x100;
        var context = new CpuContext(new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000), Generation.Gen5);
        context[CpuRegister.Rdi] = address;
        Assert.True(context.TryWriteUInt64(address, 1));
        MutexHandoffProfile.StartSession();
        try
        {
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(context));
            var snapshot = MutexHandoffProfile.Close();
            if (!MutexHandoffProfile.Enabled)
            {
                Assert.Empty(snapshot.Events);
                Assert.Equal(0, MutexHandoffProfile.CreateIdentity());
                return;
            }
            Assert.Equal(new[] { "lock", "Released", "unlock" }, snapshot.Events.Select(item => item.Stage));
            Assert.All(snapshot.Events, item => Assert.Equal(address, item.Address));
            Assert.NotEqual(0UL, snapshot.Events[0].Owner);
            Assert.Equal(0UL, snapshot.Events[1].Owner);
            Assert.True(snapshot.Events[0].MutexIdentity > 0);
        }
        finally
        {
            KernelPthreadCompatExports.PthreadMutexDestroy(context);
            MutexHandoffProfile.Close();
        }
    }

    [Fact]
    public void OutputIncludesOwnerWaiterAndWakeKey()
    {
        using var output = new StringWriter();
        MutexHandoffProfile.WriteTrace(output, new MutexHandoffProfile.Snapshot([CreateEvent(1)], 4));
        Assert.Contains("retained=1 overwritten=3", output.ToString());
        Assert.Contains("mutex=4 stage=Granted address=0x5 owner=0x6 waiter=0x7 wake=mutex:1 waiting=8 result=9", output.ToString());
    }

    public class MutexWakeScheduler : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IGuestThreadScheduler.WakeBlockedThreads), targetMethod!.Name);
            Assert.Equal("mutex-test:first", args![0]);
            Assert.Equal(1, args[1]);
            Calls++;
            return 1;
        }
    }

    private static MutexHandoffProfile.TraceEvent CreateEvent(int index) => new(index, index + 1,
        (ulong)index + 2, index + 3, "Granted", (ulong)index + 4, (ulong)index + 5,
        (ulong)index + 6, $"mutex:{index}", index + 7, index + 8);
}

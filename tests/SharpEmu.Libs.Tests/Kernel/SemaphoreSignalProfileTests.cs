// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection("GuestThreadFlowProfile")]
public sealed class SemaphoreSignalProfileTests
{
    [Fact]
    public void RingRetainsWholeEventsAndReopensForTheNextSession()
    {
        var buffer = new SemaphoreSignalProfile.EventBuffer(2);
        for (var index = 0; index < 5; index++) buffer.Record(CreateEvent(index));
        var snapshot = buffer.Close();
        Assert.Equal(5, snapshot.TotalEvents);
        Assert.Equal(new[] { CreateEvent(3), CreateEvent(4) }, snapshot.Events);
        buffer.Record(CreateEvent(5));
        Assert.Empty(buffer.Close().Events);
        buffer.Reset();
        buffer.Record(CreateEvent(6));
        Assert.Equal(CreateEvent(6), Assert.Single(buffer.Close().Events));
    }

    [Fact]
    public void ConcurrentWritesPreserveSignalIdentityAndValues()
    {
        var buffer = new SemaphoreSignalProfile.EventBuffer(128);
        Parallel.For(0, 10000, index => buffer.Record(CreateEvent(index)));
        var snapshot = buffer.Close();
        Assert.Equal(10000, snapshot.TotalEvents);
        Assert.Equal(128, snapshot.Events.Length);
        Assert.All(snapshot.Events, item => Assert.Equal(CreateEvent((int)item.Timestamp), item));
    }

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(0, 0, false)]
    [InlineData(1, int.MaxValue, false)]
    public void SignalKeepsTokenSemanticsAndRecordsStagesWhenEnabled(uint initial, int count, bool succeeds)
    {
        const ulong memoryBase = 0x100000000;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase;
        context[CpuRegister.Rdx] = initial;
        Assert.Equal(0, KernelSemaphoreCompatExports.PosixSemInit(context));
        Assert.True(context.TryReadUInt32(memoryBase, out var handle));
        var previousScheduler = GuestThreadExecution.Scheduler;
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, SignalScheduler>();
        var proxy = (SignalScheduler)(object)scheduler;
        var previousGuest = GuestThreadExecution.EnterGuestThread(0x555);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(0x12345678, 0x87654321, 0);
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            SemaphoreSignalProfile.StartSession();
            var result = KernelSemaphoreCompatExports.KernelSignalSema(context, handle, count);
            Assert.Equal(succeeds ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
            Assert.Equal(succeeds ? 1 : 0, proxy.WakeCalls);
            if (succeeds) Assert.Equal($"sceKernelWaitSema:{handle:X8}", proxy.WakeKey);
            context[CpuRegister.Rdi] = memoryBase;
            context[CpuRegister.Rsi] = memoryBase + 16;
            Assert.Equal(0, KernelSemaphoreCompatExports.PosixSemGetValue(context));
            Assert.True(context.TryReadUInt32(memoryBase + 16, out var remaining));
            Assert.Equal(initial + (succeeds ? (uint)count : 0), remaining);

            var snapshot = SemaphoreSignalProfile.Close();
            if (!RenderPhaseProfile.FrameTraceEnabled)
            {
                Assert.Empty(snapshot.Events);
            }
            else
            {
                var expectedStages = succeeds
                    ? new[] { SemaphoreSignalProfile.Stage.Entered, SemaphoreSignalProfile.Stage.Published,
                        SemaphoreSignalProfile.Stage.WakeStarted, SemaphoreSignalProfile.Stage.WakeFinished }
                    : new[] { SemaphoreSignalProfile.Stage.Entered, SemaphoreSignalProfile.Stage.Rejected };
                Assert.Equal(expectedStages, snapshot.Events.Select(item => item.Stage));
                Assert.All(snapshot.Events, item =>
                {
                    Assert.Equal(handle, item.Signal.Handle);
                    Assert.Equal(count, item.Signal.Count);
                    Assert.Equal(0x555UL, item.Signal.GuestThread);
                    Assert.Equal(0x12345678UL, item.Signal.ReturnAddress);
                    Assert.Equal(snapshot.Events[0].Signal.Sequence, item.Signal.Sequence);
                });
                Assert.Equal(succeeds ? 3 : result, snapshot.Events[^1].Result);
                if (succeeds) Assert.Equal((int)remaining, snapshot.Events[1].Available);
            }
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousGuest);
            context[CpuRegister.Rdi] = handle;
            KernelSemaphoreCompatExports.KernelDeleteSema(context);
            SemaphoreSignalProfile.StartSession();
        }
    }

    [Fact]
    public void SnapshotOutputIncludesSignalCorrelationAndCallSite()
    {
        using var output = new StringWriter();
        SemaphoreSignalProfile.WriteTrace(output, new SemaphoreSignalProfile.Snapshot([CreateEvent(1)], 5));
        Assert.Contains("retained=1 overwritten=4", output.ToString());
        Assert.Contains("signal=2 stage=Published handle=0x5 add=6 available=7 waiting=8 result=9 return=0x4", output.ToString());
    }

    public class SignalScheduler : DispatchProxy
    {
        public int WakeCalls { get; private set; }
        public string? WakeKey { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IGuestThreadScheduler.WakeBlockedThreads), targetMethod!.Name);
            WakeCalls++;
            WakeKey = (string)args![0]!;
            return 3;
        }
    }

    private static SemaphoreSignalProfile.TraceEvent CreateEvent(int index) => new(index, index + 1,
        new SemaphoreSignalProfile.Signal(index + 1, (ulong)index + 2, (ulong)index + 3,
            (uint)index + 4, index + 5), SemaphoreSignalProfile.Stage.Published, index + 6, index + 7, index + 8);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

internal sealed class DeferredSignalHarness : IDisposable, IEndOfPipeSink, IFlipSubmitter
{
    internal readonly FakeTickDevice Device = new();
    internal readonly SubmissionScheduler Scheduler;
    internal readonly StreamRunner Runner = new();
    internal readonly PendingGuestPublications Pending = new();
    internal readonly CommandStreamQueue Queue;
    internal readonly object Presenter;
    internal readonly List<ulong> Observed = new();
    internal PendingGuestPublications.Entry? CapturedPublication;

    internal DeferredSignalHarness()
    {
        Scheduler = NewActiveScheduler(Device);
        Queue = new CommandStreamQueue(Runner.Host);
        Presenter = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.PresenterType);
        Set("_scheduler", Scheduler);
        Set("_guestPublications", Pending);
        Set("_guestMemory", Runner.Host.Memory);
        Set("_commandStream", Queue);
        Set("_endOfPipe", new EndOfPipe(this, this));
        var host = (ICommandStreamHost)Presenter;
        Runner.Host.PendingPublications = host.HasPendingPublications;
        Runner.Host.PendingMemoryPublication = host.HasPendingMemoryPublication;
        Runner.Host.DeferredLabelRead = host.TryReadDeferredLabel;
        Runner.Host.OrderedWrite = host.TryDeferOrderedWrite;
        Runner.Host.CrossQueueHold = host.MustHoldForCrossQueueOrder;
        Runner.Host.DeferredEndOfPipe = (queue, write) => host.TryDeferEndOfPipe(queue, in write);
    }

    internal void Set(string name, object value) => PresenterUnderTest.PresenterType
        .GetField(name, PresenterUnderTest.InstanceMembers)!.SetValue(Presenter, value);

    internal PendingGuestPublications.Entry AddDump(ulong count = 37)
    {
        var entry = Pending.Add(0, StreamRunner.DataAddress, 8);
        Pending.Register(entry, Scheduler.CurrentTick);
        Scheduler.QueueMemoryPublication(() => PresenterUnderTest.PresenterType
            .GetMethod("PublishGuestEntry", PresenterUnderTest.InstanceMembers)!
            .Invoke(Presenter, [entry, (Action)(() => Runner.Host.WriteQword(StreamRunner.DataAddress, (1UL << 63) | count))]));
        return entry;
    }

    public void TriggerInterrupt(int eventId, uint contextId) => Observed.Add(Runner.Host.ReadQword(StreamRunner.DataAddress));
    public void CompleteFlip(ulong requestId) => Observed.Add(Runner.Host.ReadQword(StreamRunner.DataAddress));
    public void WaitForSubmitSlot() => throw new NotSupportedException();
    public int SubmitFlipFromGpu(RecordingBuffer buffer, int handle, int index, int flipMode, long flipArg, out ulong requestId)
    {
        CapturedPublication = (PendingGuestPublications.Entry?)PresenterUnderTest.PresenterType
            .GetField("_capturePublication", PresenterUnderTest.InstanceMembers)!.GetValue(Presenter);
        requestId = 77;
        return 0;
    }

    public void Dispose()
    {
        Device.Complete(Scheduler.CurrentTick);
        Device.CompleteOnSubmit = true;
        Scheduler.Dispose();
    }
}

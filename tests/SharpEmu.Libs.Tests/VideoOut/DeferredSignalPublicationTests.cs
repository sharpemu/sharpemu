// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class DeferredSignalPublicationTests
{
    [Fact]
    public void CapturedFlipRemainsHiddenUntilItsLabelAndSignalComplete()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var host = (ICommandStreamHost)harness.Presenter;
        var request = host.PrepareFlip(0, 1, 0, 0, 0);
        var capture = Assert.IsType<SharpEmu.Libs.Gpu.Scheduling.PendingGuestPublications.Entry>(harness.CapturedPublication);
        Assert.False(capture.Completed);
        var presentationType = PresenterUnderTest.PresenterType.DeclaringType!.GetNestedType("Presentation",
            System.Reflection.BindingFlags.NonPublic)!;
        var presentation = Activator.CreateInstance(presentationType,
            [null, 1u, 1u, 1L, default(SharpEmu.ShaderCompiler.GuestDrawKind), false, 0UL, 0L, false, 0UL, 0UL, capture]);
        var ready = PresenterUnderTest.PresenterType.GetMethod("IsPresentationReadyLocked", PresenterUnderTest.InstanceMembers)!;
        Assert.False((bool)ready.Invoke(harness.Presenter, [presentation])!);
        Assert.True(host.TryDeferEndOfPipe(0, new EndOfPipeWrite(EndOfPipeWriteKind.FlipWithWrite32,
            1, StreamRunner.LabelAddress, 7, FlipRequestId: request)));
        harness.Scheduler.Flush();
        harness.Device.Complete(1);
        harness.Scheduler.RunCompletedOperations();
        Assert.False(capture.Completed);
        Assert.Empty(harness.Observed);
        harness.Device.Complete(2);
        harness.Scheduler.RunCompletedOperations();
        Assert.True(capture.Completed);
        Assert.True((bool)ready.Invoke(harness.Presenter, [presentation])!);
        Assert.Equal(7u, harness.Runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal([(1UL << 63) | 37], harness.Observed);
        Assert.Equal(0, harness.Pending.Count);
    }

    private static uint[] Label(ulong address, uint value) => StreamRunner.Packet(PacketOpcode.ReleaseMemory,
        0x528, 1u << 29, StreamRunner.Low(address), StreamRunner.High(address), value, 0, 0);

    [Fact]
    public void FailedFlipLabelClearsCaptureWithoutDeliveringSignal()
    {
        using var fatal = new FatalScope();
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var host = (ICommandStreamHost)harness.Presenter;
        var request = host.PrepareFlip(0, 1, 0, 0, 0);
        Assert.True(host.TryDeferEndOfPipe(0, new EndOfPipeWrite(EndOfPipeWriteKind.FlipWithWrite32,
            1, 0x1234, 7, FlipRequestId: request)));
        harness.Scheduler.Flush();
        harness.Device.Complete(2);
        Assert.Throws<SchedulerFatalException>(harness.Scheduler.RunCompletedOperations);
        Assert.Empty(harness.Observed);
        Assert.False(harness.CapturedPublication!.Completed);
        Assert.Equal(0, harness.Pending.Count);
        var captures = (System.Collections.IDictionary)PresenterUnderTest.PresenterType
            .GetField("_flipPublications", PresenterUnderTest.InstanceMembers)!.GetValue(harness.Presenter)!;
        Assert.Empty(captures);
    }

    [Fact]
    public void LabelDoesNotStopLaterDrawOrDependOnFutureDump()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var runner = harness.Runner;
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Label(StreamRunner.LabelAddress, 7),
            StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 3, 0)));
        Assert.Single(runner.Host.AutoDraws);
        Assert.Equal(0u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        var future = harness.AddDump(52);
        harness.Scheduler.Flush();
        harness.Device.Complete(1);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal(7u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.False(future.Completed);
        Assert.True(harness.Pending.HasQueue(0));
    }

    [Fact]
    public void PendingLabelCannotPassWaitUsingOldMemoryValue()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var runner = harness.Runner;
        runner.Host.WriteDword(StreamRunner.LabelAddress, 7);
        var wait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 7, uint.MaxValue, 0);
        Assert.Equal(SubmissionProgress.Blocked, runner.Run(Label(StreamRunner.LabelAddress, 3), wait));
        harness.Device.Complete(1);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal(3u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal(SubmissionProgress.Blocked, runner.Run());
    }

    [Fact]
    public void WaitSeesPendingLabelWhileGuestMemoryAndDumpStayHidden()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var runner = harness.Runner;
        var labelWait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 7, uint.MaxValue, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Label(StreamRunner.LabelAddress, 7), labelWait));
        Assert.Equal(0u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        var dumpWait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress), 37, uint.MaxValue, 0);
        Assert.Equal(SubmissionProgress.Blocked, runner.Run(dumpWait));
        harness.Device.Complete(1);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal(7u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
    }

    [Fact]
    public void BatchedSignalsKeepOrderAroundLaterDump()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var runner = harness.Runner;
        uint[] Interrupt(uint value) => StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x528, (1u << 29) | (2u << 24),
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), value, 0, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Interrupt(1), Interrupt(2)));
        harness.AddDump(52);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Interrupt(3)));
        harness.Scheduler.Flush();
        harness.Device.Complete(harness.Scheduler.CurrentTick);
        harness.Scheduler.RunCompletedOperations();
        const ulong ready = 1UL << 63;
        Assert.Equal([ready | 37, ready | 37, ready | 52], harness.Observed);
        Assert.Equal(3u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal(0, harness.Pending.Count);
    }

    [Fact]
    public void OtherQueueWaitOnPendingLabelKeepsItsLaterSignalsBehindIt()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var graphics = harness.Runner;
        var compute = new StreamRunner(33, graphics.Host);
        var label = StreamRunner.LabelAddress;
        var completion = StreamRunner.LabelAddress + 0x100;
        Assert.Equal(SubmissionProgress.Complete, graphics.Run(Label(label, 1)));
        var wait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(label), StreamRunner.High(label), 1, uint.MaxValue, 0);
        var interrupt = StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x528, (1u << 29) | (2u << 24),
            StreamRunner.Low(completion), StreamRunner.High(completion), 5, 0, 0);
        // The wait passes at once, but the compute queue's signal must follow the graphics label.
        Assert.Equal(SubmissionProgress.Complete, compute.Run(wait, interrupt));
        Assert.Empty(graphics.Host.EndOfPipeWrites);
        Assert.Equal(0u, graphics.Host.ReadDword(completion));
        Assert.True(harness.Pending.HasQueue(33));
        Assert.Empty(harness.Observed);
        harness.Scheduler.Flush();
        harness.Device.Complete(harness.Scheduler.CurrentTick);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal([(1UL << 63) | 37], harness.Observed);
        Assert.Equal(1u, graphics.Host.ReadDword(label));
        Assert.Equal(5u, graphics.Host.ReadDword(completion));
        Assert.Equal(0, harness.Pending.Count);
    }

    [Fact]
    public void OlderPendingEntryDoesNotReplaceOrderingAfterForeignLabel()
    {
        using var harness = new DeferredSignalHarness();
        var graphics = harness.Runner;
        var compute = new StreamRunner(33, graphics.Host);
        // The compute queue's older entry publishes on an earlier tick than the graphics label.
        var older = harness.Pending.Add(33, StreamRunner.DataAddress + 0x100, 8);
        var olderTick = harness.Scheduler.CurrentTick;
        harness.Pending.Register(older, olderTick);
        harness.Scheduler.QueueMemoryPublication(() => PresenterUnderTest.PresenterType
            .GetMethod("PublishGuestEntry", PresenterUnderTest.InstanceMembers)!
            .Invoke(harness.Presenter, [older, (Action)(() => graphics.Host.WriteQword(StreamRunner.DataAddress + 0x100, 5))]));
        harness.Scheduler.Flush();
        harness.AddDump();
        harness.Scheduler.Flush();
        var label = StreamRunner.LabelAddress;
        Assert.Equal(SubmissionProgress.Complete, graphics.Run(Label(label, 1)));
        Assert.Equal(SubmissionProgress.Complete, compute.Run(StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(label), StreamRunner.High(label), 1, uint.MaxValue, 0)));
        harness.Device.Complete(olderTick);
        harness.Scheduler.RunCompletedOperations();
        Assert.True(older.Completed);
        var completion = StreamRunner.LabelAddress + 0x100;
        Assert.Equal(SubmissionProgress.Complete, compute.Run(StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x528,
            (1u << 29) | (2u << 24), StreamRunner.Low(completion), StreamRunner.High(completion), 9, 0, 0)));
        Assert.Empty(graphics.Host.EndOfPipeWrites);
        Assert.Empty(harness.Observed);
        harness.Scheduler.Flush();
        harness.Device.Complete(harness.Scheduler.CurrentTick);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal([(1UL << 63) | 37], harness.Observed);
        Assert.Equal(9u, graphics.Host.ReadDword(completion));
        Assert.Equal(0, harness.Pending.Count);
    }

    private static (DeferredSignalHarness Harness, StreamRunner Graphics, StreamRunner Compute) CrossQueueWindow()
    {
        var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var graphics = harness.Runner;
        var compute = new StreamRunner(33, graphics.Host);
        var label = StreamRunner.LabelAddress;
        Assert.Equal(SubmissionProgress.Complete, graphics.Run(Label(label, 1)));
        Assert.Equal(SubmissionProgress.Complete, compute.Run(StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(label), StreamRunner.High(label), 1, uint.MaxValue, 0)));
        return (harness, graphics, compute);
    }

    private static void PublishAll(DeferredSignalHarness harness)
    {
        harness.Scheduler.Flush();
        harness.Device.Complete(harness.Scheduler.CurrentTick);
        harness.Scheduler.RunCompletedOperations();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriteAfterForeignLabelWaitIsHeldUntilTheLabelPublishes(bool forward)
    {
        var (harness, graphics, compute) = CrossQueueWindow();
        using var owner = harness;
        harness.Pending.ForwardHeldWrites = forward;
        var flag = StreamRunner.LabelAddress + 0x200;
        Assert.Equal(SubmissionProgress.Complete, compute.Run(
            StreamRunner.Packet(PacketOpcode.WriteData, 0x00000500u, StreamRunner.Low(flag), StreamRunner.High(flag), 5)));
        Assert.Equal(0u, graphics.Host.ReadDword(flag));
        Assert.True(harness.Pending.HasQueue(33));
        // A later wait on the held-back value never reads the old memory: it takes the held value, or without
        // forwarding waits for the publication. The CPU reads the old memory until the publication either way.
        var wait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(flag), StreamRunner.High(flag), 5, uint.MaxValue, 0);
        Assert.Equal(forward ? SubmissionProgress.Complete : SubmissionProgress.Blocked, compute.Run(wait));
        Assert.Equal(0u, graphics.Host.ReadDword(flag));
        PublishAll(harness);
        Assert.Equal(1u, graphics.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal(5u, graphics.Host.ReadDword(flag));
        Assert.Equal((1UL << 63) | 37, graphics.Host.ReadQword(StreamRunner.DataAddress));
        Assert.Equal(SubmissionProgress.Complete, compute.Run());
        Assert.Equal(0, harness.Pending.Count);
    }

    // Another queue takes a held write's value at once and keeps its own later write behind it: the CPU sees neither
    // before the foreign label publishes, then both.
    [Fact]
    public void ForeignWaitTakesHeldWriteAndHoldsItsOwnWriteBehindIt()
    {
        var (harness, graphics, compute) = CrossQueueWindow();
        using var owner = harness;
        var flag = StreamRunner.LabelAddress + 0x200;
        var reply = StreamRunner.LabelAddress + 0x280;
        Assert.Equal(SubmissionProgress.Complete, compute.Run(
            StreamRunner.Packet(PacketOpcode.WriteData, 0x00000500u, StreamRunner.Low(flag), StreamRunner.High(flag), 5)));
        Assert.Equal(SubmissionProgress.Complete, graphics.Run(
            StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13, StreamRunner.Low(flag), StreamRunner.High(flag), 5, uint.MaxValue, 0),
            StreamRunner.Packet(PacketOpcode.WriteData, 0x00000500u, StreamRunner.Low(reply), StreamRunner.High(reply), 9)));
        Assert.Equal(0u, graphics.Host.ReadDword(flag));
        Assert.Equal(0u, graphics.Host.ReadDword(reply));
        PublishAll(harness);
        Assert.Equal(5u, graphics.Host.ReadDword(flag));
        Assert.Equal(9u, graphics.Host.ReadDword(reply));
        Assert.Equal(0, harness.Pending.Count);
    }

    [Fact]
    public void ReadModifyWriteAfterForeignLabelWaitSuspendsUntilTheLabelPublishes()
    {
        var (harness, graphics, compute) = CrossQueueWindow();
        using var owner = harness;
        var semaphore = StreamRunner.LabelAddress + 0x300;
        var signal = StreamRunner.Packet(PacketOpcode.MemorySemaphore, StreamRunner.Low(semaphore), StreamRunner.High(semaphore), 6u << 29);
        Assert.Equal(SubmissionProgress.Blocked, compute.Run(signal));
        Assert.Equal(0ul, graphics.Host.ReadQword(semaphore));
        PublishAll(harness);
        Assert.Equal(SubmissionProgress.Complete, compute.Run());
        Assert.NotEqual(0ul, graphics.Host.ReadQword(semaphore));
    }

    [Fact]
    public void SameQueueWaitOnPendingLabelAddsNoOrderingEntry()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var runner = harness.Runner;
        var wait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 7, uint.MaxValue, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Label(StreamRunner.LabelAddress, 7), wait));
        Assert.Equal(2, harness.Pending.Count);
    }

    [Fact]
    public void GpuCompletionLabelWaitsForItsTickWithoutAnEarlierDump()
    {
        using var harness = new DeferredSignalHarness();
        var runner = harness.Runner;
        runner.Interpreter.CompletionLabelsRequireGpu = true;
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Label(StreamRunner.LabelAddress, 7)));
        Assert.Equal(0u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal(1, harness.Pending.Count);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal(0u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        harness.Scheduler.Flush();
        harness.Device.Complete(1);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal(7u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal(0, harness.Pending.Count);
    }

    [Fact]
    public void LaterOverlappingLabelWinsAndUnrelatedQueueStaysImmediate()
    {
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var runner = harness.Runner;
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Label(StreamRunner.LabelAddress, 3), Label(StreamRunner.LabelAddress, 9)));
        var host = (ICommandStreamHost)harness.Presenter;
        Assert.False(host.TryDeferEndOfPipe(1, new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 1, StreamRunner.LabelAddress + 8, 5)));
        harness.Device.Complete(1);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal(9u, runner.Host.ReadDword(StreamRunner.LabelAddress));
        Assert.Equal(0, harness.Pending.Count);
    }

    [Fact]
    public void FailedLabelDoesNotDeliverInterruptOrLeavePendingEntry()
    {
        using var fatal = new FatalScope();
        using var harness = new DeferredSignalHarness();
        harness.AddDump();
        harness.Scheduler.Flush();
        var host = (ICommandStreamHost)harness.Presenter;
        Assert.True(host.TryDeferEndOfPipe(0, new EndOfPipeWrite(EndOfPipeWriteKind.Interrupt32, 1, 0x1234, 7)));
        harness.Scheduler.Flush();
        harness.Device.Complete(2);
        Assert.Throws<SchedulerFatalException>(harness.Scheduler.RunCompletedOperations);
        Assert.Empty(harness.Observed);
        Assert.Equal(0, harness.Pending.Count);
        Assert.Throws<InvalidOperationException>(() => harness.Pending.HasQueue(0));
    }

    [Fact]
    public void CompletionWakesBlockedQueueWithoutRetryTimeout()
    {
        using var harness = new DeferredSignalHarness();
        var runner = harness.Runner;
        harness.AddDump();
        harness.Scheduler.Flush();
        runner.Load(StreamRunner.Packet(PacketOpcode.WaitRegisterMemory,
            0x13, StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress), 37, uint.MaxValue, 0));
        harness.Queue.EnqueueGraphics(StreamRunner.CommandAddress, runner.Dwords, 1, null);
        harness.Queue.ProcessOne();
        Assert.False(harness.Queue.HasUnblockedPending);
        harness.Device.Complete(1);
        harness.Scheduler.RunCompletedOperations();
        Assert.True(harness.Queue.HasUnblockedPending);
        Assert.Equal(SliceResult.Completed, harness.Queue.ProcessOne());
    }

    [Fact]
    public void CompletionDuringSuspendCannotLoseWakeup()
    {
        using var harness = new DeferredSignalHarness();
        var runner = harness.Runner;
        runner.Host.DeferredLabelRead = null;
        runner.Host.PendingMemoryPublication = (_, _) =>
        {
            harness.Queue.NotifyPublicationCompleted();
            return true;
        };
        runner.Load(StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 7, uint.MaxValue, 0));
        harness.Queue.EnqueueGraphics(StreamRunner.CommandAddress, runner.Dwords, 1, null);
        harness.Queue.ProcessOne();
        Assert.True(harness.Queue.HasUnblockedPending);
        runner.Host.PendingMemoryPublication = (_, _) => false;
        runner.Host.WriteDword(StreamRunner.LabelAddress, 7);
        Assert.Equal(SliceResult.Completed, harness.Queue.ProcessOne());
    }

    // A deferred label of queue 1 with nothing else pending in that queue.
    private static PendingGuestPublications.Entry ForeignLabel(DeferredSignalHarness harness)
    {
        var ordering = harness.Pending.AddOrdering(1);
        var label = harness.Pending.AddAfterPending(1, 0x6000, 8, 9)!;
        harness.Pending.Publish(ordering, static () => { });
        return label;
    }

    // A cross-queue window whose foreign label publishes between MustHoldForCrossQueueOrder's window check and its wake
    // arm. A later entry keeps the foreign queue pending, so the publication saw no armed wake and woke nobody; the method
    // must not report a hold that nothing will wake.
    [Fact]
    public void ACrossQueueWindowCompletedBeforeTheArmIsNotHeld()
    {
        using var harness = new DeferredSignalHarness();
        var foreign = ForeignLabel(harness);
        _ = harness.Pending.AddAfterPending(1, 0x6008, 4, 1);
        harness.Set("_crossQueueWindows", new Dictionary<int, PendingGuestPublications.Entry> { [0] = foreign });
        harness.Set("CrossQueueHoldCheckedForTest", (Action)(() => harness.PublishDeferredSignal(foreign)));
        var held = ((ICommandStreamHost)harness.Presenter).MustHoldForCrossQueueOrder(0);
        Assert.True(foreign.Completed);
        Assert.True(harness.Pending.HasQueue(1));
        Assert.False(held);
    }

    // A window whose foreign label is still pending holds the queue and arms the wake its publication will take.
    [Fact]
    public void APendingCrossQueueWindowHoldsAndArmsItsWake()
    {
        using var harness = new DeferredSignalHarness();
        var foreign = ForeignLabel(harness);
        harness.Set("_crossQueueWindows", new Dictionary<int, PendingGuestPublications.Entry> { [0] = foreign });
        Assert.True(((ICommandStreamHost)harness.Presenter).MustHoldForCrossQueueOrder(0));
        Assert.Equal(1, (int)harness.Get("_partialLabelReadBlocked")!);
    }

    // A window whose foreign label already published holds nothing and arms nothing.
    [Fact]
    public void ACompletedCrossQueueWindowIsNotHeld()
    {
        using var harness = new DeferredSignalHarness();
        var foreign = ForeignLabel(harness);
        harness.Set("_crossQueueWindows", new Dictionary<int, PendingGuestPublications.Entry> { [0] = foreign });
        harness.PublishDeferredSignal(foreign);
        Assert.False(((ICommandStreamHost)harness.Presenter).MustHoldForCrossQueueOrder(0));
        Assert.Equal(0, (int)harness.Get("_partialLabelReadBlocked")!);
    }

    // A publication after the arm takes the armed wake, even while a later entry keeps its queue pending.
    [Fact]
    public void APublicationAfterTheArmTakesTheWake()
    {
        using var harness = new DeferredSignalHarness();
        var foreign = ForeignLabel(harness);
        _ = harness.Pending.AddAfterPending(1, 0x6008, 4, 1);
        harness.Set("_crossQueueWindows", new Dictionary<int, PendingGuestPublications.Entry> { [0] = foreign });
        Assert.True(((ICommandStreamHost)harness.Presenter).MustHoldForCrossQueueOrder(0));
        harness.PublishDeferredSignal(foreign);
        Assert.True(foreign.Completed);
        Assert.Equal(0, (int)harness.Get("_partialLabelReadBlocked")!);
    }
}

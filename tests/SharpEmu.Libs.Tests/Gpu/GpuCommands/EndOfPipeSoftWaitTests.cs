// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

// SHARPEMU_END_OF_PIPE_SOFT_WAITS: a wait met by another queue's ordinary end-of-pipe label suspends like an unmet wait
// while that release has not completed on the GPU and another head may run, and passes unchanged otherwise.
[Collection(CommandStreamQueueStateCollection.Name)]
public sealed class EndOfPipeSoftWaitTests
{
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Graphics = StreamRunner.CommandAddress;
    private const ulong ComputeA = StreamRunner.DataAddress;
    private const ulong ComputeB = StreamRunner.DataAddress + 0x800;
    private const ulong Nested = StreamRunner.TableAddress;
    private const uint FirstCompute = 0x20;

    private static uint[] Release32(ulong label, uint value) =>
        StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x28u | (5u << 8), 1u << 29,
            StreamRunner.Low(label), StreamRunner.High(label), value, 0, 0);

    private static uint[] Release64(ulong label, ulong value) =>
        StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x28u | (5u << 8), 2u << 29,
            StreamRunner.Low(label), StreamRunner.High(label), StreamRunner.Low(value), StreamRunner.High(value), 0);

    private static uint[] ReleaseInterrupt32(ulong label, uint value) =>
        StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x28u | (5u << 8), (2u << 24) | (1u << 29),
            StreamRunner.Low(label), StreamRunner.High(label), value, 0, 0);

    private static uint[] Wait32(uint function, ulong address, uint reference, uint mask = 0xFFFF_FFFFu) =>
        StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, function | 0x10u,
            StreamRunner.Low(address), StreamRunner.High(address), reference, mask, 0);

    private static uint[] Wait64(uint function, ulong address, ulong reference, ulong mask = ulong.MaxValue) =>
        StreamRunner.Packet(PacketOpcode.WaitRegisterMemory64, function | 0x10u,
            StreamRunner.Low(address), StreamRunner.High(address), StreamRunner.Low(reference), StreamRunner.High(reference),
            StreamRunner.Low(mask), StreamRunner.High(mask), 0);

    private static uint[] Draw(uint vertices) => StreamRunner.Packet(PacketOpcode.DrawIndexAuto, vertices, 0);

    private static uint[] Instances(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static uint[] IndirectBuffer(ulong address, uint dwords) =>
        StreamRunner.Packet(PacketOpcode.IndirectBuffer, StreamRunner.Low(address), StreamRunner.High(address) & 0xFFFF, dwords);

    private sealed record Setup(RecordingCommandStreamHost Host, CommandStreamQueue Queue, EndOfPipeLandings Landings);

    private static Setup NewQueue(long capTicks = long.MaxValue, bool soft = true)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        var landings = new EndOfPipeLandings { CapTicks = capTicks };
        // Set either way: unless SHARPEMU_END_OF_PIPE_SOFT_WAITS=0, the queue starts with its own registry.
        queue.Landings = soft ? landings : null;
        return new Setup(host, queue, landings);
    }

    private static void Graphics0(Setup setup, ulong submission, params uint[][] packets)
    {
        var words = StreamRunner.Concat(packets);
        setup.Host.WriteWords(Graphics, words);
        setup.Queue.EnqueueGraphics(Graphics, (uint)words.Length, submission, null);
    }

    private static void Compute(Setup setup, uint queue, ulong address, ulong submission, params uint[][] packets)
    {
        var words = StreamRunner.Concat(packets);
        setup.Host.WriteWords(address, words);
        setup.Queue.EnqueueCompute(queue, address, (uint)words.Length, submission, null);
    }

    // The releasing compute queue runs first, so its label is in memory and its landing is not complete.
    private static EndOfPipeLanding Published(Setup setup, params uint[][] release)
    {
        Compute(setup, FirstCompute, ComputeA, 1, release);
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        return Assert.IsType<EndOfPipeLanding>(setup.Host.EndOfPipeWrites[^1].Landing);
    }

    // After the release, queue 1 (the releasing compute queue) gets another head behind the graphics head in the
    // round-robin order, so the graphics wait runs while another head may run.
    private static void OtherWork(Setup setup) => Compute(setup, FirstCompute, ComputeB, 2, Instances(5));

    private static int Draws(Setup setup) => setup.Host.Calls.Count(call => call.StartsWith("draw_auto", StringComparison.Ordinal));

    private static void RunUntilIdle(Setup setup)
    {
        for (var slices = 0; slices < 16; slices++)
        {
            if (setup.Queue.ProcessOne() is SliceResult.NoWork) return;
        }

        Assert.Fail("The queue did not go idle.");
    }

    [Fact]
    public void MetWaitYieldsToAnotherHeadThenRunsOnceWhenNoneIsLeft()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(setup);

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        Assert.Equal(0, Draws(setup));
        // A soft wait is not a blocked wait: the batch policy keeps its own rule.
        Assert.Equal(0, setup.Queue.BlockedQueueCount);

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(5u, setup.Queue.GetInterpreter(1).InstanceCount);
        Assert.Equal(0, Draws(setup));

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
        Assert.Equal(EndOfPipeLandings.Release.Idle, landing.ReleasedBy);
        Assert.Equal(SliceResult.NoWork, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
    }

    [Fact]
    public void SoftWaitPassesOnceItsReleaseCompletesOnTheGpu()
    {
        var setup = NewQueue();
        setup.Host.EndOfPipeRecorded = write => write.Landing?.Recorded(7);
        var landing = Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(setup);
        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());

        setup.Queue.NotifyCompletedGpuTick(7);
        RunUntilIdle(setup);

        Assert.Equal(1, Draws(setup));
        Assert.Equal(EndOfPipeLandings.Release.Complete, landing.ReleasedBy);
    }

    [Fact]
    public void WithoutAnotherHeadTheMetWaitPassesAtOnce()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
        Assert.Equal(EndOfPipeLandings.Release.Idle, landing.ReleasedBy);
    }

    [Fact]
    public void SwitchOffKeepsTheOriginalOrder()
    {
        var setup = NewQueue(soft: false);
        Compute(setup, FirstCompute, ComputeA, 1, Release32(Label, 1));
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Null(setup.Host.EndOfPipeWrites[^1].Landing);
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(setup);

        // The graphics head runs first, as without the switch, and the other head after it.
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
        Assert.NotEqual(5u, setup.Queue.GetInterpreter(1).InstanceCount);
    }

    [Theory]
    [InlineData(5u, 0xFFFF_FFFFu, 1u)] // greater-or-equal
    [InlineData(3u, 0x0000_FFFFu, 1u)] // partial mask
    public void OnlyFullMaskEqualityWaitsAreAdmitted(uint function, uint mask, uint reference)
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(function, Label, reference, mask), Draw(7));
        OtherWork(setup);

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
        Assert.Null(landing.ReleasedBy);
    }

    [Fact]
    public void AWaitOfAnotherWidthIsNotAdmitted()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        setup.Host.WriteDword(Label + 4, 0);
        Graphics0(setup, 3, Wait64(3, Label, 1), Draw(7));
        OtherWork(setup);

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
        Assert.Null(landing.ReleasedBy);
    }

    [Fact]
    public void A64BitReleaseIsAdmittedForA64BitEqualityWait()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release64(Label, 0x1_0000_0002));
        Graphics0(setup, 3, Wait64(3, Label, 0x1_0000_0002), Draw(7));
        OtherWork(setup);

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        RunUntilIdle(setup);
        Assert.Equal(1, Draws(setup));
        Assert.Equal(EndOfPipeLandings.Release.Idle, landing.ReleasedBy);
    }

    [Fact]
    public void InterruptLabelsAndDeferredLabelsTakeNoHint()
    {
        var setup = NewQueue();
        Compute(setup, FirstCompute, ComputeA, 1, ReleaseInterrupt32(Label, 1));
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Null(setup.Host.EndOfPipeWrites[^1].Landing);
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(setup);
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));

        // A deferred label replaces the address's hint.
        var deferredSetup = NewQueue();
        Published(deferredSetup, Release32(Label, 1));
        deferredSetup.Host.DeferredEndOfPipe = (_, _) => true;
        Compute(deferredSetup, FirstCompute, ComputeB, 2, Release32(Label, 1));
        Assert.Equal(SliceResult.Completed, deferredSetup.Queue.ProcessOne());
        deferredSetup.Host.DeferredEndOfPipe = null;
        Graphics0(deferredSetup, 3, Wait32(3, Label, 1), Draw(7));
        Compute(deferredSetup, FirstCompute, ComputeA, 4, Instances(5));
        Assert.Equal(SliceResult.Completed, deferredSetup.Queue.ProcessOne());
        Assert.Equal(1, Draws(deferredSetup));
    }

    // A wait met through a deferred label's overlay value is not a met label in guest memory: no soft wait.
    [Fact]
    public void AWaitMetThroughADeferredLabelTakesNoSoftWait()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        setup.Host.DeferredLabelRead = (int _, ulong _, ulong _, out bool deferred, out ulong value) =>
        {
            deferred = true;
            value = 1;
            return true;
        };
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(setup);

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
        Assert.Null(landing.ReleasedBy);
    }

    [Fact]
    public void AQueuesOwnReleaseIsNoHint()
    {
        var setup = NewQueue();
        Compute(setup, FirstCompute + 1, ComputeB, 2, Instances(5));
        Graphics0(setup, 3, Release32(Label, 1), Wait32(3, Label, 1), Draw(7));

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
    }

    [Fact]
    public void ANewerReleaseReplacesTheHintAndAnOldTickDoesNotCompleteIt()
    {
        var setup = NewQueue();
        var older = Published(setup, Release32(Label, 1));
        var newer = Published(setup, Release32(Label, 2));
        older.Recorded(5);
        setup.Queue.NotifyCompletedGpuTick(5);
        Graphics0(setup, 3, Wait32(3, Label, 2), Draw(7));
        OtherWork(setup);

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        Assert.Equal(0, Draws(setup));
        RunUntilIdle(setup);
        Assert.Equal(EndOfPipeLandings.Release.Idle, newer.ReleasedBy);
        Assert.Null(older.ReleasedBy);
    }

    [Fact]
    public void TheDeadlineHoldsAcrossRetriesAndAUsedReleaseIsNotArmedAgain()
    {
        var setup = NewQueue(capTicks: Stopwatch.Frequency / 1000);
        var landing = Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7), Wait32(3, Label, 1), Draw(8));
        // Two other submissions on queue 1: the first runs while the wait is deferred, the second is still queued at the
        // deadline, so only the deadline lets the wait pass then.
        OtherWork(setup);
        Compute(setup, FirstCompute, ComputeA, 4, Instances(6));

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 500;
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(100);

        RunUntilIdle(setup);
        Assert.Equal(EndOfPipeLandings.Release.Cap, landing.ReleasedBy);
        // The second wait met the same, used release: it ran without a second soft wait.
        Assert.Equal(2, Draws(setup));
    }

    [Fact]
    public void AControlBarrierCutoffAndShutdownLeaveNoSoftWait()
    {
        var barrier = NewQueue();
        var landing = Published(barrier, Release32(Label, 1));
        Graphics0(barrier, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(barrier);
        Assert.True(barrier.Queue.TryEnqueueControlBarrier(static () => { }, static () => { }));
        RunUntilIdle(barrier);
        Assert.Equal(1, Draws(barrier));
        Assert.Equal(EndOfPipeLandings.Release.Idle, landing.ReleasedBy);

        var shutdown = NewQueue();
        var stopped = Published(shutdown, Release32(Label, 1));
        Graphics0(shutdown, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(shutdown);
        Assert.Equal(SliceResult.BlockedWithoutProgress, shutdown.Queue.ProcessOne());
        shutdown.Queue.StopAccepting();
        RunUntilIdle(shutdown);
        Assert.Equal(1, Draws(shutdown));
        Assert.Equal(EndOfPipeLandings.Release.Idle, stopped.ReleasedBy);
    }

    [Fact]
    public void ANestedSoftWaitResumesInsideItsBufferAndEveryPacketRunsOnce()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        var nested = StreamRunner.Concat(Wait32(3, Label, 1), Draw(9));
        setup.Host.WriteWords(Nested, nested);
        Graphics0(setup, 3, Instances(2), IndirectBuffer(Nested, (uint)nested.Length), Draw(7));
        OtherWork(setup);

        // The prefix before the wait made progress, so the slice ends with the usual flush.
        Assert.Equal(SliceResult.Progressed, setup.Queue.ProcessOne());
        Assert.Equal("flush", setup.Host.Calls[^1]);
        Assert.Equal(0, Draws(setup));

        RunUntilIdle(setup);
        Assert.Equal(new[] { "9", "7" },
            setup.Host.Calls.Where(call => call.StartsWith("draw_auto", StringComparison.Ordinal)).Select(call => call.Split(' ')[^1]).ToArray());
        Assert.Equal(EndOfPipeLandings.Release.Idle, landing.ReleasedBy);
    }

    // The guest's wait passed once; a later change of the label (another release to it) must not turn it back into an unmet
    // wait. The retry resumes only the soft delay.
    [Fact]
    public void ALaterLabelChangeDoesNotUndoAPassedWait()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        Compute(setup, FirstCompute, ComputeB, 2, Release32(Label, 2));

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(2u, setup.Host.ReadDword(Label));

        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(1, Draws(setup));
        Assert.Equal(EndOfPipeLandings.Release.Idle, landing.ReleasedBy);
    }

    // A dropped submission's soft wait ends when the queue runs another submission, even one at the same packet address:
    // that packet's wait is evaluated afresh (here it is unmet and blocks as usual).
    [Fact]
    public void ADroppedSoftWaitDoesNotCarryToALaterSubmissionAtTheSamePacket()
    {
        var setup = NewQueue();
        var landing = Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        OtherWork(setup);
        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());

        setup.Queue.DiscardAll();
        Graphics0(setup, 4, Wait32(3, Label, 3), Draw(8));

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        Assert.Equal(0, Draws(setup));
        Assert.Equal(1, setup.Queue.BlockedQueueCount);
        Assert.True(landing.Consumed);
        Assert.Null(landing.ReleasedBy);
    }

    // When every remaining head waits softly, one of them runs on (its wait passes) rather than the queue idling.
    [Fact]
    public void WhenEveryHeadWaitsSoftlyOneRunsOn()
    {
        var setup = NewQueue();
        var first = Published(setup, Release32(Label, 1));
        Compute(setup, FirstCompute + 1, ComputeB, 2, Release32(Label + 0x10, 1));
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        var second = Assert.IsType<EndOfPipeLanding>(setup.Host.EndOfPipeWrites[^1].Landing);
        Graphics0(setup, 3, Wait32(3, Label, 1), Draw(7));
        Compute(setup, FirstCompute, ComputeA, 4, Wait32(3, Label + 0x10, 1), Instances(5));
        Compute(setup, FirstCompute + 1, ComputeB + 0x200, 5, Instances(6));

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        Assert.Equal(SliceResult.Completed, setup.Queue.ProcessOne());
        Assert.Equal(6u, setup.Queue.GetInterpreter(2).InstanceCount);

        RunUntilIdle(setup);
        Assert.Equal(1, Draws(setup));
        Assert.Equal(5u, setup.Queue.GetInterpreter(1).InstanceCount);
        Assert.Equal(EndOfPipeLandings.Release.Idle, first.ReleasedBy);
        Assert.Equal(EndOfPipeLandings.Release.Idle, second.ReleasedBy);
    }

    [Fact]
    public void AnUnmetWaitIsStillBlocked()
    {
        var setup = NewQueue();
        Published(setup, Release32(Label, 1));
        Graphics0(setup, 3, Wait32(3, Label, 2), Draw(7));
        OtherWork(setup);

        Assert.Equal(SliceResult.BlockedWithoutProgress, setup.Queue.ProcessOne());
        Assert.Equal(1, setup.Queue.BlockedQueueCount);
    }
}

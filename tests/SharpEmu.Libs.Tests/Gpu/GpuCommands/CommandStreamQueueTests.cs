// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

[CollectionDefinition(CommandStreamQueueStateCollection.Name, DisableParallelization = true)]
public sealed class CommandStreamQueueStateCollection
{
    public const string Name = "CommandStreamQueueState";
}

[Collection(CommandStreamQueueStateCollection.Name)]
public sealed class CommandStreamQueueTests
{
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Graphics = StreamRunner.CommandAddress;
    private const ulong Compute = StreamRunner.DataAddress;

    private static uint[] CreateInstanceCountPacket(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static uint[] WaitEqual(ulong address, uint reference) =>
        StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u, StreamRunner.Low(address), StreamRunner.High(address), reference, 0xFFFF_FFFFu, 0);

    private static uint[] WriteLabel(ulong address, uint value) =>
        StreamRunner.Packet(PacketOpcode.WriteData, 0x00000500u, StreamRunner.Low(address), StreamRunner.High(address), value);

    private static (RecordingCommandStreamHost Host, CommandStreamQueue Queue) NewQueue()
    {
        var host = new RecordingCommandStreamHost();
        return (host, new CommandStreamQueue(host));
    }

    private static void Enqueue(RecordingCommandStreamHost host, CommandStreamQueue queue, ulong address, ulong submissionId, params uint[][] packets)
    {
        var words = StreamRunner.Concat(packets);
        host.WriteWords(address, words);
        queue.EnqueueGraphics(address, (uint)words.Length, submissionId, null);
    }

    private static void EnqueueCompute(RecordingCommandStreamHost host, CommandStreamQueue queue, uint computeQueue, ulong address, ulong submissionId, params uint[][] packets)
    {
        var words = StreamRunner.Concat(packets);
        host.WriteWords(address, words);
        queue.EnqueueCompute(computeQueue, address, (uint)words.Length, submissionId, null);
    }

    [Fact]
    public void GraphicsSubmission_RunsBeginsCollectsAndFlushes()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, Graphics, 7, CreateInstanceCountPacket(3));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());

        Assert.Equal(new[] { "begin 0 7", "gc", "flush" }, host.Calls);
        Assert.Equal(3u, queue.GetInterpreter(0).InstanceCount);
        Assert.Equal(1UL, queue.GetInterpreter(0).SubmitId);
        Assert.Equal(SliceResult.NoWork, queue.ProcessOne());
        Assert.Equal(IdleOutcome.Completed, queue.WaitForIdle());
    }

    private static uint[] ReleaseMemoryInterrupt(ulong label, uint value) =>
        StreamRunner.Packet(
            PacketOpcode.ReleaseMemory,
            0x28u | (5u << 8),
            (2u << 24) | (1u << 29),
            StreamRunner.Low(label), StreamRunner.High(label),
            value, 0,
            0);

    // Release-memory packets do not submit on their own; the slice end does, so the
    // interrupt they queue always reaches the GPU once the slice is over.
    [Fact]
    public void ReleaseMemoryInterrupt_IsSubmittedAtTheEndOfTheSlice()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, Graphics, 1, ReleaseMemoryInterrupt(Label, 5), CreateInstanceCountPacket(2), ReleaseMemoryInterrupt(Label, 6));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());

        Assert.Equal(new[] { "begin 0 1", "eop Interrupt32", "eop Interrupt32", "gc", "flush" }, host.Calls);
    }

    // A slice that blocks after a release still submits it, so a wait that depends on
    // the guest reacting to that interrupt cannot hold the interrupt back.
    [Fact]
    public void ReleaseMemoryInterrupt_IsSubmittedWhenTheSliceBlocks()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, Graphics, 1, ReleaseMemoryInterrupt(Label, 5), WaitEqual(Label + 8, 1));

        Assert.Equal(SliceResult.Progressed, queue.ProcessOne());

        var release = host.Calls.IndexOf("eop Interrupt32");
        Assert.True(release >= 0 && host.Calls.IndexOf("flush") > release, string.Join(", ", host.Calls));
    }

    // A video-out export flip captures after the draws submitted before it, never ahead of them.
    [Fact]
    public void FlipPreparation_RunsAfterTheGraphicsSubmissionsBeforeIt()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, Graphics, 1, StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 3, 0));
        Assert.True(queue.TryEnqueueFlipPreparation(1, 2, 77));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.NoWork, queue.ProcessOne());

        var draw = host.Calls.IndexOf("draw_auto 1 3");
        var flip = host.Calls.IndexOf("cpu_flip 1 2 77");
        Assert.True(draw >= 0 && flip > draw, string.Join(", ", host.Calls));
        Assert.Equal("flush", host.Calls[flip + 1]);

        queue.StopAccepting();
        Assert.False(queue.TryEnqueueFlipPreparation(1, 2, 78));
    }

    [Fact]
    public void SubmitIds_AreSharedAcrossQueuesAndEventIdsFollowTheQueue()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, Graphics, 1, CreateInstanceCountPacket(1));
        EnqueueCompute(host, queue, 0x21, Compute, 2, CreateInstanceCountPacket(1));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());

        Assert.Equal(1UL, queue.GetInterpreter(0).SubmitId);
        Assert.Equal(2UL, queue.GetInterpreter(2).SubmitId);
        Assert.Equal(0, queue.GetInterpreter(0).InterruptEventId);
        Assert.Equal(0x21, queue.GetInterpreter(2).InterruptEventId);
        Assert.Equal(new[] { "begin 0 1", "gc", "flush", "begin 2 2", "gc", "flush" }, host.Calls);
        Assert.Contains("compute queue is outside", Assert.Throws<CommandStreamFatalException>(() => queue.EnqueueCompute(0x58, Compute, 2, 3, null)).Message);
        Assert.Contains("compute submission is empty", Assert.Throws<CommandStreamFatalException>(() => queue.EnqueueCompute(0x20, Compute, 0, 3, null)).Message);
    }

    [Fact]
    public void Queues_AlternateRoundRobin()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, Graphics, 1, CreateInstanceCountPacket(1));
        Enqueue(host, queue, Graphics + 0x100, 2, CreateInstanceCountPacket(1));
        EnqueueCompute(host, queue, 0x20, Compute, 3, CreateInstanceCountPacket(1));

        for (var slice = 0; slice < 3; slice++)
        {
            Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        }

        Assert.Equal(new[] { "begin 0 1", "begin 1 3", "begin 0 2" }, host.Calls.Where(call => call.StartsWith("begin", StringComparison.Ordinal)));
    }

    [Fact]
    public void BlockedHead_IsRequeuedAndRetriedWithoutStoppingOtherQueues()
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        Enqueue(host, queue, Graphics, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(9));
        EnqueueCompute(host, queue, 0x20, Compute, 2, CreateInstanceCountPacket(5));

        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(1, queue.BlockedQueueCount);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(0, queue.BlockedQueueCount);
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(SliceResult.AllBlocked, queue.ProcessOne());

        host.WriteDword(Label, 1);
        queue.RetryBlocked();
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(9u, queue.GetInterpreter(0).InstanceCount);
        Assert.Equal(1UL, queue.BlockedRetries);
    }

    [Fact]
    public void CompletedGpuTick_RetriesBlockedWaitWithoutTheTimeout()
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        Enqueue(host, queue, Graphics, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(9));
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());

        queue.NotifyCompletedGpuTick(1);
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        host.WriteDword(Label, 1);
        queue.NotifyCompletedGpuTick(1);
        Assert.Equal(SliceResult.AllBlocked, queue.ProcessOne());

        queue.NotifyCompletedGpuTick(2);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(9u, queue.GetInterpreter(0).InstanceCount);
    }

    [Fact]
    public void PresentationProgress_RetriesBlockedFlipWait()
    {
        var (host, queue) = NewQueue();
        host.FlipDone = false;
        Enqueue(host, queue, Graphics, 1,
            StreamRunner.CustomPacket(PacketOpcode.Nop, PacketCustomCode.WaitFlipDone, 1, 0, 0, 0, 0, 0));
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        host.FlipDone = true;
        queue.RetryBlocked();
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
    }

    [Fact]
    public async Task WaitForIdle_IsNotReleasedByStopAccepting()
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        Enqueue(host, queue, Graphics, 1, WaitEqual(Label, 1));
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        var waiter = Task.Run(queue.WaitForIdle);

        queue.StopAccepting();
        await Task.WhenAny(waiter, Task.Delay(200));
        Assert.False(waiter.IsCompleted);
        Assert.Throws<OperationCanceledException>(() => queue.EnqueueGraphics(Graphics, 1, 2, null));
        Assert.Throws<OperationCanceledException>(() => queue.EnqueueCompute(GpuCommandInterpreter.ComputeQueueBase, Compute, 1, 3, null));

        host.WriteDword(Label, 1);
        queue.RetryBlocked();
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(IdleOutcome.Completed, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void DrainForShutdown_WaitsTheRetryIntervalBeforeCancelling()
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        Enqueue(host, queue, Graphics, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(9));
        queue.StopAccepting();
        var outcome = IdleOutcome.Cancelled;
        var drainThread = new Thread(() => outcome = queue.DrainForShutdown(cancelBlockedOnNoProgress: true));
        drainThread.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => (drainThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(2)));
            host.WriteDword(Label, 1);
        }
        finally
        {
            Assert.True(drainThread.Join(TimeSpan.FromSeconds(2)));
        }

        Assert.Equal(IdleOutcome.Completed, outcome);
        Assert.Equal(9u, queue.GetInterpreter(0).InstanceCount);
    }

    [Fact]
    public void DrainForShutdown_CancelsOnlyHeadsThatMakeNoProgressAndTheOutcomeSticks()
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        Enqueue(host, queue, Graphics, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(9));
        Enqueue(host, queue, Graphics + 0x100, 2, CreateInstanceCountPacket(4));
        EnqueueCompute(host, queue, 0x20, Compute, 3, CreateInstanceCountPacket(5));
        queue.StopAccepting();

        var outcome = queue.DrainForShutdown(cancelBlockedOnNoProgress: true);

        Assert.Equal(IdleOutcome.Cancelled, outcome);
        Assert.Equal(5u, queue.GetInterpreter(1).InstanceCount);
        Assert.Equal(1u, queue.GetInterpreter(0).InstanceCount);
        Assert.False(queue.HasPending);
        Assert.Equal(IdleOutcome.Cancelled, queue.WaitForIdle());
        Assert.Equal(IdleOutcome.Cancelled, queue.Outcome);
    }

    [Fact]
    public async Task DrainForShutdown_WithoutCancellationKeepsRetrying()
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        Enqueue(host, queue, Graphics, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(9));
        queue.StopAccepting();
        var drain = Task.Run(() => queue.DrainForShutdown(cancelBlockedOnNoProgress: false));

        await Task.WhenAny(drain, Task.Delay(350));
        Assert.False(drain.IsCompleted);
        host.WriteDword(Label, 1);
        Assert.Equal(IdleOutcome.Completed, await drain.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(9u, queue.GetInterpreter(0).InstanceCount);
    }

    [Fact]
    public async Task HostFailure_MarksTheQueueFailedAndReleasesWaiters()
    {
        var (host, queue) = NewQueue();
        host.WriteWords(Graphics, StreamRunner.Packet(0x41, 0));
        queue.EnqueueGraphics(Graphics, 2, 1, null);
        var waiter = Task.Run(queue.WaitForIdle);

        Assert.Throws<CommandStreamFatalException>(() => queue.ProcessOne());

        Assert.Equal(IdleOutcome.Failed, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(queue.HasPending);
        Assert.Contains("no longer accepts", Assert.Throws<CommandStreamFatalException>(() => queue.EnqueueGraphics(Graphics, 2, 2, null)).Message);
    }

    [Fact]
    public async Task ErrorWriterFailure_PreservesTheHostExceptionAndReleasesWaiters()
    {
        var (host, queue) = NewQueue();
        var hostException = new CommandStreamFatalException("The command stream host failed.");
        host.PendingCommands.Enqueue(() => throw hostException);
        Enqueue(host, queue, Graphics, 1, CreateInstanceCountPacket(1));
        Enqueue(host, queue, Graphics + 0x100, 2, CreateInstanceCountPacket(2));
        var waiter = Task.Run(queue.WaitForIdle);
        var previousErrorWriter = Console.Error;
        using var failingErrorWriter = new FailingErrorWriter();

        try
        {
            Exception? actualException;
            try
            {
                Console.SetError(failingErrorWriter);
                actualException = Record.Exception(() => queue.ProcessOne());
            }
            finally
            {
                Console.SetError(previousErrorWriter);
            }

            Assert.True(failingErrorWriter.WriteAttempted);
            Assert.Same(hostException, actualException);
            Assert.Equal(IdleOutcome.Failed, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(queue.HasPending);
            Assert.Contains("no longer accepts", Assert.Throws<CommandStreamFatalException>(() =>
                queue.EnqueueGraphics(Graphics, 2, 3, null)).Message);
        }
        finally
        {
            queue.Fail();
            await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class FailingErrorWriter : StringWriter
    {
        public bool WriteAttempted { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteAttempted = true;
            throw new IOException("The error output is unavailable.");
        }
    }

    [Fact]
    public async Task Done_QueuesTheBoundaryAndOnlyWaitsForASecondOne()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host, boundariesInFlight: 1);
        Enqueue(host, queue, Graphics, 1, CreateInstanceCountPacket(1));

        // The first suspend point returns without draining the accepted submissions.
        Assert.Equal(IdleOutcome.Completed, await Task.Run(queue.Done).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, queue.FrameNumber);
        Assert.True(queue.HasPending);

        // The second one waits until the first boundary has been processed.
        var second = Task.Run(queue.Done);
        await Task.WhenAny(second, Task.Delay(100));
        Assert.False(second.IsCompleted);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(IdleOutcome.Completed, await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, queue.FrameNumber);

        // The next graphics submission starts from a reset processor.
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(2, queue.FrameNumber);
        Enqueue(host, queue, Graphics, 2, CreateInstanceCountPacket(2));
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(2UL, queue.GetInterpreter(0).SubmitId);
    }

    [Fact]
    public async Task Done_WaitsForItsOwnBoundaryWithoutRunAhead()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host, boundariesInFlight: 0);
        Enqueue(host, queue, Graphics, 1, CreateInstanceCountPacket(1));

        var done = Task.Run(queue.Done);
        await Task.WhenAny(done, Task.Delay(100));
        Assert.False(done.IsCompleted);

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        await Task.WhenAny(done, Task.Delay(100));
        Assert.False(done.IsCompleted);

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(IdleOutcome.Completed, await done.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, queue.FrameNumber);
        Assert.False(queue.HasPending);
    }

    [Fact]
    public async Task Done_KeepsALabelRewrittenAfterItAwayFromAnUnfinishedFrame()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host, boundariesInFlight: 0);
        void WriteLabel(uint value) => Assert.True(host.GuestMemory.TryWrite(StreamRunner.DataAddress, BitConverter.GetBytes(value)));
        WriteLabel(0);
        Enqueue(host, queue, Graphics, 1,
            StreamRunner.Packet(PacketOpcode.WriteData, 0x00000500u, StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress), 1),
            WaitEqual(StreamRunner.DataAddress, 1));

        var guest = Task.Run(() =>
        {
            var outcome = queue.Done();
            WriteLabel(0);
            return outcome;
        });

        await Task.WhenAny(guest, Task.Delay(100));
        Assert.False(guest.IsCompleted);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(IdleOutcome.Completed, await guest.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairedLabelWait_SurvivesTheNextFramesResetWithRunAhead(bool is64Bit)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host, boundariesInFlight: 1);
        var packets = new List<uint[]>();
        for (var pair = 0; pair < 7; pair++)
        {
            var address = Label + (ulong)pair * 8;
            packets.Add(is64Bit
                ? StreamRunner.Packet(PacketOpcode.WriteData, 0x00000500u,
                    StreamRunner.Low(address), StreamRunner.High(address), 1, 2)
                : WriteLabel(address, 1));
            packets.Add(is64Bit
                ? StreamRunner.Packet(PacketOpcode.WaitRegisterMemory64, 0x13u,
                    StreamRunner.Low(address), StreamRunner.High(address), 1, 2, uint.MaxValue, uint.MaxValue, 0)
                : WaitEqual(address, 1));
        }
        Enqueue(host, queue, Graphics, 1, packets.ToArray());
        host.BeforeGuestRead = address =>
        {
            if (address == Label + 6 * 8) host.WriteQword(address, 0);
        };

        Assert.Equal(IdleOutcome.Completed, await Task.Run(queue.Done).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, queue.FrameNumber); // CPU/GPU overlap remains enabled.
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(0UL, host.ReadQword(Label + 6 * 8)); // The CPU reset was not overwritten.
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(1, queue.FrameNumber);
        Assert.Equal(0, queue.BlockedQueueCount);
    }

    [Theory]
    [InlineData(0)] // A CPU-produced label must still be read from live memory.
    [InlineData(1)] // Never forward a value from the preceding submission.
    [InlineData(2)] // Never forward through work that may write the label on the GPU.
    public void LabelWait_DoesNotForwardAnUnrelatedOrOlderStore(int scenario)
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        if (scenario == 0)
        {
            Enqueue(host, queue, Graphics, 1, WriteLabel(Label + 8, 1), WaitEqual(Label, 1));
        }
        else if (scenario == 1)
        {
            Enqueue(host, queue, Graphics, 1, WriteLabel(Label, 1));
            Assert.Equal(SliceResult.Completed, queue.ProcessOne());
            host.WriteDword(Label, 0);
            Enqueue(host, queue, Graphics + 0x100, 2, WaitEqual(Label, 1));
        }
        else
        {
            Enqueue(host, queue, Graphics, 1, WriteLabel(Label, 1),
                StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 3, 0), WaitEqual(Label, 1));
            host.BeforeGuestRead = address =>
            {
                if (address == Label) host.WriteDword(Label, 0);
            };
        }
        Assert.Equal(scenario == 1 ? SliceResult.BlockedWithoutProgress : SliceResult.Progressed, queue.ProcessOne());
        host.BeforeGuestRead = null;
        host.WriteDword(Label, 1);
        queue.RetryBlocked();
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
    }
}

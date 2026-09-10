// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class CommandStreamQueueTests
{
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Graphics = StreamRunner.CommandAddress;
    private const ulong Compute = StreamRunner.DataAddress;

    private static uint[] CreateInstanceCountPacket(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static uint[] WaitEqual(ulong address, uint reference) =>
        StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u, StreamRunner.Low(address), StreamRunner.High(address), reference, 0xFFFF_FFFFu, 0);

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
    public async Task DrainForShutdown_WaitsTheRetryIntervalBeforeCancelling()
    {
        var (host, queue) = NewQueue();
        host.WriteDword(Label, 0);
        Enqueue(host, queue, Graphics, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(9));
        queue.StopAccepting();
        var producer = Task.Run(async () =>
        {
            await Task.Delay(30);
            host.WriteDword(Label, 1);
        });

        var outcome = queue.DrainForShutdown(cancelBlockedOnNoProgress: true);

        await producer;
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
    public async Task Done_FromAnotherThreadWaitsForTheQueueAndCountsFrames()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, Graphics, 1, CreateInstanceCountPacket(1));
        var done = Task.Run(queue.Done);

        await Task.WhenAny(done, Task.Delay(100));
        Assert.False(done.IsCompleted);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(IdleOutcome.Completed, await done.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, queue.FrameNumber);

        // The next graphics submission starts from a reset processor.
        Enqueue(host, queue, Graphics, 2, CreateInstanceCountPacket(2));
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(2UL, queue.GetInterpreter(0).SubmitId);
    }
}

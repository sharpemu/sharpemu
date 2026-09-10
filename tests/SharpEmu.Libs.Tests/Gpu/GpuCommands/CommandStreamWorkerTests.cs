// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class CommandStreamWorkerTests
{
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Graphics = StreamRunner.CommandAddress;

    private static uint[] CreateInstanceCountPacket(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static uint[] WaitEqual(ulong address, uint reference) =>
        StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u, StreamRunner.Low(address), StreamRunner.High(address), reference, 0xFFFF_FFFFu, 0);

    private static (RecordingCommandStreamHost Host, CommandStreamQueue Queue, CommandStreamWorker Worker) NewWorker(bool cancelBlockedAtStop)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        var worker = new CommandStreamWorker(queue, host, () => host.PendingCommands.Count != 0, cancelBlockedAtStop, "test command stream");
        worker.Start();
        return (host, queue, worker);
    }

    private static void Enqueue(RecordingCommandStreamHost host, CommandStreamQueue queue, ulong submissionId, params uint[][] packets)
    {
        var words = StreamRunner.Concat(packets);
        host.WriteWords(Graphics, words);
        queue.EnqueueGraphics(Graphics, (uint)words.Length, submissionId, null);
    }

    [Fact]
    public void Worker_ProcessesSubmissionsAndStopsWhenEmpty()
    {
        var (host, queue, worker) = NewWorker(cancelBlockedAtStop: false);

        Enqueue(host, queue, 1, CreateInstanceCountPacket(4));

        Assert.Equal(IdleOutcome.Completed, queue.WaitForIdle());
        Assert.Equal(IdleOutcome.Completed, worker.Stop());
        Assert.Equal(4u, queue.GetInterpreter(0).InstanceCount);
        Assert.Contains("synchronize", host.Calls);
    }

    [Fact]
    public void Worker_RetriesABlockedHeadAfterTheTimedWait()
    {
        var (host, queue, worker) = NewWorker(cancelBlockedAtStop: false);
        host.WriteDword(Label, 0);

        Enqueue(host, queue, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(6));
        Thread.Sleep(50);
        host.WriteDword(Label, 1);

        Assert.Equal(IdleOutcome.Completed, queue.WaitForIdle());
        Assert.Equal(6u, queue.GetInterpreter(0).InstanceCount);
        Assert.True(queue.BlockedRetries >= 1);
        worker.Stop();
    }

    [Fact]
    public async Task Stop_CancelsAPermanentlyBlockedHeadAndReportsIt()
    {
        var (host, queue, worker) = NewWorker(cancelBlockedAtStop: true);
        host.WriteDword(Label, 0);
        Enqueue(host, queue, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(6));
        var waiter = Task.Run(queue.WaitForIdle);

        Assert.Equal(IdleOutcome.Cancelled, worker.Stop());
        Assert.Equal(IdleOutcome.Cancelled, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1u, queue.GetInterpreter(0).InstanceCount);
    }

    [Fact]
    public async Task FailureOutsideASlice_FailsTheQueueAndReleasesWaiters()
    {
        var (host, queue, worker) = NewWorker(cancelBlockedAtStop: true);
        host.WriteDword(Label, 0);
        Enqueue(host, queue, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(6));
        var waiter = Task.Run(queue.WaitForIdle);
        host.PendingCommands.Enqueue(() => throw new InvalidOperationException("relay command failed"));
        queue.Wake();

        Assert.Equal(IdleOutcome.Failed, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(queue.HasPending);
        Assert.Contains("relay command failed", Assert.Throws<InvalidOperationException>(() => worker.Stop()).Message);
    }

    [Fact]
    public void Stop_DrainsCompletableWorkBeforeExiting()
    {
        var (host, queue, worker) = NewWorker(cancelBlockedAtStop: true);
        host.WriteDword(Label, 1);
        Enqueue(host, queue, 1, WaitEqual(Label, 1), CreateInstanceCountPacket(8));

        Assert.Equal(IdleOutcome.Completed, worker.Stop());
        Assert.Equal(8u, queue.GetInterpreter(0).InstanceCount);
    }

    [Fact]
    public void Worker_RunsPostedCommandsBeforeSubmissions()
    {
        var (host, queue, worker) = NewWorker(cancelBlockedAtStop: false);
        var order = new List<string>();
        host.PendingCommands.Enqueue(() => order.Add("command"));
        queue.Wake();
        Thread.Sleep(50);

        Enqueue(host, queue, 1, CreateInstanceCountPacket(2));
        Assert.Equal(IdleOutcome.Completed, queue.WaitForIdle());
        order.Add("submission");
        worker.Stop();

        Assert.Equal(new[] { "command", "submission" }, order);
    }
}

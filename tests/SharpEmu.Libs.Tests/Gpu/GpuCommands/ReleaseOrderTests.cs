// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

// A release the CPU can observe lands after the older compute submissions that can still run, a soft wait included, and
// never waits for one that is blocked.
[Collection(CommandStreamQueueStateCollection.Name)]
public sealed class ReleaseOrderTests
{
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong SecondLabel = StreamRunner.LabelAddress + 0x10;
    private const ulong Graphics = StreamRunner.CommandAddress;
    private const ulong SecondGraphics = StreamRunner.CommandAddress + 0x400;
    private const ulong Compute = StreamRunner.DataAddress;
    private const uint FirstCompute = 0x20;

    private static uint[] Release32(ulong label, uint value) =>
        StreamRunner.Packet(PacketOpcode.ReleaseMemory, 0x28u | (5u << 8), 1u << 29,
            StreamRunner.Low(label), StreamRunner.High(label), value, 0, 0);

    private static uint[] Wait32(uint function, ulong address, uint reference) =>
        StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, function | 0x10u,
            StreamRunner.Low(address), StreamRunner.High(address), reference, 0xFFFF_FFFFu, 0);

    private static uint[] Instances(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static (RecordingCommandStreamHost Host, CommandStreamQueue Queue) NewQueue(bool softWaits = false)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host)
        {
            Landings = softWaits ? new EndOfPipeLandings { CapTicks = long.MaxValue } : null,
        };
        return (host, queue);
    }

    private static void Enqueue(RecordingCommandStreamHost host, CommandStreamQueue queue, uint? computeQueue, ulong address,
        ulong submission, params uint[][] packets)
    {
        var words = StreamRunner.Concat(packets);
        host.WriteWords(address, words);
        if (computeQueue is { } compute)
        {
            queue.EnqueueCompute(compute, address, (uint)words.Length, submission, null);
        }
        else
        {
            queue.EnqueueGraphics(address, (uint)words.Length, submission, null);
        }
    }

    [Fact]
    public void GraphicsReleaseLandsAfterAnOlderRunnableComputeSubmission()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, FirstCompute, Compute, 1, Instances(5));
        Enqueue(host, queue, null, Graphics, 2, Release32(Label, 1));

        // The round robin reaches the graphics queue first; its release lets the older compute submission run.
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(0u, host.ReadDword(Label));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(5u, queue.GetInterpreter(1).InstanceCount);
        Assert.Equal(0u, host.ReadDword(Label));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(1u, host.ReadDword(Label));
        Assert.Equal(SliceResult.NoWork, queue.ProcessOne());
    }

    [Fact]
    public void ReleaseLandsWhileAnOlderComputeSubmissionWaitsForIt()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, FirstCompute, Compute, 1, Wait32(3, Label, 1), Instances(5));
        Enqueue(host, queue, null, Graphics, 2, Release32(Label, 1));

        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        // The compute wait is unmet and blocks; the release then lands instead of waiting for it.
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(1u, queue.GetInterpreter(1).InstanceCount);

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(1u, host.ReadDword(Label));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(5u, queue.GetInterpreter(1).InstanceCount);
        Assert.Equal(SliceResult.NoWork, queue.ProcessOne());
    }

    [Fact]
    public void OlderComputeSubmissionStopsWaitingSoftlyForTheRelease()
    {
        var (host, queue) = NewQueue(softWaits: true);
        Enqueue(host, queue, null, Graphics, 1, Release32(Label, 1));
        Enqueue(host, queue, FirstCompute, Compute, 2, Wait32(3, Label, 1), Instances(5));
        Enqueue(host, queue, null, SecondGraphics, 3, Release32(SecondLabel, 1));

        // The first release lands; the compute wait it meets is soft while that release has not completed on the GPU and
        // the graphics queue can run.
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(1u, host.ReadDword(Label));
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(1u, queue.GetInterpreter(1).InstanceCount);

        // The second release waits for the older compute submission, which then runs without its soft wait.
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(0u, host.ReadDword(SecondLabel));
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(5u, queue.GetInterpreter(1).InstanceCount);
        Assert.Equal(0u, host.ReadDword(SecondLabel));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(1u, host.ReadDword(SecondLabel));
        Assert.Equal(SliceResult.NoWork, queue.ProcessOne());
    }

    [Fact]
    public void TwoReleasesWaitingForOlderBlockedWorkBothLand()
    {
        // Round-robin order: release, older blocked work, release, older blocked work. A retry from each waiting release
        // used to unblock the work the other one checks next, so neither release ever landed.
        var (host, queue) = NewQueue();
        const ulong neverWritten = Label + 0x40;
        Enqueue(host, queue, FirstCompute + 1, Compute + 0x100, 1, Wait32(3, neverWritten, 1));
        Enqueue(host, queue, FirstCompute + 3, Compute + 0x200, 2, Wait32(3, neverWritten + 0x10, 1));
        Enqueue(host, queue, FirstCompute, Compute, 3, Release32(Label, 1));
        Enqueue(host, queue, FirstCompute + 2, Compute + 0x300, 4, Release32(SecondLabel, 1));

        var slices = 0;
        while ((host.ReadDword(Label) == 0 || host.ReadDword(SecondLabel) == 0) && slices < 24)
        {
            queue.ProcessOne();
            slices++;
        }

        Assert.Equal((1u, 1u), (host.ReadDword(Label), host.ReadDword(SecondLabel)));
        Assert.True(slices < 24, $"the releases took {slices} slices");

        // The last completion retries the two waits once each; then the queue rests instead of spinning.
        var settle = 0;
        while (queue.ProcessOne() != SliceResult.AllBlocked && settle < 4)
        {
            settle++;
        }

        Assert.True(settle < 4, $"the queue did not settle after {settle} slices");
    }

    [Fact]
    public void ReleaseWithoutOlderComputeWorkLandsAtOnce()
    {
        var (host, queue) = NewQueue();
        Enqueue(host, queue, null, Graphics, 1, Release32(Label, 1));
        Enqueue(host, queue, FirstCompute, Compute, 2, Instances(5));

        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(1u, host.ReadDword(Label));
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(SliceResult.NoWork, queue.ProcessOne());
    }
}

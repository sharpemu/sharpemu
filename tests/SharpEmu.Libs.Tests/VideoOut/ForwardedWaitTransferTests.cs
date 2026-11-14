// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class ForwardedWaitTransferTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(34, 0)]
    [InlineData(34, 1)]
    [InlineData(34, 2)]
    [InlineData(34, 3)]
    public void TransferAfterForwardedHeldWriteWaitsBeforeTouchingDisjointMemory(int readerQueue, int operation)
    {
        using var harness = new DeferredSignalHarness();
        harness.Pending.ForwardHeldWrites = true;
        harness.AddDump();
        harness.Scheduler.Flush();
        var graphics = harness.Runner;
        var writer = new StreamRunner(33, graphics.Host);
        var reader = new StreamRunner(readerQueue, graphics.Host);
        var label = StreamRunner.LabelAddress;
        var flag = label + 0x200;
        var source = label + 0x300;
        var destination = label + 0x400;
        graphics.Host.WriteDword(source, 7);
        Assert.Equal(SubmissionProgress.Complete, graphics.Run(StreamRunner.Packet(PacketOpcode.ReleaseMemory,
            0x528, 1u << 29, StreamRunner.Low(label), StreamRunner.High(label), 1, 0, 0)));
        Assert.Equal(SubmissionProgress.Complete, writer.Run(Wait(label, 1),
            StreamRunner.Packet(PacketOpcode.WriteData, 0x500, StreamRunner.Low(flag), StreamRunner.High(flag), 5)));
        Assert.Equal(SubmissionProgress.Complete, reader.Run(Wait(flag, 5)));
        Assert.Equal(0u, graphics.Host.ReadDword(flag));

        var immediate = operation is 0 or 3;
        var input = immediate ? 9UL : source;
        var transfer = operation < 2
            ? StreamRunner.Packet(PacketOpcode.DmaData, immediate ? 2u << 29 : 0u,
                StreamRunner.Low(input), StreamRunner.High(input), StreamRunner.Low(destination), StreamRunner.High(destination), 4)
            : StreamRunner.Packet(PacketOpcode.CopyData, 0x100u | (immediate ? 5u : 1u),
                StreamRunner.Low(input), StreamRunner.High(input), StreamRunner.Low(destination), StreamRunner.High(destination));

        Assert.Equal(SubmissionProgress.Blocked, reader.Run(transfer));
        Assert.Equal(0u, graphics.Host.ReadDword(destination));
        Assert.DoesNotContain(graphics.Host.Calls, IsTransfer);

        harness.Scheduler.Flush();
        harness.Device.Complete(harness.Scheduler.CurrentTick);
        harness.Scheduler.RunCompletedOperations();
        Assert.Equal(5u, graphics.Host.ReadDword(flag));
        Assert.Equal(SubmissionProgress.Complete, reader.Run());
        Assert.Equal(immediate ? 9u : 7u, graphics.Host.ReadDword(destination));
        Assert.Single(graphics.Host.Calls.Where(IsTransfer));
        Assert.Equal(0, harness.Pending.Count);
    }

    private static bool IsTransfer(string call) => call.StartsWith("copy ") || call.StartsWith("fill ");
    private static uint[] Wait(ulong address, uint value) => StreamRunner.Packet(PacketOpcode.WaitRegisterMemory,
        0x13, StreamRunner.Low(address), StreamRunner.High(address), value, uint.MaxValue, 0);
}

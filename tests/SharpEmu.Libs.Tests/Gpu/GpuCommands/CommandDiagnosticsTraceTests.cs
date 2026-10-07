// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

[Collection(SchedulingStateCollection.Name)]
public sealed class CommandDiagnosticsTraceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConditionalBlock_ReportsOnlySkippedWork(bool execute)
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(StreamRunner.LabelAddress, execute ? 1u : 0u);
        var condition = StreamRunner.Packet(PacketOpcode.ConditionalExecute,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 0, 2);
        runner.Run(StreamRunner.Packet(PacketOpcode.NumInstances, 3), condition,
            StreamRunner.Packet(PacketOpcode.NumInstances, 7), StreamRunner.Packet(PacketOpcode.Nop, 0));
        Assert.Equal(execute ? 7u : 3u, runner.Interpreter.InstanceCount);
        if (!ImageClearTrace.Enabled || execute)
        {
            Assert.Empty(runner.Host.TracedWrites);
            return;
        }
        var trace = Assert.Single(runner.Host.TracedWrites);
        Assert.Equal("command-skip-conditional-execute", trace.Operation);
        Assert.Equal(StreamRunner.CommandAddress + 7 * sizeof(uint), trace.Address);
        Assert.Equal(8UL, trace.Size);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PredicatedPacket_ReportsOnlySkippedWork(bool skip)
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(StreamRunner.LabelAddress, skip ? 1UL : 0UL);
        var drawState = StreamRunner.Packet(PacketOpcode.NumInstances, 7);
        drawState[0] |= 1u;
        var predicate = StreamRunner.Packet(PacketOpcode.SetPredication,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress) | (3u << 16), 0);
        runner.Run(StreamRunner.Packet(PacketOpcode.NumInstances, 3), predicate, drawState);
        Assert.Equal(skip ? 3u : 7u, runner.Interpreter.InstanceCount);
        if (!ImageClearTrace.Enabled || !skip)
        {
            Assert.Empty(runner.Host.TracedWrites);
            return;
        }
        var trace = Assert.Single(runner.Host.TracedWrites);
        Assert.Equal("command-skip-predicated", trace.Operation);
        Assert.Equal(StreamRunner.CommandAddress + 6 * sizeof(uint), trace.Address);
        Assert.Equal(8UL, trace.Size);
        var writers = Assert.Single(runner.Host.WriterRequests);
        Assert.Equal(StreamRunner.LabelAddress, writers.Address);
        Assert.Equal(8UL, writers.Size);
    }

    [Fact]
    public void LaterPredicateRead_ReportsChangesWithoutChangingTheCurrentDecision()
    {
        if (!ImageClearTrace.Enabled) return;
        var runner = new StreamRunner();
        runner.Host.WriteQword(StreamRunner.LabelAddress, 1);
        runner.Host.WriteQword(StreamRunner.DataAddress, 1);
        var original = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            runner.Interpreter.SetPredication(0, 3, 0, StreamRunner.LabelAddress);
            Assert.True(runner.Interpreter.PredicateSkip);
            runner.Host.WriteQword(StreamRunner.LabelAddress, 0);
            runner.Interpreter.SetPredication(0, 3, 0, StreamRunner.DataAddress);
            Assert.True(runner.Interpreter.PredicateSkip);
        }
        finally { Console.SetError(original); }
        Assert.Contains($"PredicationChanged address=0x{StreamRunner.LabelAddress:X16} evaluated=0x0000000000000001 now=0x0000000000000000", output.ToString());
        Assert.Contains((StreamRunner.LabelAddress, sizeof(ulong)), runner.Host.GuestReadRanges);
    }

    [Fact]
    public void SkippedIndirectBuffer_UsesABoundedReadWithoutExecutingIt()
    {
        if (!ImageClearTrace.Enabled) return;
        var runner = new StreamRunner();
        var target = StreamRunner.DataAddress + 0x4000;
        runner.Host.WriteWords(target, StreamRunner.Packet(PacketOpcode.NumInstances, 99));
        runner.Host.WriteWords(StreamRunner.CommandAddress, StreamRunner.Packet(PacketOpcode.IndirectBuffer,
            StreamRunner.Low(target), StreamRunner.High(target), 5000));
        var initialCount = runner.Interpreter.InstanceCount;
        var original = Console.Error;
        using var output = new StringWriter();
        try
        {
            Console.SetError(output);
            runner.Interpreter.TraceSkippedPackets("predicated", StreamRunner.CommandAddress, 4, PacketOpcode.IndirectBuffer);
        }
        finally { Console.SetError(original); }
        Assert.Equal(initialCount, runner.Interpreter.InstanceCount);
        Assert.Contains((target, 4096 * sizeof(uint)), runner.Host.GuestReadRanges);
        Assert.DoesNotContain(runner.Host.GuestReadRanges, read => read.Address == target && read.Size > 16384);
        Assert.Contains($"CommandSkipTarget target=0x{target:X16}", output.ToString());
        Assert.Equal(16UL, Assert.Single(runner.Host.TracedWrites).Size);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterLabelTests
{
    private const uint Nop = PacketOpcode.Nop;
    private const ulong Label = StreamRunner.LabelAddress;

    private static uint[] EventWriteEop(uint eventType, uint eventIndex, uint cacheAction, uint source, uint interruptSelector, ulong destination, ulong value, uint cachePolicy) =>
        StreamRunner.Packet(
            PacketOpcode.EventWriteEndOfPipe,
            eventType | (eventIndex << 8) | (cacheAction << 12) | (cachePolicy << 25),
            StreamRunner.Low(destination),
            (StreamRunner.High(destination) & 0xFFFF) | (interruptSelector << 24) | (source << 29),
            StreamRunner.Low(value),
            StreamRunner.High(value));

    private static uint[] EventWriteEos(uint eventType, uint eventIndex, uint cacheAction, uint source, uint interruptSelector, ulong destination, uint value)
    {
        var packet = StreamRunner.Packet(
            PacketOpcode.EventWriteEndOfShader,
            eventType | (eventIndex << 8) | (cacheAction << 12),
            StreamRunner.Low(destination),
            (StreamRunner.High(destination) & 0xFFFF) | (interruptSelector << 24) | (source << 29),
            value);
        packet[0] |= 2u;
        return packet;
    }

    private static uint[] ReleaseMemoryNative(uint eventType, uint eventIndex, uint gcr, uint destinationSelect, uint dataSelection, uint interruptSelector, ulong destination, ulong value, uint contextId) =>
        StreamRunner.Packet(
            PacketOpcode.ReleaseMemory,
            eventType | (eventIndex << 8) | (gcr << 12),
            (destinationSelect << 16) | (interruptSelector << 24) | (dataSelection << 29),
            StreamRunner.Low(destination), StreamRunner.High(destination),
            StreamRunner.Low(value), StreamRunner.High(value),
            contextId);

    private static uint[] ReleaseMemoryWrapped(uint action, uint gcr, uint dataSelection, uint interruptSelector, ulong destination, ulong value, uint contextId) =>
        StreamRunner.CustomPacket(
            Nop, PacketCustomCode.ReleaseMemory,
            action,
            gcr | (dataSelection << 16) | (interruptSelector << 24),
            StreamRunner.Low(destination), StreamRunner.High(destination),
            StreamRunner.Low(value), StreamRunner.High(value),
            contextId);

    [Fact]
    public void EndOfPipeWrite64_LandsBeforeTheCompletionIsRecorded()
    {
        var runner = new StreamRunner();

        runner.Run(EventWriteEop(0x28, 0, 0x00, 2, 2, Label, 0x1122_3344_5566_7788, 0));

        var write = Assert.Single(runner.Host.EndOfPipeWrites);
        Assert.Equal(EndOfPipeWriteKind.Interrupt64, write.Kind);
        Assert.Equal(Label, write.Destination);
        Assert.Equal(0x1122_3344_5566_7788UL, runner.Host.ReadQword(Label));
        Assert.Equal(0, write.EventId);
    }

    [Theory]
    [InlineData(0x04u, 5u, 0x00u, 0u, EndOfPipeWriteKind.Write64)]
    [InlineData(0x14u, 0u, 0x38u, 0u, EndOfPipeWriteKind.WriteBack64)]
    [InlineData(0x2Fu, 0u, 0x00u, 0u, EndOfPipeWriteKind.Write64)]
    [InlineData(0x04u, 5u, 0x3Bu, 2u, EndOfPipeWriteKind.InterruptWriteBack64)]
    [InlineData(0x28u, 0u, 0x38u, 2u, EndOfPipeWriteKind.InterruptWriteBack64)]
    public void EndOfPipeWrite64_AcceptedCombinations(uint eventType, uint eventIndex, uint cacheAction, uint interruptSelector, EndOfPipeWriteKind expected)
    {
        var runner = new StreamRunner();

        runner.Run(EventWriteEop(eventType, eventIndex, cacheAction, 2, interruptSelector, Label, 0xABCD, 0));

        Assert.Equal(expected, Assert.Single(runner.Host.EndOfPipeWrites).Kind);
        Assert.Equal(0xABCDUL, runner.Host.ReadQword(Label));
    }

    [Theory]
    [InlineData(0x2Fu, 0u, 0x00u, 2u)]
    [InlineData(0x04u, 0u, 0x00u, 0u)]
    [InlineData(0x2Fu, 6u, 0x38u, 2u)]
    public void EndOfPipeWrite64_RejectedCombinations(uint eventType, uint eventIndex, uint cacheAction, uint interruptSelector)
    {
        var runner = new StreamRunner();

        var fatal = runner.RunExpectingFatal(EventWriteEop(eventType, eventIndex, cacheAction, 2, interruptSelector, Label, 1, 0));

        Assert.Contains("event type is unknown", fatal.Message);
        Assert.Contains($"type=0x{eventType:X2}", fatal.Message);
    }

    [Fact]
    public void InterruptSelectorOne_OnGraphicsQueuesTheInterruptWithoutAWrite()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Label, 0);

        runner.Run(EventWriteEop(0x28, 0, 0x00, 2, 1, Label, 0xFF, 0));

        Assert.Equal(0UL, runner.Host.ReadQword(Label));
        var write = Assert.Single(runner.Host.EndOfPipeWrites);
        Assert.Equal(EndOfPipeWriteKind.InterruptOnly, write.Kind);
    }

    [Fact]
    public void InterruptSelectorOne_OnComputeWritesAndInterrupts()
    {
        var runner = new StreamRunner(queueId: 3);

        runner.Run(EventWriteEop(0x28, 0, 0x00, 2, 1, Label, 0xFF, 0));

        Assert.Equal(0xFFUL, runner.Host.ReadQword(Label));
        var write = Assert.Single(runner.Host.EndOfPipeWrites);
        Assert.Equal(EndOfPipeWriteKind.Interrupt64, write.Kind);
        Assert.Equal(GpuCommandInterpreter.ComputeQueueBase + 2, write.EventId);
    }

    [Fact]
    public void ClockSource_WritesTheReferenceClock()
    {
        var runner = new StreamRunner();

        runner.Run(EventWriteEop(0x04, 5, 0x00, 4, 0, Label, 0, 0));

        Assert.NotEqual(0UL, runner.Host.ReadQword(Label));
        Assert.Equal(EndOfPipeWriteKind.ClockWrite, Assert.Single(runner.Host.EndOfPipeWrites).Kind);
    }

    [Fact]
    public void UnsupportedCachePolicyOrSelector_IsFatal()
    {
        var runner = new StreamRunner();

        Assert.Contains("cache policy", runner.RunExpectingFatal(EventWriteEop(0x28, 0, 0, 2, 0, Label, 1, 1)).Message);
        Assert.Contains("interrupt selector is unknown", runner.RunExpectingFatal(EventWriteEop(0x28, 0, 0, 2, 5, Label, 1, 0)).Message);
    }

    [Fact]
    public void EndOfShader_WritesThirtyTwoBits()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Label, ulong.MaxValue);

        runner.Run(EventWriteEos(0x2F, 6, 0x38, 2, 0, Label, 0x1234));

        Assert.Equal(0xFFFF_FFFF_0000_1234UL, runner.Host.ReadQword(Label));
        Assert.Equal(EndOfPipeWriteKind.WriteBack32, Assert.Single(runner.Host.EndOfPipeWrites).Kind);
    }

    [Fact]
    public void GdsSource_SynchronizesReadsAndInterruptsAtOnce()
    {
        var runner = new StreamRunner(queueId: 1);
        runner.Host.Gds[8] = 0x11;
        runner.Host.Gds[12] = 0x22;

        runner.Run(EventWriteEos(0x2F, 6, 0x00, 1, 2, Label, (2u << 16) | 2u));

        Assert.Equal(new[] { "synchronize", "read_gds 2 2", "eop GdsWrite32", $"interrupt {GpuCommandInterpreter.ComputeQueueBase} 0" }, runner.Host.Calls);
        Assert.Equal(0x11u, runner.Host.ReadDword(Label));
        Assert.Equal(0x22u, runner.Host.ReadDword(Label + 4));
    }

    [Fact]
    public void ReleaseMemoryNative_DataSelectionOne_WritesFlushesAndFollowsTheGcrBarrierRule()
    {
        var runner = new StreamRunner();

        runner.Run(ReleaseMemoryNative(0x28, 5, 0, 0, 1, 0, Label, 0x77, 0));
        Assert.Equal(new[] { "eop Write32", "flush" }, runner.Host.Calls);
        Assert.Equal(0x77u, runner.Host.ReadDword(Label));

        runner.Host.Calls.Clear();
        runner.Run(ReleaseMemoryNative(0x28, 5, 1u << 9, 0, 1, 2, Label, 0x78, 9));
        Assert.Equal(new[] { "barrier", "eop InterruptWriteBack32", "flush" }, runner.Host.Calls);
        Assert.Equal(9u, runner.Host.EndOfPipeWrites[^1].ContextId);
    }

    [Fact]
    public void ReleaseMemoryNative_NoDataOrSelectorFour_OnlyInterrupts()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 5);

        runner.Run(ReleaseMemoryNative(0x28, 5, 0, 0, 0, 2, Label, 1, 0));
        Assert.Equal(new[] { "eop InterruptOnly", "flush" }, runner.Host.Calls);

        runner.Host.Calls.Clear();
        runner.Run(ReleaseMemoryNative(0x04, 5, 0, 0, 1, 4, Label, 1, 0));
        Assert.Equal(new[] { "barrier", "eop InterruptOnly", "flush" }, runner.Host.Calls);
        Assert.Equal(5u, runner.Host.ReadDword(Label));

        runner.Host.Calls.Clear();
        runner.Run(ReleaseMemoryNative(0x28, 5, 0, 0, 1, 3, Label, 1, 0));
        Assert.Equal(new[] { "eop Write32", "flush" }, runner.Host.Calls);
        Assert.Equal(1u, runner.Host.ReadDword(Label));
    }

    [Fact]
    public void ReleaseMemoryNative_DataSelectionTwoAndThree()
    {
        var runner = new StreamRunner();

        runner.Run(ReleaseMemoryNative(0x28, 5, 0, 0, 2, 0, Label, 0x0102_0304_0506_0708, 0));
        Assert.Equal(0x0102_0304_0506_0708UL, runner.Host.ReadQword(Label));
        Assert.Equal(EndOfPipeWriteKind.Write64, runner.Host.EndOfPipeWrites[^1].Kind);
        Assert.DoesNotContain("flush", runner.Host.Calls);

        runner.Run(ReleaseMemoryNative(0x28, 5, 0, 0, 3, 0, Label, 0, 0));
        Assert.NotEqual(0UL, runner.Host.ReadQword(Label));
        Assert.Equal(EndOfPipeWriteKind.ClockWrite, runner.Host.EndOfPipeWrites[^1].Kind);
    }

    [Fact]
    public void ReleaseMemoryNative_GdsSelectionFlushesOnlyForSelectorOne()
    {
        var runner = new StreamRunner(queueId: 2);
        runner.Host.Gds[0] = 0x42;

        runner.Run(ReleaseMemoryNative(0x28, 5, 0, 0, 5, 1, Label, 1u << 16, 0));
        Assert.Equal(new[] { "synchronize", "read_gds 0 1", "eop GdsWrite32", $"interrupt {GpuCommandInterpreter.ComputeQueueBase + 1} 0", "flush" }, runner.Host.Calls);

        runner.Host.Calls.Clear();
        runner.Run(ReleaseMemoryNative(0x28, 5, 0, 0, 5, 0, Label, 1u << 16, 0));
        Assert.DoesNotContain("flush", runner.Host.Calls);
    }

    [Fact]
    public void ReleaseMemoryNative_RejectsUnknownSelections()
    {
        var runner = new StreamRunner();

        Assert.Contains("data selection", runner.RunExpectingFatal(ReleaseMemoryNative(0x28, 5, 0, 0, 4, 0, Label, 1, 0)).Message);
        Assert.Contains("destination is not supported", runner.RunExpectingFatal(ReleaseMemoryNative(0x28, 5, 0, 2, 1, 0, Label, 1, 0)).Message);
        Assert.Contains("interrupt selector is unknown", runner.RunExpectingFatal(ReleaseMemoryNative(0x28, 5, 0, 0, 0, 7, Label, 1, 0)).Message);
    }

    [Fact]
    public void ReleaseMemoryWrapped_DerivesTheEventIndexFromTheAction()
    {
        var runner = new StreamRunner();

        runner.Run(ReleaseMemoryWrapped(0x04, 0, 2, 2, Label, 0x5, 3));
        Assert.Equal(0x5UL, runner.Host.ReadQword(Label));
        Assert.Equal(new[] { "barrier", "eop Interrupt64" }, runner.Host.Calls);
        Assert.Equal(3u, runner.Host.EndOfPipeWrites[^1].ContextId);

        runner.Host.Calls.Clear();
        runner.Run(ReleaseMemoryWrapped(0x2F, 1u << 9, 1, 0, Label, 0x9, 0));
        Assert.Equal(0x9u, runner.Host.ReadDword(Label));
        Assert.Equal(new[] { "barrier", "eop WriteBack32", "flush" }, runner.Host.Calls);
    }

    [Fact]
    public void ReleaseMemoryWrapped_ConditionalInterruptsCompareTheLabel()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 4);

        runner.Run(ReleaseMemoryWrapped(0x28, 0, 1, 5, Label, 4, 1));
        Assert.Equal(new[] { "eop InterruptOnly", "flush" }, runner.Host.Calls);
        Assert.Equal(4u, runner.Host.ReadDword(Label));

        runner.Host.Calls.Clear();
        runner.Run(ReleaseMemoryWrapped(0x28, 0, 1, 5, Label, 3, 1));
        Assert.Empty(runner.Host.Calls);
    }

    [Theory]
    [InlineData(0x07u, 0u)]
    [InlineData(0x16u, 7u)]
    [InlineData(0x2Cu, 0u)]
    public void EventWrite_FlushTypesEmitABarrier(uint eventType, uint eventIndex)
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, eventType | (eventIndex << 8)));

        Assert.Equal(new[] { "barrier" }, runner.Host.Calls);
    }

    [Fact]
    public void EventWrite_IgnoredTypesUnknownTypesAndOcclusionDump()
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x38));
        Assert.Empty(runner.Host.Calls);

        Assert.Contains("event type is unknown", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.EventWrite, 0x16 | (1u << 8))).Message);
        Assert.Contains("event type is unknown", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.EventWrite, 0x3F)).Message);

        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x39 | (1u << 8), StreamRunner.Low(Label), StreamRunner.High(Label)));
        Assert.Equal(1UL << 63, runner.Host.ReadQword(Label));
        Assert.Equal(1UL << 63, runner.Host.ReadQword(Label + 15 * 16));
        Assert.Equal(0UL, runner.Host.ReadQword(Label + 8));

        runner.Run(StreamRunner.Packet(PacketOpcode.EventWrite, 0x39 | (1u << 8), StreamRunner.Low(Label), StreamRunner.High(Label)));
        Assert.Equal((1UL << 63) | 1, runner.Host.ReadQword(Label));
    }

    [Fact]
    public void FlipMarkers_PrepareRecordAndFlush()
    {
        var runner = new StreamRunner();
        var setFlip = StreamRunner.CustomPacket(Nop, PacketCustomCode.Flip, 7, 2, 1, 0x44, 0);

        runner.Run(setFlip);
        Assert.Equal(new[] { "prepare_flip 7 2 1 68", "eop Flip", "flush" }, runner.Host.Calls);

        runner.Host.Calls.Clear();
        runner.Run(StreamRunner.CustomPacket(Nop, 0, 0x6875_0778, StreamRunner.Low(Label), StreamRunner.High(Label), 0x99));
        Assert.Equal(new[] { "prepare_flip 7 2 1 68", "eop FlipWithWrite32", "flush" }, runner.Host.Calls);
        Assert.Equal(0x99u, runner.Host.ReadDword(Label));

        runner.Host.Calls.Clear();
        runner.Run(StreamRunner.CustomPacket(Nop, 0, 0x6875_0781, StreamRunner.Low(Label), StreamRunner.High(Label), 0x9A, 0x04, 0x38));
        Assert.Equal(new[] { "prepare_flip 7 2 1 68", "eop FlipWithInterruptWriteBack32", "flush" }, runner.Host.Calls);
        Assert.Contains("flip event type is unknown", runner.RunExpectingFatal(StreamRunner.CustomPacket(Nop, 0, 0x6875_0781, 0, 0, 0, 0x28, 0x38)).Message);
        // Marker ids with no side effect (Demon's Souls emits 0x6EC and 0x80C) are consumed, not fatal.
        runner.Host.Calls.Clear();
        runner.Run(StreamRunner.CustomPacket(Nop, 0, 0x6875_06EC), StreamRunner.CustomPacket(Nop, 0, 0x6875_080C));
        Assert.Empty(runner.Host.Calls);
    }

    [Fact]
    public void FlipWithoutASlot_SuspendsBeforeWritingTheLabel_AndWritesOnceOnRetry()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 0);
        runner.Host.FlipSlot = false;

        var progress = runner.Run(
            StreamRunner.CustomPacket(Nop, PacketCustomCode.Flip, 1, 0, 0, 0, 0),
            StreamRunner.CustomPacket(Nop, 0, 0x6875_0778, StreamRunner.Low(Label), StreamRunner.High(Label), 0x55));

        Assert.Equal(SubmissionProgress.Blocked, progress);
        Assert.Equal(0u, runner.Host.ReadDword(Label));

        runner.Host.FlipSlot = true;
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(0x55u, runner.Host.ReadDword(Label));
        Assert.Equal(2, runner.Host.Calls.Count(call => call.StartsWith("prepare_flip", StringComparison.Ordinal)));
        Assert.Equal(1, runner.Host.Calls.Count(call => call == "eop FlipWithWrite32"));
    }

    [Fact]
    public void WaitFlipDone_FlushesThenSuspendsUntilTheBufferIsPresented()
    {
        var runner = new StreamRunner();
        runner.Host.FlipDone = false;

        var progress = runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.WaitFlipDone, 3, 1, 0, 0, 0, 0), StreamRunner.Packet(PacketOpcode.NumInstances, 5));

        Assert.Equal(SubmissionProgress.Blocked, progress);
        Assert.Equal(new[] { "flush", "flip_done? 3 1" }, runner.Host.Calls);

        runner.Host.FlipDone = true;
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(5u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void AcquireMemory_IsANoOp()
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.AcquireMemory, 0, 0, 0, 0, 0, 0, 0), StreamRunner.Packet(PacketOpcode.AcquireMemory, 0, 0, 0, 0, 0, 0));

        Assert.Empty(runner.Host.Calls);
    }
}

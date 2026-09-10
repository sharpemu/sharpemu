// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterWaitTests
{
    private const uint Nop = PacketOpcode.Nop;
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Data = StreamRunner.DataAddress;

    private static uint[] CreateInstanceCountPacket(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static uint[] Wait32(uint function, ulong address, uint reference, uint mask, uint operation = 0, bool memorySpace = true) =>
        StreamRunner.Packet(
            PacketOpcode.WaitRegisterMemory,
            function | (memorySpace ? 0x10u : 0) | ((operation & 0x3u) << 8) | ((operation & 0xCu) << 4),
            StreamRunner.Low(address), StreamRunner.High(address), reference, mask, 0);

    private static uint[] Wait64(uint function, ulong address, ulong reference, ulong mask) =>
        StreamRunner.Packet(
            PacketOpcode.WaitRegisterMemory64,
            function | 0x10u,
            StreamRunner.Low(address), StreamRunner.High(address),
            StreamRunner.Low(reference), StreamRunner.High(reference),
            StreamRunner.Low(mask), StreamRunner.High(mask), 0);

    private static uint[] ConditionalWrite(uint function, ulong readAddress, uint reference, uint mask, bool writesMemory, ulong writeAddress, uint value) =>
        StreamRunner.Packet(
            PacketOpcode.ConditionalWrite,
            function | (1u << 4) | (writesMemory ? 1u << 8 : 0),
            StreamRunner.Low(readAddress), StreamRunner.High(readAddress), reference, mask,
            StreamRunner.Low(writeAddress), StreamRunner.High(writeAddress), value);

    private static uint[] Atomic(uint operation, uint command, ulong address, ulong source, ulong compare, uint engine = 0) =>
        StreamRunner.Packet(
            PacketOpcode.AtomicMemory,
            operation | (command << 8) | (engine << 30),
            StreamRunner.Low(address), StreamRunner.High(address),
            StreamRunner.Low(source), StreamRunner.High(source),
            StreamRunner.Low(compare), StreamRunner.High(compare), 0);

    private static uint[] Semaphore(ulong address, uint selection, bool writeSignal = false) =>
        StreamRunner.Packet(PacketOpcode.MemorySemaphore, StreamRunner.Low(address), StreamRunner.High(address), (selection << 29) | (writeSignal ? 1u << 20 : 0));

    [Theory]
    [InlineData(1u, 5u, 6u, true)]
    [InlineData(1u, 6u, 6u, false)]
    [InlineData(2u, 6u, 6u, true)]
    [InlineData(3u, 0x16u, 0x6u, true)]
    [InlineData(4u, 6u, 6u, false)]
    [InlineData(5u, 6u, 6u, true)]
    [InlineData(6u, 6u, 6u, false)]
    [InlineData(0u, 0u, 9u, true)]
    public void WaitRegisterMemory32_ComparesTheMaskedValue(uint function, uint value, uint reference, bool satisfied)
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, value);

        var progress = runner.Run(Wait32(function, Label, reference, 0xF), CreateInstanceCountPacket(2));

        Assert.Equal(satisfied ? SubmissionProgress.Complete : SubmissionProgress.Blocked, progress);
        Assert.Equal(satisfied ? 2u : 1u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void WaitRegisterMemory64_UsesQwordsAndResumesAtThePacket()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Label, 1);

        Assert.Equal(SubmissionProgress.Blocked, runner.Run(Wait64(5, Label, 0x1_0000_0000, ulong.MaxValue), CreateInstanceCountPacket(3)));
        runner.Host.WriteQword(Label, 0x1_0000_0000);
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(3u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void WaitRegisterMemory_RejectsRegisterSpaceUnknownCompareAndOperations()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 1);

        Assert.Contains("register-space wait", runner.RunExpectingFatal(Wait32(3, Label, 1, 1, memorySpace: false)).Message);
        Assert.Contains("compare function is unknown", runner.RunExpectingFatal(Wait32(7, Label, 1, 1)).Message);
        Assert.Contains("wait operation is not supported", runner.RunExpectingFatal(Wait32(3, Label, 1, 1, operation: 2)).Message);
        Assert.Contains("wait address is zero", runner.RunExpectingFatal(Wait32(3, 0, 1, 1)).Message);
    }

    [Fact]
    public void ConditionalWaitOperation_RunsOnlyAfterTheScratchEnabledIt()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 0);
        runner.Host.WriteDword(Data, 1);

        Assert.Equal(SubmissionProgress.Complete, runner.Run(Wait32(3, Label, 1, 1, operation: 4)));

        runner.Run(ConditionalWrite(3, Data, 1, 1, writesMemory: false, 0, 1));
        Assert.True(runner.Interpreter.ConditionalWaitEnabled);
        Assert.Equal(SubmissionProgress.Blocked, runner.Run(Wait32(3, Label, 1, 1, operation: 4)));

        runner.Run(ConditionalWrite(3, Data, 1, 1, writesMemory: false, 0, 0));
        Assert.False(runner.Interpreter.ConditionalWaitEnabled);
    }

    [Fact]
    public void ConditionalWrite_WritesMemoryOnlyWhenTheCompareHolds()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Data, 7);
        runner.Host.WriteDword(Label, 0);

        runner.Run(ConditionalWrite(4, Data, 7, 0xFF, writesMemory: true, Label, 0x11));
        Assert.Equal(0u, runner.Host.ReadDword(Label));

        runner.Run(ConditionalWrite(3, Data, 7, 0xFF, writesMemory: true, Label, 0x11));
        Assert.Equal(0x11u, runner.Host.ReadDword(Label));

        Assert.Contains("conditional-write packet is not supported", runner.RunExpectingFatal(ConditionalWrite(7, Data, 7, 0xFF, writesMemory: true, Label, 1)).Message);
    }

    [Fact]
    public void WrappedWaits_AllThreeLayouts()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Label, 0x2_0000_0005);

        var legacy = StreamRunner.CustomPacket(Nop, PacketCustomCode.WaitMemory32, StreamRunner.Low(Label), StreamRunner.High(Label), 0xF, 0x13, 5);
        var wait32 = StreamRunner.CustomPacket(Nop, PacketCustomCode.WaitMemory32, StreamRunner.Low(Label), StreamRunner.High(Label), 0xF, 5, 0x13, 0);
        var wait64 = StreamRunner.CustomPacket(Nop, PacketCustomCode.WaitMemory64, StreamRunner.Low(Label), StreamRunner.High(Label), 0xFFFF_FFFF, 0xFFFF_FFFF, 5, 2, 0x13, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(legacy, wait32, wait64, CreateInstanceCountPacket(4)));
        Assert.Equal(4u, runner.Interpreter.InstanceCount);

        var unmet = StreamRunner.CustomPacket(Nop, PacketCustomCode.WaitMemory32, StreamRunner.Low(Label), StreamRunner.High(Label), 0xF, 6, 0x13, 0);
        Assert.Equal(SubmissionProgress.Blocked, runner.Run(unmet));

        var maskless = StreamRunner.CustomPacket(Nop, PacketCustomCode.WaitMemory32, StreamRunner.Low(Label), StreamRunner.High(Label), 0, 6, 0x13, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(maskless));
    }

    [Fact]
    public void ConditionalExecute_SkipsTheBlockWhenTheDwordIsZero()
    {
        var runner = new StreamRunner();
        var execute = StreamRunner.Packet(PacketOpcode.ConditionalExecute, StreamRunner.Low(Label), StreamRunner.High(Label), 0, 2);

        runner.Host.WriteDword(Label, 0);
        runner.Run(execute, CreateInstanceCountPacket(5), CreateInstanceCountPacket(6));
        Assert.Equal(6u, runner.Interpreter.InstanceCount);

        runner.Host.WriteDword(Label, 1);
        runner.Run(execute, CreateInstanceCountPacket(7), CreateInstanceCountPacket(8));
        Assert.Equal(8u, runner.Interpreter.InstanceCount);

        var overrun = StreamRunner.Packet(PacketOpcode.ConditionalExecute, StreamRunner.Low(Label), StreamRunner.High(Label), 0, 3);
        Assert.Contains("conditional-execute packet is not supported", runner.RunExpectingFatal(overrun, CreateInstanceCountPacket(9)).Message);
        Assert.Contains("conditional-execute packet is not supported", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.ConditionalExecute, 1, 0, 0, 0)).Message);
    }

    [Fact]
    public void SetPredication_BothLayoutsAndTheWaitFlag()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Label, 0);
        var skipped = CreateInstanceCountPacket(2);
        skipped[0] |= 1u;

        var newLayout = StreamRunner.Packet(PacketOpcode.SetPredication, (3u << 16) | (1u << 8) | (1u << 12), StreamRunner.Low(Label), StreamRunner.High(Label));
        runner.Run(newLayout, skipped);
        Assert.Equal(new[] { "flush_and_wait" }, runner.Host.Calls);
        Assert.True(runner.Interpreter.PredicateSkip);
        Assert.Equal(1u, runner.Interpreter.InstanceCount);

        var oldLayout = StreamRunner.Packet(PacketOpcode.SetPredication, StreamRunner.Low(Label), StreamRunner.High(Label) | (3u << 16));
        runner.Run(oldLayout, skipped);
        Assert.False(runner.Interpreter.PredicateSkip);
        Assert.Equal(2u, runner.Interpreter.InstanceCount);

        runner.Run(StreamRunner.Packet(PacketOpcode.SetPredication, 0, 0, 0));
        Assert.False(runner.Interpreter.PredicateSkip);
        Assert.Contains("predication address is zero", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetPredication, 3u << 16, 0, 0)).Message);
        Assert.Contains("predication operation is unknown", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetPredication, 1u << 16, 0, 0)).Message);
    }

    [Fact]
    public void Rewind_SuspendsUntilTheValidBitIsSet()
    {
        var runner = new StreamRunner();

        Assert.Equal(SubmissionProgress.Complete, runner.Run(StreamRunner.Packet(PacketOpcode.Rewind, 0x8100_0000u), CreateInstanceCountPacket(2)));
        Assert.Equal(SubmissionProgress.Blocked, runner.Run(StreamRunner.Packet(PacketOpcode.Rewind, 0), CreateInstanceCountPacket(3)));
        Assert.Contains("rewind packet is not supported", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.Rewind, 2)).Message);
    }

    [Fact]
    public void CeAndDeCounters_GateTheStream()
    {
        var runner = new StreamRunner();
        var incrementCe = StreamRunner.Packet(PacketOpcode.IncrementCeCounter, 1);
        var incrementDe = StreamRunner.Packet(PacketOpcode.IncrementDeCounter, 0);
        var waitCe = StreamRunner.Packet(PacketOpcode.WaitOnCeCounter, 1);

        Assert.Equal(SubmissionProgress.Blocked, runner.Run(waitCe));
        Assert.Equal(SubmissionProgress.Complete, runner.Run(incrementCe, waitCe, incrementDe));
        Assert.Equal(1u, runner.Interpreter.ConstantEngineCount);
        Assert.Equal(1u, runner.Interpreter.DrawEngineCount);

        Assert.Equal(SubmissionProgress.Complete, runner.Run(StreamRunner.Packet(PacketOpcode.WaitOnDeCounterDiff, 1)));
        Assert.Equal(SubmissionProgress.Blocked, runner.Run(StreamRunner.Packet(PacketOpcode.WaitOnDeCounterDiff, 0)));
        Assert.Contains("packet is not supported", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.IncrementCeCounter, 2)).Message);
    }

    [Fact]
    public void AtomicMemory_AppliesOnceAndLoopsOnCompareSwap()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Data, 10);

        runner.Run(Atomic(0x0F, 0, Data, 5, 0));
        Assert.Equal(15u, runner.Host.ReadDword(Data));
        Assert.True(runner.Interpreter.AtomicReturnMeValid);
        Assert.Equal(10UL, runner.Interpreter.AtomicReturnMeData);

        Assert.Equal(SubmissionProgress.Blocked, runner.Run(Atomic(0x08, 1, Data, 99, 20), CreateInstanceCountPacket(4)));
        Assert.Equal(15u, runner.Host.ReadDword(Data));
        runner.Host.WriteDword(Data, 20);
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(99u, runner.Host.ReadDword(Data));
        Assert.Equal(4u, runner.Interpreter.InstanceCount);

        runner.Run(Atomic(0x0F, 0, Data, 1, 0, engine: 1));
        Assert.True(runner.Interpreter.AtomicReturnPfpValid);
        Assert.Contains("atomic packet is not supported", runner.RunExpectingFatal(Atomic(0x01, 0, Data, 1, 0)).Message);
        Assert.Contains("atomic packet is not supported", new StreamRunner(queueId: 1).RunExpectingFatal(Atomic(0x0F, 0, Data, 1, 0, engine: 1)).Message);
    }

    [Fact]
    public void AtomicAndSemaphore_ReadGpuProducedValuesThroughTheHost()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Data, 10);
        runner.Host.PendingGpuValues[Data] = 20;

        runner.Run(Atomic(0x0F, 0, Data, 5, 0));

        Assert.Equal(25u, runner.Host.ReadDword(Data));
        Assert.Equal(20UL, runner.Interpreter.AtomicReturnMeData);
        Assert.Contains(Data, runner.Host.GuestReads);

        runner.Host.WriteQword(Label, 0);
        runner.Host.PendingGpuValues[Label] = 1;
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Semaphore(Label, 7)));
        Assert.Equal(0UL, runner.Host.ReadQword(Label));

        runner.Host.PendingGpuValues[Label] = 3;
        runner.Run(Semaphore(Label, 6));
        Assert.Equal(4UL, runner.Host.ReadQword(Label));
    }

    [Fact]
    public void MemorySemaphore_SignalsAndWaits()
    {
        var runner = new StreamRunner();
        runner.Host.WriteQword(Data, 0);

        runner.Run(Semaphore(Data, 6), Semaphore(Data, 6));
        Assert.Equal(2UL, runner.Host.ReadQword(Data));

        runner.Run(Semaphore(Data, 6, writeSignal: true));
        Assert.Equal(1UL, runner.Host.ReadQword(Data));

        Assert.Equal(SubmissionProgress.Complete, runner.Run(Semaphore(Data, 7)));
        Assert.Equal(0UL, runner.Host.ReadQword(Data));

        Assert.Equal(SubmissionProgress.Blocked, runner.Run(Semaphore(Data, 7), CreateInstanceCountPacket(3)));
        runner.Host.WriteQword(Data, 1);
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(0UL, runner.Host.ReadQword(Data));
        Assert.Equal(3u, runner.Interpreter.InstanceCount);
        Assert.Contains("semaphore packet is not supported", runner.RunExpectingFatal(Semaphore(Data, 5)).Message);
    }
}

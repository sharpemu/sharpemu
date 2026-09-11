// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterLoopTests
{
    private const uint Nop = PacketOpcode.Nop;
    private const ulong Command = StreamRunner.CommandAddress;
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Nested = StreamRunner.DataAddress;

    private static uint[] CreateInstanceCountPacket(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static uint[] IndirectBuffer(ulong address, uint dwords, bool chain = false) =>
        StreamRunner.Packet(PacketOpcode.IndirectBuffer, StreamRunner.Low(address), StreamRunner.High(address) & 0xFFFF, dwords | (chain ? 1u << 20 : 0));

    private static uint[] Rewind(bool valid) => StreamRunner.Packet(PacketOpcode.Rewind, valid ? 0x8000_0000u : 0);

    private static uint[] WaitEqual(ulong address, uint reference) =>
        StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u, StreamRunner.Low(address), StreamRunner.High(address), reference, 0xFFFF_FFFFu, 0);

    private static uint[] WriteData(ulong destination, params uint[] values)
    {
        var payload = new uint[3 + values.Length];
        payload[0] = 5u << 8;
        payload[1] = StreamRunner.Low(destination);
        payload[2] = StreamRunner.High(destination);
        values.CopyTo(payload, 3);
        return StreamRunner.Packet(PacketOpcode.WriteData, payload);
    }

    [Fact]
    public void FillerAndPredicatedPackets_AreSkipped()
    {
        var runner = new StreamRunner();
        var predicated = CreateInstanceCountPacket(7);
        predicated[0] |= 1u;
        runner.Host.WriteQword(Label, 1);
        var predication = StreamRunner.Packet(PacketOpcode.SetPredication, StreamRunner.Low(Label), StreamRunner.High(Label) | (3u << 16), 0);

        var progress = runner.Run(new[] { PacketHeader.FillerHeader }, predication, predicated, CreateInstanceCountPacket(9));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(9u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void UnknownOpcode_IsFatalWithTheOffsetAndHeader()
    {
        var runner = new StreamRunner();

        var fatal = runner.RunExpectingFatal(CreateInstanceCountPacket(2), StreamRunner.Packet(0x41, 0));

        Assert.Contains("opcode=0x41", fatal.Message);
        Assert.Contains("offset=0x00002", fatal.Message);
    }

    [Fact]
    public void PacketLongerThanTheBuffer_IsFatal()
    {
        var runner = new StreamRunner();
        runner.Load(Command, new[] { PacketHeader.Make(4, PacketOpcode.NumInstances), 1u });

        var fatal = Assert.Throws<CommandStreamFatalException>(() => runner.Run());

        Assert.Contains("length=4 remaining=2", fatal.Message);
    }

    [Fact]
    public void NestedIndirectBuffer_RunsBeforeTheParentContinues()
    {
        var runner = new StreamRunner();
        runner.Load(Nested, CreateInstanceCountPacket(5));

        var progress = runner.Run(CreateInstanceCountPacket(2), IndirectBuffer(Nested, 2), CreateInstanceCountPacket(3));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(3u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void ChainedIndirectBuffer_ReplacesTheParent()
    {
        var runner = new StreamRunner();
        runner.Load(Nested, CreateInstanceCountPacket(5));

        var progress = runner.Run(CreateInstanceCountPacket(2), IndirectBuffer(Nested, 2, chain: true), CreateInstanceCountPacket(3));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal(5u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void IndirectBufferPaddingAndRingChunkAdvance()
    {
        var runner = new StreamRunner();
        var chunkBase = RecordingCommandStreamHost.MemoryBase + 0x2_0000;
        var nextChunk = chunkBase + GpuCommandInterpreter.RingChunkBytes;
        runner.Host.WriteWords(chunkBase, StreamRunner.Concat(CreateInstanceCountPacket(4), IndirectBuffer(1, 0)));
        runner.Host.WriteWords(nextChunk, CreateInstanceCountPacket(6));

        // Padding is ignored; the chain enters the ring; the sentinel advances to the next chunk.
        var progress = runner.Run(IndirectBuffer(0, 0), IndirectBuffer(chunkBase, 6, chain: true));

        Assert.Equal(SubmissionProgress.Blocked, progress);
        Assert.Equal(6u, runner.Interpreter.InstanceCount);

        // The next word of the ring is still unwritten: the stream parks until the guest appends.
        runner.Host.WriteWords(nextChunk + 8, CreateInstanceCountPacket(8));
        Assert.Equal(SubmissionProgress.Blocked, runner.Run());
        Assert.Equal(8u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void SuspensionInsideANestedBuffer_ResumesAtTheSameCursor()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 0);
        runner.Host.WriteWords(Nested, StreamRunner.Concat(WaitEqual(Label, 1), CreateInstanceCountPacket(5)));

        Assert.Equal(SubmissionProgress.Blocked, runner.Run(CreateInstanceCountPacket(2), IndirectBuffer(Nested, 9), CreateInstanceCountPacket(3)));
        Assert.Equal(2u, runner.Interpreter.InstanceCount);

        runner.Host.WriteDword(Label, 1);
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(3u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void BranchPacket_TakesThenOrElse()
    {
        var runner = new StreamRunner();
        var thenBuffer = Nested;
        var elseBuffer = Nested + 0x100;
        runner.Host.WriteWords(thenBuffer, CreateInstanceCountPacket(11));
        runner.Host.WriteWords(elseBuffer, CreateInstanceCountPacket(22));
        runner.Host.WriteQword(Label, 5);
        uint[] Branch(uint function) => StreamRunner.Packet(
            PacketOpcode.IndirectBuffer,
            2u | (function << 8),
            StreamRunner.Low(Label), StreamRunner.High(Label),
            0xFFFF_FFFFu, 0xFFFF_FFFFu,
            5, 0,
            StreamRunner.Low(thenBuffer), StreamRunner.High(thenBuffer), 2,
            StreamRunner.Low(elseBuffer), StreamRunner.High(elseBuffer), 2);

        runner.Run(Branch(3));
        Assert.Equal(11u, runner.Interpreter.InstanceCount);

        runner.Run(Branch(4));
        Assert.Equal(22u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void ContextState_PushPopAndDoublePush()
    {
        var runner = new StreamRunner();
        var setTargetMask = StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x8E, 0xF);
        uint[] State(uint operation) => StreamRunner.CustomPacket(Nop, PacketCustomCode.ContextState, operation, 0);

        runner.Run(setTargetMask, State(1), StreamRunner.Packet(PacketOpcode.SetContextRegister, 0x8E, 0x1), State(2));
        Assert.Equal(0xFu, runner.Interpreter.TypedRegisters.Context.RenderTargetMask);

        runner.Run(State(3));
        Assert.Equal(0u, runner.Interpreter.TypedRegisters.Context.RenderTargetMask);
        Assert.True(runner.Interpreter.TypedRegisters.ContextPushed);

        var fatal = runner.RunExpectingFatal(State(1));
        Assert.Contains("already pushed", fatal.Message);
    }

    [Fact]
    public void ConstantRam_WritesDumpsAndBounds()
    {
        var runner = new StreamRunner();
        var write = StreamRunner.Packet(PacketOpcode.WriteConstantRam, 0x10, 0xAAAA_0001, 0xAAAA_0002);
        var dump = StreamRunner.Packet(PacketOpcode.DumpConstantRam, 0x10, 2, StreamRunner.Low(Label), StreamRunner.High(Label));

        runner.Run(write, dump);

        Assert.Equal(0xAAAA_0001u, runner.Host.ReadDword(Label));
        Assert.Equal(0xAAAA_0002u, runner.Host.ReadDword(Label + 4));
        Assert.Contains("out of range", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.WriteConstantRam, 0xC000, 1)).Message);
    }

    [Fact]
    public void Reset_ClearsBanksButKeepsInstancesAndIndexBase()
    {
        var runner = new StreamRunner();
        runner.Run(
            CreateInstanceCountPacket(4),
            StreamRunner.Packet(PacketOpcode.IndexBase, 0x1234, 0),
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, 0x10, 1),
            StreamRunner.Packet(PacketOpcode.WriteConstantRam, 0, 9));

        runner.Interpreter.Reset();

        Assert.Empty(runner.Interpreter.Registers.Shader);
        Assert.Equal(0u, runner.Interpreter.ConstantRam[0]);
        Assert.Equal(4u, runner.Interpreter.InstanceCount);
        Assert.Equal(0x1234UL, runner.Interpreter.IndexBaseAddress);

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.DrawReset, 0));
        Assert.Equal(1u, runner.Interpreter.InstanceCount);
        Assert.Equal(0UL, runner.Interpreter.IndexBaseAddress);
        Assert.Contains("queue_reset 0", runner.Host.Calls);
    }

    [Fact]
    public void NestedBuffer_PatchingTheParentsNextPacket_IsSeen()
    {
        var runner = new StreamRunner();
        var patched = CreateInstanceCountPacket(30);
        var parentPacketAddress = Command + 4 * 4;
        runner.Host.WriteWords(Nested, WriteData(parentPacketAddress + 4, 31));

        runner.Run(IndirectBuffer(Nested, 5), patched);

        Assert.Equal(31u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void DmaFillOverCommandsAhead_IsSeen()
    {
        var runner = new StreamRunner();
        var target = Command + (8 * 4);
        var fill = StreamRunner.Packet(PacketOpcode.DmaData, 2u << 29, 17, 0, StreamRunner.Low(target), StreamRunner.High(target), 4);

        runner.Run(fill, CreateInstanceCountPacket(1));

        Assert.Equal(17u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void PacketPatchedWhileSuspended_IsReadOnResume()
    {
        var runner = new StreamRunner();

        Assert.Equal(SubmissionProgress.Blocked, runner.Run(Rewind(false), CreateInstanceCountPacket(3)));
        Assert.Equal(1u, runner.Interpreter.InstanceCount);

        runner.Host.WriteDword(Command + 4, 0x8000_0000u);
        runner.Host.WriteDword(Command + 12, 4);
        Assert.Equal(SubmissionProgress.Complete, runner.Run());
        Assert.Equal(4u, runner.Interpreter.InstanceCount);
    }

    [Fact]
    public void EveryGuestRead_GoesThroughTheHost()
    {
        var runner = new StreamRunner();
        runner.Host.WriteDword(Label, 1);

        runner.Run(WaitEqual(Label, 1));

        Assert.Contains(Command, runner.Host.GuestReads);
        Assert.Contains(Label, runner.Host.GuestReads);
    }

    [Fact]
    public void PendingCommands_RunBeforeEveryPacket()
    {
        var runner = new StreamRunner();
        var ran = new List<uint>();
        runner.Host.PendingCommands.Enqueue(() => ran.Add(runner.Interpreter.InstanceCount));

        runner.Run(CreateInstanceCountPacket(2), CreateInstanceCountPacket(3));

        Assert.Equal(new uint[] { 1 }, ran);
        Assert.Equal(3, runner.Host.PendingCommandRuns);
    }

    [Fact]
    public void SequentialChains_DoNotConsumeNestingDepth()
    {
        var runner = new StreamRunner();
        const int chains = 100;
        for (var index = 0; index < chains; index++)
        {
            var buffer = Nested + ((ulong)index * 0x40);
            var next = buffer + 0x40;
            var words = index == chains - 1
                ? CreateInstanceCountPacket((uint)index + 1)
                : StreamRunner.Concat(CreateInstanceCountPacket((uint)index + 1), IndirectBuffer(next, index == chains - 2 ? 2u : 6u, chain: true));
            runner.Host.WriteWords(buffer, words);
        }

        var progress = runner.Run(IndirectBuffer(Nested, 6, chain: true));

        Assert.Equal(SubmissionProgress.Complete, progress);
        Assert.Equal((uint)chains, runner.Interpreter.InstanceCount);
        Assert.Equal(0, runner.Commands.Depth);
    }

    [Fact]
    public void NestingDeeperThanTheLimit_IsFatal()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Nested, StreamRunner.Concat(IndirectBuffer(Nested, 6), CreateInstanceCountPacket(1)));

        var fatal = runner.RunExpectingFatal(IndirectBuffer(Nested, 6), CreateInstanceCountPacket(1));

        Assert.Contains("nesting is too deep", fatal.Message);
    }
}

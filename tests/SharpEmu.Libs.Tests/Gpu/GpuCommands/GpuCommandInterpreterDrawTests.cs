// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class GpuCommandInterpreterDrawTests
{
    private const uint Nop = PacketOpcode.Nop;
    private const ulong Indices = StreamRunner.DataAddress;
    private const ulong Arguments = StreamRunner.TableAddress;

    private static uint[] SetBase(ulong address, bool dispatch)
    {
        var packet = StreamRunner.Packet(PacketOpcode.SetBase, 1, StreamRunner.Low(address), StreamRunner.High(address));
        if (dispatch)
        {
            packet[0] |= 2u;
        }

        return packet;
    }

    [Fact]
    public void DrawIndex2_PassesTheIndexStateAndInstances()
    {
        var runner = new StreamRunner();

        runner.Run(
            StreamRunner.Packet(PacketOpcode.IndexType, 1),
            StreamRunner.Packet(PacketOpcode.NumInstances, 4),
            StreamRunner.Packet(PacketOpcode.DrawIndex2, 100, StreamRunner.Low(Indices), StreamRunner.High(Indices), 60, 0x20));

        var draw = Assert.Single(runner.Host.IndexedDraws);
        Assert.Equal(60u, draw.IndexCount);
        Assert.Equal(Indices, draw.IndexAddress);
        Assert.Equal(1u, draw.IndexTypeAndSize);
        Assert.Equal(4u, draw.InstanceCount);
        Assert.Equal(PacketOpcode.DrawIndex2, draw.Opcode);
        Assert.Equal(StreamRunner.CommandAddress + 16, draw.PacketAddress);
        Assert.Contains("indexed draw is not supported", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.DrawIndex2, 10, 0, 0, 60, 0)).Message);
    }

    [Fact]
    public void DrawPreamble_UsesTheLongForm()
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.Packet(PacketOpcode.DispatchDrawPreamble, 30, StreamRunner.Low(Indices), StreamRunner.High(Indices), 8, 0, 0, 3, 0xA0));

        var draw = Assert.Single(runner.Host.IndexedDraws);
        Assert.Equal(30u, draw.IndexCount);
        Assert.Equal(3u, draw.InstanceCount);
    }

    [Fact]
    public void DrawIndexOffset_AddsTheScaledOffsetToTheIndexBase()
    {
        var runner = new StreamRunner();

        runner.Run(
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 1),
            StreamRunner.Packet(PacketOpcode.DrawIndexOffset2, 100, 5, 20, 0));

        var draw = Assert.Single(runner.Host.IndexedDraws);
        Assert.Equal(Indices + 20, draw.IndexAddress);
        Assert.Equal(5u, runner.Interpreter.DrawIndexOffset);
    }

    [Theory]
    [InlineData(3u, 3u, 0x40000000u)]
    [InlineData(3u, 3u, 1u)]
    [InlineData(2u, 3u, 0u)]
    public void DrawIndexOffsetRejectsInvalidPacketFlagsAndCounts(uint maximumIndexCount, uint indexCount, uint initiator)
    {
        var runner = new StreamRunner();
        var failure = runner.RunExpectingFatal(StreamRunner.Packet(
            PacketOpcode.DrawIndexOffset2, maximumIndexCount, 0, indexCount, initiator));
        Assert.Contains("offset draw is not supported", failure.Message);
        Assert.Empty(runner.Host.IndexedDraws);
    }

    [Fact]
    public void DrawAuto_NativeWrappedAndMultiAuto()
    {
        var runner = new StreamRunner();

        runner.Run(
            StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 12, 0x22),
            StreamRunner.CustomPacket(Nop, PacketCustomCode.DrawIndexAuto, 0, 0, 0, 0, 0, 0),
            StreamRunner.CustomPacket(Nop, PacketCustomCode.DrawIndexAuto, 7, 0, 0, 0, 0, 0),
            StreamRunner.Packet(PacketOpcode.DrawIndexMultiAuto, 0, 0, 3u << 21));

        Assert.Equal(new uint[] { 12, 7, 3 }, runner.Host.AutoDraws.Select(draw => draw.VertexCount));
        Assert.Contains("auto draw flags", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 1, 0x1)).Message);
    }

    [Fact]
    public void IndirectDraws_ReadArgumentsFromMemoryAndKeepTheInstanceCount()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Arguments, new uint[] { 9, 3, 1, 2 });
        runner.Host.WriteWords(Arguments + 16, new uint[] { 50, 2, 4, 7, 1 });

        runner.Run(
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 0),
            StreamRunner.Packet(PacketOpcode.IndexBufferSize, 40),
            StreamRunner.Packet(PacketOpcode.DrawIndirect, 0, 0, 0, 2),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0, 0, 0x22),
            StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 5, 0));

        var auto = runner.Host.AutoDraws[0];
        Assert.Equal((9u, 3u, 1u, 2u, DrawOffsetSource.IndirectArguments), (auto.VertexCount, auto.InstanceCount, auto.FirstVertex, auto.FirstInstance, auto.OffsetSource));
        var indexed = Assert.Single(runner.Host.IndexedDraws);
        Assert.Equal(40u, indexed.IndexCount);
        Assert.Equal(Indices + 8, indexed.IndexAddress);
        Assert.Equal(7, indexed.BaseVertex);
        Assert.Equal(2u, runner.Host.AutoDraws[1].InstanceCount);

        Assert.Contains("initiator is not supported", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.DrawIndirect, 0, 0, 0, 3)).Message);
        Assert.Contains("arguments base is zero", new StreamRunner().RunExpectingFatal(StreamRunner.Packet(PacketOpcode.DrawIndirect, 0, 0, 0, 2)).Message);
    }

    // The CP writes the start vertex or base vertex and the start instance to the registers the packet names (0x280
    // names none); the shader adds them to its vertex and instance ids, so the draw counts those from 0.
    [Fact]
    public void IndirectDraws_WriteTheRegistersThePacketNames()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDrawOnGpu = true;
        runner.Host.WriteWords(Arguments, new uint[] { 9, 3, 1, 2 });
        runner.Host.WriteWords(Arguments + 16, new uint[] { 50, 2, 4, 7, 5 });
        var seen = new List<(uint BaseVertex, uint StartInstance)>();
        runner.Host.OnDraw = () => seen.Add((runner.Interpreter.Registers.Shader[0xA9], runner.Interpreter.Registers.Shader[0xA8]));

        runner.Run(
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 0),
            StreamRunner.Packet(PacketOpcode.IndexBufferSize, 40),
            StreamRunner.Packet(PacketOpcode.DrawIndirect, 0, 0xA9, 0xA8, 2),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0xA9, 0xA8, 0x22),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0x280, 0xA8, 0x22));

        Assert.Equal(new[] { (1u, 2u), (7u, 5u), (7u, 5u) }, seen);
        var auto = Assert.Single(runner.Host.AutoDraws);
        Assert.Equal((9u, 3u, 0u, 0u), (auto.VertexCount, auto.InstanceCount, auto.FirstVertex, auto.FirstInstance));
        // The registers need the arguments, so they are read here although the host could leave them on the GPU.
        Assert.All(runner.Host.IndexedDraws, draw => Assert.Equal(0ul, draw.IndirectArgumentsAddress));
        Assert.Equal((Indices + 8, 0, 0u), (runner.Host.IndexedDraws[0].IndexAddress, runner.Host.IndexedDraws[0].BaseVertex, runner.Host.IndexedDraws[0].FirstInstance));
        // A base the packet names no register for still reaches the draw.
        Assert.Equal((7, 0u), (runner.Host.IndexedDraws[1].BaseVertex, runner.Host.IndexedDraws[1].FirstInstance));
        // The draw says which values are in registers.
        Assert.Equal((true, true), (auto.FirstVertexInRegister, auto.FirstInstanceInRegister));
        Assert.Equal((true, true), (runner.Host.IndexedDraws[0].BaseVertexInRegister, runner.Host.IndexedDraws[0].FirstInstanceInRegister));
        Assert.Equal((false, true), (runner.Host.IndexedDraws[1].BaseVertexInRegister, runner.Host.IndexedDraws[1].FirstInstanceInRegister));
    }

    [Fact]
    public void IndirectMulti_WritesTheDrawIndexWhenEnabled()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Arguments, new uint[] { 1, 1, 0, 3, 2, 1, 0, 4 });
        var seen = new List<(uint StartInstance, uint DrawIndex)>();
        runner.Host.OnDraw = () => seen.Add((runner.Interpreter.Registers.Shader[0xA8], runner.Interpreter.Registers.Shader[0xAA]));

        runner.Run(
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.DrawIndirectMulti, 0, 0xAAu << 16 | 0x280, 0xA8u | 1u << 27, 0, 2, 0, 0, 16, 2));

        Assert.Equal(new[] { (3u, 0u), (4u, 1u) }, seen);
        Assert.Equal(new uint[] { 1, 2 }, runner.Host.AutoDraws.Select(draw => draw.VertexCount));
        Assert.All(runner.Host.AutoDraws, draw => Assert.Equal(0u, draw.FirstInstance));
    }

    // The draw index is the draw's ordinal, so a packet that names only it leaves the arguments on the GPU.
    [Fact]
    public void IndexedIndirectMulti_KeepsArgumentsOnTheGpuWhenOnlyTheDrawIndexIsNamed()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDrawOnGpu = true;
        var seen = new List<uint>();
        runner.Host.OnDraw = () => seen.Add(runner.Interpreter.Registers.Shader[0xA8]);

        runner.Run(
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 0),
            StreamRunner.Packet(PacketOpcode.IndexBufferSize, 40),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirectMulti, 16, 0xA8u << 16 | 0x280, 0x280u | 1u << 27, 0, 2, 0, 0, 32, 2));

        Assert.Equal(new uint[] { 0, 1 }, seen);
        Assert.Equal(new[] { Arguments + 16, Arguments + 48 }, runner.Host.IndexedDraws.Select(draw => draw.IndirectArgumentsAddress));
        Assert.DoesNotContain(runner.Host.GuestReads, address => address >= Arguments && address < Arguments + 80);
    }

    // A start instance in the vertex program's live user data leaves the arguments on the GPU as a draw index does, so the
    // draws never wait for the GPU work that wrote them.
    [Fact]
    public void IndirectMulti_KeepsArgumentsOnTheGpuWhenTheStartInstanceIsInLiveUserData()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDrawOnGpu = true;
        const uint gs0 = ShaderRegisterOffset.SpiShaderUserDataGs0;

        runner.Run(
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.SpiShaderPgmRsrc2Gs, 4u << 1),
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 0),
            StreamRunner.Packet(PacketOpcode.IndexBufferSize, 40),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirectMulti, 16, (gs0 + 3) << 16 | 0x280, (gs0 + 2) | 1u << 27, 0, 2, 0, 0, 32, 2),
            StreamRunner.Packet(PacketOpcode.DrawIndirectMulti, 0, 0x280, gs0 + 1, 0, 2, 0, 0, 16, 2));

        Assert.Equal(new[] { Arguments + 16, Arguments + 48 }, runner.Host.IndexedDraws.Select(draw => draw.IndirectArgumentsAddress));
        Assert.All(runner.Host.IndexedDraws, draw => Assert.Equal((2, false), (draw.StartInstanceUserData, draw.FirstInstanceInRegister)));
        Assert.Equal(new[] { Arguments, Arguments + 16 }, runner.Host.AutoDraws.Select(draw => draw.IndirectArgumentsAddress));
        Assert.All(runner.Host.AutoDraws, draw => Assert.Equal((1, false), (draw.StartInstanceUserData, draw.FirstInstanceInRegister)));
        Assert.Equal(1u, runner.Interpreter.Registers.Shader[gs0 + 3]);
        Assert.DoesNotContain(runner.Host.GuestReads, address => address >= Arguments && address < Arguments + 80);
    }

    [Fact]
    public void IndexedIndirectDraws_LeaveArgumentsOnTheGpuWhenTheHostCan()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDrawOnGpu = true;
        runner.Host.WriteWords(Arguments + 16, new uint[] { 50, 2, 4, 7, 1 });

        runner.Run(
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 0),
            StreamRunner.Packet(PacketOpcode.IndexBufferSize, 40),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0, 0, 0x22));

        var indexed = Assert.Single(runner.Host.IndexedDraws);
        Assert.Equal((40u, Indices, Arguments + 16, DrawOffsetSource.IndirectArguments),
            (indexed.IndexCount, indexed.IndexAddress, indexed.IndirectArgumentsAddress, indexed.OffsetSource));
        Assert.DoesNotContain(runner.Host.GuestReads, address => address >= Arguments + 16 && address < Arguments + 36);

        // A later draw that inherits the instance count reads it then.
        runner.Run(StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 5, 0));
        Assert.Equal(2u, runner.Host.AutoDraws[0].InstanceCount);

        // 8-bit indices are expanded on the CPU, so their counts are read at once.
        runner.Run(
            StreamRunner.Packet(PacketOpcode.IndexType, 2),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0, 0, 0x22));
        Assert.Equal(0ul, runner.Host.IndexedDraws[1].IndirectArgumentsAddress);
        Assert.Equal(40u, runner.Host.IndexedDraws[1].IndexCount);
    }

    // A non-indexed indirect draw that names no register leaves its arguments on the GPU too.
    [Fact]
    public void AutoIndirectDraws_LeaveArgumentsOnTheGpuWhenTheHostCan()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDrawOnGpu = true;
        runner.Host.WriteWords(Arguments, new uint[] { 9, 3, 1, 2 });

        runner.Run(
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.DrawIndirect, 0, 0x280, 0, 2));

        var auto = Assert.Single(runner.Host.AutoDraws);
        Assert.Equal((Arguments, DrawOffsetSource.IndirectArguments), (auto.IndirectArgumentsAddress, auto.OffsetSource));
        Assert.DoesNotContain(runner.Host.GuestReads, address => address >= Arguments && address < Arguments + 16);

        // A later draw that inherits the instance count reads it then.
        runner.Run(StreamRunner.Packet(PacketOpcode.DrawIndexAuto, 5, 0));
        Assert.Equal(3u, runner.Host.AutoDraws[1].InstanceCount);
    }

    [Fact]
    public void UnsizedIndexedIndirectDraws_LearnTheIndexRangeBeforeUsingTheGpu()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDrawOnGpu = true;
        runner.Host.WriteWords(Arguments + 16, new uint[] { 3, 1, 2, 0, 0 });
        var draw = new[]
        {
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 0),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0, 0, 0x22),
        };

        runner.Run(draw);
        runner.Run(draw);

        Assert.Equal(2, runner.Host.IndexedDraws.Count);
        Assert.Equal((3u, Indices + 4, 0ul), (runner.Host.IndexedDraws[0].IndexCount, runner.Host.IndexedDraws[0].IndexAddress, runner.Host.IndexedDraws[0].IndirectArgumentsAddress));
        var learned = runner.Host.IndexedDraws[1];
        Assert.Equal((Arguments + 16, Indices, true), (learned.IndirectArgumentsAddress, learned.IndexAddress, learned.UnboundedIndexBuffer));
        Assert.Equal((uint)(((Indices + 10 + 0x3FFF) & ~0x3FFFul) - Indices) / 2, learned.IndexCount);
    }

    [Fact]
    public void IndirectMulti_LoopsOverTheStrideAndTheCountAddress()
    {
        var runner = new StreamRunner();
        var countAddress = StreamRunner.LabelAddress;
        runner.Host.WriteWords(Arguments, new uint[] { 1, 1, 0, 0, 0, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 0, 3, 1, 0, 0 });
        runner.Host.WriteDword(countAddress, 2);

        runner.Run(
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.DrawIndirectMulti, 0, 0, 0, 1u << 30, 5, StreamRunner.Low(countAddress), StreamRunner.High(countAddress), 32, 2),
            StreamRunner.Packet(PacketOpcode.DrawIndirectMulti, 0, 0, 0, 0, 0, 0, 0, 32, 2));

        Assert.Equal(new uint[] { 1, 2 }, runner.Host.AutoDraws.Select(draw => draw.VertexCount));
        Assert.Contains("stride is smaller", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.DrawIndirectMulti, 0, 0, 0, 0, 1, 0, 0, 8, 2)).Message);
    }

    // A start instance in one of the vertex program's live user data registers stays the draw's first instance: the draw
    // names that user data (the program stores its base instance there), the register is not written, and the host can
    // keep the arguments on the GPU. A named base vertex still reads the arguments here, and a start instance in a
    // register the program does not load is written as the CP would.
    [Fact]
    public void IndirectDraws_NameTheLiveUserRegisterOfTheirStartInstance()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDrawOnGpu = true;
        runner.Host.WriteWords(Arguments, new uint[] { 9, 3, 1, 2 });
        runner.Host.WriteWords(Arguments + 16, new uint[] { 50, 2, 4, 7, 1 });
        const uint gs0 = ShaderRegisterOffset.SpiShaderUserDataGs0;

        runner.Run(
            StreamRunner.Packet(PacketOpcode.SetShaderRegister, ShaderRegisterOffset.SpiShaderPgmRsrc2Gs, 4u << 1),
            SetBase(Arguments, dispatch: false),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(Indices), StreamRunner.High(Indices)),
            StreamRunner.Packet(PacketOpcode.IndexType, 0),
            StreamRunner.Packet(PacketOpcode.IndexBufferSize, 40),
            StreamRunner.Packet(PacketOpcode.DrawIndirect, 0, gs0 + 3, gs0, 2),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0x280, gs0 + 2, 0x22),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0x280, gs0 + 9, 0x22),
            StreamRunner.Packet(PacketOpcode.IndexType, 2),
            StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 16, 0x280, gs0 + 1, 0x22));

        var auto = Assert.Single(runner.Host.AutoDraws);
        Assert.Equal((0ul, 0u, true), (auto.IndirectArgumentsAddress, auto.FirstVertex, auto.FirstVertexInRegister));
        Assert.Equal((2u, false, 0), (auto.FirstInstance, auto.FirstInstanceInRegister, auto.StartInstanceUserData));
        Assert.Equal(3, runner.Host.IndexedDraws.Count);
        var indexed = runner.Host.IndexedDraws;
        Assert.Equal((Arguments + 16, 0u, false, 2),
            (indexed[0].IndirectArgumentsAddress, indexed[0].FirstInstance, indexed[0].FirstInstanceInRegister, indexed[0].StartInstanceUserData));
        Assert.Equal((0ul, 0u, true, -1),
            (indexed[1].IndirectArgumentsAddress, indexed[1].FirstInstance, indexed[1].FirstInstanceInRegister, indexed[1].StartInstanceUserData));
        // Byte indices have no GPU index range, so the arguments are read; the start instance still goes to the draw.
        Assert.Equal((0ul, 1u, false, 1),
            (indexed[2].IndirectArgumentsAddress, indexed[2].FirstInstance, indexed[2].FirstInstanceInRegister, indexed[2].StartInstanceUserData));
        var shader = runner.Interpreter.Registers.Shader;
        Assert.False(shader.ContainsKey(gs0) || shader.ContainsKey(gs0 + 1) || shader.ContainsKey(gs0 + 2));
        Assert.Equal((1u, 1u), (shader[gs0 + 3], shader[gs0 + 9]));
    }

    [Fact]
    public void Dispatches_DirectAndBothIndirectEncodings()
    {
        var runner = new StreamRunner();
        runner.Host.WriteWords(Arguments, new uint[] { 4, 5, 6 });

        runner.Run(
            StreamRunner.Packet(PacketOpcode.DispatchDirect, 1, 2, 3, 0x41),
            SetBase(Arguments, dispatch: true),
            StreamRunner.Packet(PacketOpcode.DispatchIndirect, 0, 0x41),
            StreamRunner.Packet(PacketOpcode.DispatchIndirect, StreamRunner.Low(Arguments), StreamRunner.High(Arguments), 0x43));

        Assert.Equal(new[] { "dispatch 0 1 2 3 41", $"dispatch 0 4 5 6 41 @{Arguments:X}", $"dispatch 0 4 5 6 43 @{Arguments:X}" }, runner.Host.Calls);
        Assert.Contains("indirect dispatch arguments base is zero", new StreamRunner().RunExpectingFatal(StreamRunner.Packet(PacketOpcode.DispatchIndirect, 0, 0x41)).Message);
        Assert.Contains("set-base packet is not supported", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetBase, 2, 0, 0)).Message);
    }

    // A host that dispatches indirectly on the GPU gets the arguments address without the
    // counts, so the interpreter never waits for a GPU readback of them. Thread-unit
    // dispatches (initiator bit 5) still read them to convert threads into groups, unless
    // the host converts them on the GPU too.
    [Fact]
    public void IndirectDispatches_LeaveWorkgroupCountsOnTheGpuWhenTheHostCan()
    {
        var runner = new StreamRunner();
        runner.Host.ResolvesIndirectDispatchOnGpu = true;
        runner.Host.WriteWords(Arguments, new uint[] { 4, 5, 6 });

        runner.Run(
            SetBase(Arguments, dispatch: true),
            StreamRunner.Packet(PacketOpcode.DispatchIndirect, 0, 0x41),
            StreamRunner.Packet(PacketOpcode.DispatchIndirect, StreamRunner.Low(Arguments), StreamRunner.High(Arguments), 0x41));

        Assert.Equal(new[] { $"dispatch 0 0 0 0 41 @{Arguments:X}", $"dispatch 0 0 0 0 41 @{Arguments:X}" }, runner.Host.Calls);
        Assert.DoesNotContain(Arguments, runner.Host.GuestReads);

        runner.Host.Calls.Clear();
        runner.Run(
            SetBase(Arguments, dispatch: true),
            StreamRunner.Packet(PacketOpcode.DispatchIndirect, 0, 0x61));
        Assert.Equal(new[] { $"dispatch 0 4 5 6 61 @{Arguments:X}" }, runner.Host.Calls);

        var threads = new StreamRunner();
        threads.Host.ResolvesIndirectDispatchOnGpu = true;
        threads.Host.ResolvesIndirectThreadDispatchOnGpu = true;
        threads.Host.WriteWords(Arguments, new uint[] { 4, 5, 6 });
        threads.Run(
            SetBase(Arguments, dispatch: true),
            StreamRunner.Packet(PacketOpcode.DispatchIndirect, 0, 0x61));
        Assert.Equal(new[] { $"dispatch 0 0 0 0 61 @{Arguments:X}" }, threads.Host.Calls);
        Assert.DoesNotContain(Arguments, threads.Host.GuestReads);
    }

    [Fact]
    public void IndexCountPackets_SetTheBufferSize()
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.IndexCount, 77), StreamRunner.Packet(PacketOpcode.IndexBufferSize, 88));

        Assert.Equal(88u, runner.Interpreter.IndexBufferSize);
    }
}

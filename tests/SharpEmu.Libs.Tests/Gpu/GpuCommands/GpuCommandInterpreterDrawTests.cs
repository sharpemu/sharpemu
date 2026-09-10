// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
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

        Assert.Equal(new[] { "dispatch 0 1 2 3 41", "dispatch 0 4 5 6 41", "dispatch 0 4 5 6 43" }, runner.Host.Calls);
        Assert.Contains("indirect dispatch arguments base is zero", new StreamRunner().RunExpectingFatal(StreamRunner.Packet(PacketOpcode.DispatchIndirect, 0, 0x41)).Message);
        Assert.Contains("set-base packet is not supported", runner.RunExpectingFatal(StreamRunner.Packet(PacketOpcode.SetBase, 2, 0, 0)).Message);
    }

    [Fact]
    public void IndexCountPackets_SetTheBufferSize()
    {
        var runner = new StreamRunner();

        runner.Run(StreamRunner.CustomPacket(Nop, PacketCustomCode.IndexCount, 77), StreamRunner.Packet(PacketOpcode.IndexBufferSize, 88));

        Assert.Equal(88u, runner.Interpreter.IndexBufferSize);
    }
}

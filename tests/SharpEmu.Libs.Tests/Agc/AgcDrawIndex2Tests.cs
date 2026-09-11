// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDrawIndex2Tests
{
    private const ulong BaseAddress = 0x2_1000_0000;
    private const ulong SubmitPacketAddress = BaseAddress + 0x40;
    private const ulong CommandAddress = BaseAddress + 0x200;

    private const uint ItIndexBase = 0x26;
    private const uint ItDrawIndex2 = 0x27;

    [Theory]
    [InlineData(0u, 0u, 0UL, 0x280u, 0x280u, 2u, 3)]
    [InlineData(1u, 0u, 0UL, 0x280u, 0x280u, 2u, 0)]
    [InlineData(1u, 1u, 0x60180707UL, 0x010C010Fu, 0x0800010Fu, 0x22u, 1)]
    [InlineData(1u, 5u, 0x160180707UL, 0x010C010Fu, 0x0800010Fu, 2u, 3)]
    public void IndexedMultiDrawPreservesCountAddressStrideAndModifier(
        uint countFromMemory, uint memoryCount, ulong modifier, uint vertexLocations,
        uint instanceLocation, uint initiator, int expectedDraws)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var commandBufferAddress = BaseAddress + 0x80;
        var stackAddress = BaseAddress + 0x500;
        WriteUInt64(memory, commandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, commandBufferAddress + 0x18, CommandAddress + 40);
        WriteUInt64(memory, stackAddress + 8, modifier);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = 16;
        context[CpuRegister.Rdx] = countFromMemory;
        context[CpuRegister.Rcx] = 3;
        context[CpuRegister.R8] = StreamRunner.LabelAddress;
        context[CpuRegister.R9] = 32;
        context[CpuRegister.Rsp] = stackAddress;
        AgcExports.DcbDrawIndexIndirectMulti(context);
        Assert.Equal(CommandAddress, context[CpuRegister.Rax]);
        Span<byte> bytes = stackalloc byte[40];
        Assert.True(memory.TryRead(CommandAddress, bytes));
        var packet = new uint[10];
        for (var index = 0; index < packet.Length; index++)
            packet[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * 4)..]);
        Assert.Equal(new uint[] { PacketHeader.Make(10, PacketOpcode.DrawIndexIndirectMulti),
            16, vertexLocations, instanceLocation, countFromMemory << 30, 3,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 32, initiator }, packet);
        AgcExports.DcbDrawIndexIndirectMultiGetSize(context);
        Assert.Equal(40UL, context[CpuRegister.Rax]);
        Span<byte> cursor = stackalloc byte[8];
        Assert.True(memory.TryRead(commandBufferAddress + 0x10, cursor));
        Assert.Equal(CommandAddress + 40, BinaryPrimitives.ReadUInt64LittleEndian(cursor));

        var runner = new StreamRunner();
        runner.Host.WriteDword(StreamRunner.LabelAddress, memoryCount);
        for (var draw = 0u; draw < 3; draw++)
            runner.Host.WriteWords(StreamRunner.DataAddress + 16 + draw * 32, new uint[] { 7 + draw, 2, 0, 4, 5 });
        Assert.Equal(SubmissionProgress.Complete, runner.Run(
            StreamRunner.Packet(PacketOpcode.SetBase, 1, StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress)),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(StreamRunner.DataAddress + 0x200), StreamRunner.High(StreamRunner.DataAddress + 0x200)), packet));
        Assert.Equal(expectedDraws, runner.Host.IndexedDraws.Count);
        for (var draw = 0; draw < expectedDraws; draw++)
        {
            Assert.Equal((uint)(7 + draw), runner.Host.IndexedDraws[draw].IndexCount);
            Assert.Equal(2u, runner.Host.IndexedDraws[draw].InstanceCount);
        }
    }

    [Theory]
    [InlineData(0UL, 0x280u, 0x280u, 2u)]
    [InlineData(0x40000100UL, 0x280u, 0x280u, 0x22u)]
    [InlineData(0x140000100UL, 0x280u, 0x280u, 2u)]
    [InlineData(0x60180705UL, 0x10Fu, 0x10Fu, 0x22u)]
    [InlineData(0xA0180605UL, 0x10Fu, 0x10Fu, 2u)]
    [InlineData(0x40180605UL, 0x8Fu, 0x8Fu, 2u)]
    public void DrawIndirectEncodesTheModifierBeforeInterpretation(
        ulong drawModifier, uint firstVertexRegister, uint firstInstanceRegister, uint drawInitiator)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var commandBufferAddress = BaseAddress + 0x80;
        WriteUInt64(memory, commandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, commandBufferAddress + 0x18, CommandAddress + 0x100);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = 16;
        context[CpuRegister.Rdx] = drawModifier;
        AgcExports.DcbDrawIndirect(context);
        Assert.Equal(CommandAddress, context[CpuRegister.Rax]);
        Span<byte> packetBytes = stackalloc byte[20];
        Assert.True(memory.TryRead(CommandAddress, packetBytes));
        var packet = new uint[5];
        for (var index = 0; index < packet.Length; index++)
        {
            packet[index] = BinaryPrimitives.ReadUInt32LittleEndian(packetBytes[(index * sizeof(uint))..]);
        }
        Assert.Equal(PacketHeader.Make(5, PacketOpcode.DrawIndirect), packet[0]);
        Assert.Equal(16u, packet[1]);
        Assert.Equal(firstVertexRegister, packet[2]);
        Assert.Equal(firstInstanceRegister, packet[3]);
        Assert.Equal(drawInitiator, packet[4]);
        var runner = new StreamRunner();
        runner.Host.WriteWords(StreamRunner.DataAddress + 16, new uint[] { 9, 3, 1, 2 });
        Assert.Equal(SubmissionProgress.Complete, runner.Run(
            StreamRunner.Packet(PacketOpcode.SetBase, 1,
                StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress)), packet));
        var draw = Assert.Single(runner.Host.AutoDraws);
        Assert.Equal((9u, 3u, 1u, 2u), (draw.VertexCount, draw.InstanceCount, draw.FirstVertex, draw.FirstInstance));
    }

    [Theory]
    [InlineData(0x40000000UL, 0x280u, 0x280u, 2u)]
    [InlineData(0x40000100UL, 0x280u, 0x280u, 0x22u)]
    [InlineData(0x140000100UL, 0x280u, 0x280u, 2u)]
    [InlineData(0x60180707UL, 0x010C010Fu, 0x0800010Fu, 0x22u)]
    public void IndexedIndirectDrawEncodesTheModifierBeforeInterpretation(
        ulong modifier, uint vertexLocations, uint instanceLocation, uint initiator)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var commandBufferAddress = BaseAddress + 0x80;
        WriteUInt64(memory, commandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, commandBufferAddress + 0x18, CommandAddress + 20);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = 0x1F4;
        context[CpuRegister.Rdx] = modifier;
        AgcExports.DcbDrawIndexIndirect(context);
        Assert.Equal(CommandAddress, context[CpuRegister.Rax]);
        Span<byte> packetBytes = stackalloc byte[20];
        Assert.True(memory.TryRead(CommandAddress, packetBytes));
        var packet = new uint[5];
        for (var index = 0; index < packet.Length; index++)
        {
            packet[index] = BinaryPrimitives.ReadUInt32LittleEndian(packetBytes[(index * sizeof(uint))..]);
        }

        Assert.Equal(new uint[] { PacketHeader.Make(5, PacketOpcode.DrawIndexIndirect),
            0x1F4, vertexLocations, instanceLocation, initiator }, packet);
        var runner = new StreamRunner();
        runner.Host.WriteWords(StreamRunner.DataAddress + 0x1F4, new uint[] { 7, 2, 3, 4, 5 });
        Assert.Equal(SubmissionProgress.Complete, runner.Run(
            StreamRunner.Packet(PacketOpcode.SetBase, 1, StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress)),
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress)), packet));
        var draw = Assert.Single(runner.Host.IndexedDraws);
        Assert.Equal(7u, draw.IndexCount);
        Assert.Equal(2u, draw.InstanceCount);
        Assert.Equal(StreamRunner.DataAddress + 6, draw.IndexAddress);
        Assert.Equal(4, draw.BaseVertex);
        Assert.Equal(5u, draw.FirstInstance);
    }

    [Theory]
    [InlineData(0x40000000UL, 3u, 0u)]
    [InlineData(0x40000100UL, 3u, 0x20u)]
    [InlineData(0x140000100UL, 3u, 0u)]
    [InlineData(0UL, 0u, 0u)]
    public void DrawIndexOffsetEncodesTheModifierBeforeInterpretation(ulong drawModifier, uint indexCount, uint expectedInitiator)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var commandBufferAddress = BaseAddress + 0x80;
        WriteUInt64(memory, commandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, commandBufferAddress + 0x18, CommandAddress + 0x100);
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = indexCount;
        context[CpuRegister.Rcx] = drawModifier;
        AgcExports.DcbDrawIndexOffset(context);
        Assert.Equal(CommandAddress, context[CpuRegister.Rax]);

        Span<byte> packetBytes = stackalloc byte[20];
        Assert.True(memory.TryRead(CommandAddress, packetBytes));
        var packet = new uint[5];
        for (var index = 0; index < packet.Length; index++)
        {
            packet[index] = BinaryPrimitives.ReadUInt32LittleEndian(packetBytes[(index * sizeof(uint))..]);
        }
        Assert.Equal(PacketHeader.Make(5, PacketOpcode.DrawIndexOffset2), packet[0]);
        Assert.Equal(Math.Max(indexCount, 1u), packet[1]);
        Assert.Equal(2u, packet[2]);
        Assert.Equal(indexCount, packet[3]);
        Assert.Equal(expectedInitiator, packet[4]);

        var runner = new StreamRunner();
        Assert.Equal(SubmissionProgress.Complete, runner.Run(
            StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress)), packet));
        if (indexCount != 0)
        {
            var draw = Assert.Single(runner.Host.IndexedDraws);
            Assert.Equal(indexCount, draw.IndexCount);
            Assert.Equal(StreamRunner.DataAddress + 4, draw.IndexAddress);
        }
    }

    [Fact]
    public void DrawIndex2UsesItsEmbeddedIndexBuffer()
    {
        const ulong staleAddress = 0x0000_0006_00AA_0000;
        const ulong embeddedAddress = 0x0000_0006_0080_AC50;
        const uint maximumIndexCount = 0x200;
        const uint drawCount = 6;

        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(3, ItIndexBase),
            unchecked((uint)staleAddress),
            (uint)(staleAddress >> 32),
            Pm4Header(6, ItDrawIndex2),
            maximumIndexCount,
            unchecked((uint)embeddedAddress),
            (uint)(embeddedAddress >> 32),
            drawCount,
            0);

        WriteUInt64(memory, SubmitPacketAddress, CommandAddress);
        WriteUInt32(memory, SubmitPacketAddress + 8, 9);
        ctx[CpuRegister.Rdi] = SubmitPacketAddress;
        AgcExports.DriverSubmitDcb(ctx);

        Assert.True(AgcExports.TryGetGraphicsIndexStateForTests(
            ctx,
            out var address,
            out var count,
            out var offset));
        Assert.Equal(embeddedAddress, address);
        Assert.Equal(drawCount, count);
        Assert.Equal(0u, offset);
    }

    private static uint Pm4Header(uint dwords, uint opcode) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8);

    private static void WriteDwords(FakeCpuMemory memory, ulong address, params uint[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            WriteUInt32(memory, address + ((ulong)index * sizeof(uint)), values[index]);
        }
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

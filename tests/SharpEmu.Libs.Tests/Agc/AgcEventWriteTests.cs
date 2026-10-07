// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcEventWriteTests
{
    [Fact]
    public void DrawCounterDumpsPublishBothHalvesOfQueryResults()
    {
        var runner = new StreamRunner();
        var begin = Build(0x39, StreamRunner.LabelAddress);
        var end = Build(0x39, StreamRunner.LabelAddress + 8);

        Assert.Equal(0x139u, begin[1]);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(begin, end));
        for (var block = 0u; block < 16; block++)
        {
            var address = StreamRunner.LabelAddress + block * 16;
            var first = runner.Host.ReadQword(address);
            var last = runner.Host.ReadQword(address + 8);
            Assert.NotEqual(0UL, first & last & (1UL << 63));
            Assert.True(last > first);
        }
    }

    [Theory]
    [InlineData(0x38u, 0x1234567800UL, 4)]
    [InlineData(0x39u, 0x1234567800UL, 4)]
    [InlineData(0x3Au, 0UL, 2)]
    [InlineData(0x10u, 0UL, 2)]
    public void DrawEventsPreservePayloadAndAdvanceCursor(uint eventType, ulong payload, int words)
    {
        var packet = Build(eventType, payload);
        Assert.Equal(words, packet.Length);
        Assert.Equal(eventType | (words == 4 ? 0x100u : 0), packet[1]);
        if (words == 4)
        {
            Assert.Equal((uint)payload, packet[2]);
            Assert.Equal((uint)(payload >> 32), packet[3]);
        }
    }

    private static uint[] Build(uint eventType, ulong payload)
    {
        const ulong memoryAddress = 0x2_1000_0000;
        const ulong commandBuffer = memoryAddress + 0x80;
        const ulong packetAddress = memoryAddress + 0x200;
        var memory = new FakeCpuMemory(memoryAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, packetAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], packetAddress + 0x100);
        Assert.True(memory.TryWrite(commandBuffer + 0x10, bytes));
        context[CpuRegister.Rdi] = commandBuffer;
        context[CpuRegister.Rsi] = eventType;
        context[CpuRegister.Rdx] = payload;

        AgcExports.DcbEventWrite(context);
        Assert.Equal(packetAddress, context[CpuRegister.Rax]);
        Assert.True(memory.TryRead(commandBuffer + 0x10, bytes[..8]));
        var length = checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(bytes) - packetAddress));
        Assert.InRange(length, 8, 16);
        Assert.True(memory.TryRead(packetAddress, bytes[..length]));
        var packet = new uint[length / 4];
        for (var i = 0; i < packet.Length; i++)
            packet[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
        return packet;
    }
}

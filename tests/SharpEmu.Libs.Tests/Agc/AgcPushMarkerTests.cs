// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcPushMarkerTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CommandArenaAddress = BaseAddress + 0x1000;
    private const ulong MarkerAddress = BaseAddress + 0x3000;

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("A", false)]
    [InlineData("A", true)]
    [InlineData("abc", true)]
    [InlineData("abcd", true)]
    [InlineData("abcde", false)]
    [InlineData("Scene/Pass/Draw 1234", true)]
    public void DcbPushMarker_WritesTheMarkerPacketTheWayTheByteByByteReaderDid(string marker, bool scanInPlace)
    {
        var packet = PushMarker(marker, scanInPlace, out var returned, out var cursorAfter);

        Assert.Equal(CommandArenaAddress, returned);
        var expected = ReferencePacket(marker);
        Assert.Equal(expected, packet);
        Assert.Equal(CommandArenaAddress + (ulong)expected.Length, cursorAfter);
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(255, false)]
    [InlineData(1000, true)]
    [InlineData(4094, true)]
    [InlineData(4094, false)]
    public void DcbPushMarker_HandlesMarkersThatDoNotFitTheStackBuffer(int length, bool scanInPlace)
    {
        var marker = new string('m', length);

        var packet = PushMarker(marker, scanInPlace, out _, out _, arenaDwords: 2048);

        Assert.Equal(ReferencePacket(marker), packet);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DcbPushMarker_RejectsAMarkerWithNoTerminatorInsideTheLimit(bool scanInPlace)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize) { SupportsCStringScan = scanInPlace };
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(MarkerAddress, Enumerable.Repeat((byte)'x', 4095).ToArray()));
        PrepareCommandBuffer(memory, arenaDwords: 2048);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = MarkerAddress;

        AgcExports.DcbPushMarker(ctx);

        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void DcbPushMarker_RejectsAnUnreadableMarker()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize) { SupportsCStringScan = true };
        var ctx = new CpuContext(memory, Generation.Gen5);
        PrepareCommandBuffer(memory, arenaDwords: 64);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = BaseAddress + 0x40_0000;

        AgcExports.DcbPushMarker(ctx);

        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    // The original implementation: header, then the marker bytes packed into dwords with zero padding.
    private static byte[] ReferencePacket(string marker)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(marker);
        var payloadDwords = Math.Max((bytes.Length + 4) / 4, 1);
        var packetDwords = payloadDwords + 1;
        var packet = new byte[packetDwords * sizeof(uint)];
        var header = 0xC0000000u |
            ((((ushort)packetDwords - 2u) & 0x3FFFu) << 16) |
            (0x10u << 8) |
            (0x0Bu << 2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet, header);
        bytes.CopyTo(packet, sizeof(uint));
        return packet;
    }

    private static byte[] PushMarker(string marker, bool scanInPlace, out ulong returned, out ulong cursorAfter, int arenaDwords = 64)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize) { SupportsCStringScan = scanInPlace };
        var ctx = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(MarkerAddress, marker);
        PrepareCommandBuffer(memory, arenaDwords);
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = MarkerAddress;

        AgcExports.DcbPushMarker(ctx);

        returned = ctx[CpuRegister.Rax];
        var expectedLength = ReferencePacket(marker).Length;
        var packet = new byte[expectedLength];
        Assert.True(memory.TryRead(returned, packet));
        Span<byte> cursor = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(CommandBufferAddress + 0x10, cursor));
        cursorAfter = BinaryPrimitives.ReadUInt64LittleEndian(cursor);
        return packet;
    }

    private static void PrepareCommandBuffer(FakeCpuMemory memory, int arenaDwords)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(value, CommandArenaAddress);
        Assert.True(memory.TryWrite(CommandBufferAddress + 0x10, value));
        BinaryPrimitives.WriteUInt64LittleEndian(value, CommandArenaAddress + (ulong)arenaDwords * sizeof(uint));
        Assert.True(memory.TryWrite(CommandBufferAddress + 0x18, value));
    }
}

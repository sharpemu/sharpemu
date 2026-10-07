// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcMarkerPacketTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Draw")]
    [InlineData("Menu marker")]
    public void SetMarkerWritesTerminatedPaddedPacketAndAdvancesCursor(string marker)
    {
        const ulong baseAddress = 0x210000000;
        const ulong commandBufferAddress = baseAddress + 0x80;
        const ulong packetAddress = baseAddress + 0x200;
        const ulong markerAddress = baseAddress + 0x500;
        var memory = new FakeCpuMemory(baseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(commandBufferAddress + 0x10, packetAddress));
        Assert.True(context.TryWriteUInt64(commandBufferAddress + 0x18, packetAddress + 0x100));
        Assert.True(memory.TryWrite(markerAddress, Encoding.ASCII.GetBytes(marker + "\0")));
        context[CpuRegister.Rdi] = commandBufferAddress;
        context[CpuRegister.Rsi] = markerAddress;
        context[CpuRegister.Rdx] = 0x123456;

        AgcExports.SetDrawCommandBufferMarker(context);

        var payloadDwords = (marker.Length + 4) / 4;
        var bytes = new byte[(payloadDwords + 1) * 4];
        Assert.True(memory.TryRead(packetAddress, bytes));
        Assert.Equal(0xC000102Cu | ((uint)(payloadDwords - 1) << 16),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(Encoding.ASCII.GetBytes(marker), bytes[4..(4 + marker.Length)]);
        Assert.All(bytes[(4 + marker.Length)..], value => Assert.Equal((byte)0, value));
        Assert.Equal(packetAddress, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt64(commandBufferAddress + 0x10, out var cursor));
        Assert.Equal(packetAddress + (ulong)bytes.Length, cursor);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDrawIndex2Tests
{
    private const ulong BaseAddress = 0x2_1000_0000;
    private const ulong SubmitPacketAddress = BaseAddress + 0x40;
    private const ulong CommandAddress = BaseAddress + 0x200;

    private const uint ItIndexBase = 0x26;
    private const uint ItDrawIndex2 = 0x27;

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
        Assert.Equal(maximumIndexCount, count);
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

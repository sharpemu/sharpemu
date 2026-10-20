// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcCommandBufferConcurrencyTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong MarkerStringAddress = BaseAddress + 0x200;
    private const ulong PacketAddress = BaseAddress + 0x1000;
    private const int PacketsPerThread = 1000;
    private const int PacketBytes = 2 * sizeof(uint);

    // Both threads read the cursor, add their packet and store it back; without
    // serialization they hand out the same address and one packet overwrites the other.
    [Fact]
    public void ConcurrentAppends_ToOneCommandBuffer_LoseNoPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x10000);
        var capacity = 2 * PacketsPerThread * PacketBytes;
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + (ulong)capacity);

        using var start = new ManualResetEventSlim();
        var threads = new[] { 0x16u, 0x17u }
            .Select(eventType => new Thread(() =>
            {
                var ctx = new CpuContext(memory, Generation.Gen5);
                start.Wait();
                for (var index = 0; index < PacketsPerThread; index++)
                {
                    ctx[CpuRegister.Rdi] = CommandBufferAddress;
                    ctx[CpuRegister.Rsi] = eventType;
                    ctx[CpuRegister.Rdx] = 0;
                    AgcExports.DcbEventWrite(ctx);
                }
            }))
            .ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        start.Set();
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }

        var counts = new Dictionary<uint, int>();
        for (var index = 0; index < 2 * PacketsPerThread; index++)
        {
            var address = PacketAddress + (ulong)(index * PacketBytes);
            Assert.Equal(0xC000_4600u, ReadUInt32(memory, address));
            var eventType = ReadUInt32(memory, address + 4);
            counts[eventType] = counts.GetValueOrDefault(eventType) + 1;
        }

        Assert.Equal(PacketsPerThread, counts[0x16]);
        Assert.Equal(PacketsPerThread, counts[0x17]);
        Assert.Equal(PacketAddress + (ulong)capacity, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void PushMarker_FromAnotherWriter_DoesNotReserveSpaceInTheOwnersBuffer()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x10000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x400);
        Assert.True(memory.TryWrite(MarkerStringAddress, Encoding.ASCII.GetBytes("marker\0")));

        var owner = new CpuContext(memory, Generation.Gen5);
        owner[CpuRegister.Rdi] = CommandBufferAddress;
        owner[CpuRegister.Rsi] = 0x16;
        owner[CpuRegister.Rdx] = 0;
        AgcExports.DcbEventWrite(owner);
        var cursorAfterOwner = ReadUInt64(memory, CommandBufferAddress + 0x10);

        var foreign = new CpuContext(memory, Generation.Gen5);
        foreign[CpuRegister.Rdi] = CommandBufferAddress;
        foreign[CpuRegister.Rsi] = MarkerStringAddress;
        AgcExports.DcbPushMarker(foreign);

        Assert.Equal(0ul, foreign[CpuRegister.Rax]);
        Assert.Equal(cursorAfterOwner, ReadUInt64(memory, CommandBufferAddress + 0x10));

        // The owner's own marker is still written, and so is a marker once an ordinary
        // export hands the buffer to the other writer.
        owner[CpuRegister.Rdi] = CommandBufferAddress;
        owner[CpuRegister.Rsi] = MarkerStringAddress;
        AgcExports.DcbPushMarker(owner);
        Assert.Equal(cursorAfterOwner, owner[CpuRegister.Rax]);

        foreign[CpuRegister.Rdi] = CommandBufferAddress;
        foreign[CpuRegister.Rsi] = 0x17;
        foreign[CpuRegister.Rdx] = 0;
        AgcExports.DcbEventWrite(foreign);
        Assert.NotEqual(0ul, foreign[CpuRegister.Rax]);
        foreign[CpuRegister.Rdi] = CommandBufferAddress;
        foreign[CpuRegister.Rsi] = MarkerStringAddress;
        AgcExports.DcbPushMarker(foreign);
        Assert.NotEqual(0ul, foreign[CpuRegister.Rax]);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutFlipStatusTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;
    private const int FlipStatusSize = 0x40;

    [Fact]
    public void GetFlipStatus_WritesFullStructureWithZeroPendingFlips()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);

        try
        {
            // Sentinel canvas: every field of the 0x40-byte SceVideoOutFlipStatus
            // must be written; the byte after it must stay untouched. Ghost of
            // Yōtei's present loop polls flipPendingNum at +0x34, which stays
            // sentinel garbage (an infinite poll) if the write is short.
            var sentinel = new byte[FlipStatusSize + 1];
            Array.Fill(sentinel, (byte)0xCC);
            Assert.True(memory.TryWrite(StatusAddress, sentinel));

            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(context));

            Assert.Equal(0UL, ReadUInt64(memory, StatusAddress + 0x00)); // count
            Assert.Equal(0UL, ReadUInt64(memory, StatusAddress + 0x20)); // submitTsc
            Assert.Equal(0UL, ReadUInt64(memory, StatusAddress + 0x28)); // reserved0
            Assert.Equal(0u, ReadUInt32(memory, StatusAddress + 0x30)); // gcQueueNum
            Assert.Equal(0u, ReadUInt32(memory, StatusAddress + 0x34)); // flipPendingNum
            Assert.Equal(uint.MaxValue, ReadUInt32(memory, StatusAddress + 0x38)); // currentBuffer (none yet)
            Assert.Equal(0u, ReadUInt32(memory, StatusAddress + 0x3C)); // reserved1

            var tail = new byte[1];
            Assert.True(memory.TryRead(StatusAddress + FlipStatusSize, tail));
            Assert.Equal(0xCC, tail[0]);
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            VideoOutExports.VideoOutClose(context);
        }
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }
}

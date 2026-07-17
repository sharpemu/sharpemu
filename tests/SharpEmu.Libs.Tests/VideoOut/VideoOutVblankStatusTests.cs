// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutVblankStatusTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;
    private const int StatusSize = 0x28;

    [Theory]
    [InlineData(Generation.Gen4, 0x10, 0x18)]
    [InlineData(Generation.Gen5, 0x18, 0x10)]
    public void GetVblankStatus_WritesGenerationCounterOffset(
        Generation generation,
        int counterOffset,
        int reservedOffset)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, generation);
        var handle = OpenPort(context);

        try
        {
            var sentinel = new byte[StatusSize + 1];
            Array.Fill(sentinel, (byte)0xCC);
            Assert.True(memory.TryWrite(StatusAddress, sentinel));

            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetVblankStatus(context));

            var status = new byte[StatusSize + 1];
            Assert.True(memory.TryRead(StatusAddress, status));
            Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(counterOffset)) > 0);
            Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(reservedOffset)));
            Assert.All(status.AsSpan(0x20, 0x08).ToArray(), value => Assert.Equal(0, value));
            Assert.Equal(0xCC, status[StatusSize]);
        }
        finally
        {
            ClosePort(context, handle);
        }
    }

    private static int OpenPort(CpuContext context)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);
        return handle;
    }

    private static void ClosePort(CpuContext context, int handle)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(0, VideoOutExports.VideoOutClose(context));
    }
}

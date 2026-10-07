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
    private const int StatusSize = 0x80;

    [Fact]
    public void GetFlipStatus_WritesTheCompleteGen5Layout()
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
            var sentinel = new byte[StatusSize + 1];
            Array.Fill(sentinel, (byte)0xCC);
            Assert.True(memory.TryWrite(StatusAddress, sentinel));

            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(context));

            var status = new byte[StatusSize + 1];
            Assert.True(memory.TryRead(StatusAddress, status));
            Assert.Equal(-1L, BinaryPrimitives.ReadInt64LittleEndian(status.AsSpan(0x18)));
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(0x38)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(0x34)));
            Assert.All(status.AsSpan(0, StatusSize).ToArray()
                .Where((_, index) => index is < 0x18 or >= 0x20 and < 0x38 or >= 0x3C)
                .ToArray(), value => Assert.Equal(0, value));
            Assert.Equal(0xCC, status[StatusSize]);
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ = VideoOutExports.VideoOutClose(context);
        }
    }
}

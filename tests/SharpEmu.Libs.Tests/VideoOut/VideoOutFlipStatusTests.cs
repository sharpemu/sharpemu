// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutFlipStatusTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;
    private const long FlipArg = 0x1122_3344_5566_7788;

    [Theory]
    [InlineData(Generation.Gen4, 0x40)]
    [InlineData(Generation.Gen5, 0x80)]
    public void GetFlipStatus_WritesGenerationLayout(Generation generation, int statusSize)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, generation);
        var handle = OpenPort(context);

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = ulong.MaxValue;
            context[CpuRegister.Rdx] = 1;
            context[CpuRegister.Rcx] = unchecked((ulong)FlipArg);
            Assert.Equal(0, VideoOutExports.VideoOutSubmitFlip(context));

            var sentinel = new byte[0x81];
            Array.Fill(sentinel, (byte)0xCC);
            Assert.True(memory.TryWrite(StatusAddress, sentinel));

            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(context));

            var status = new byte[statusSize + 1];
            Assert.True(memory.TryRead(StatusAddress, status));
            Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(status));
            Assert.Equal(FlipArg, BinaryPrimitives.ReadInt64LittleEndian(status.AsSpan(0x18)));
            Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(0x38)));

            Assert.All(status.AsSpan(0x08, 0x10).ToArray(), value => Assert.Equal(0, value));
            Assert.All(status.AsSpan(0x20, 0x18).ToArray(), value => Assert.Equal(0, value));
            Assert.All(status.AsSpan(0x3C, statusSize - 0x3C).ToArray(), value => Assert.Equal(0, value));
            Assert.Equal(0xCC, status[statusSize]);
        }
        finally
        {
            ClosePort(context, handle);
        }
    }

    [Theory]
    [InlineData(Generation.Gen4, 0x40)]
    [InlineData(Generation.Gen5, 0x80)]
    public void GetFlipStatus_UnwritableLayoutReturnsMemoryFault(
        Generation generation,
        int statusSize)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, generation);
        var handle = OpenPort(context);

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = MemoryBase + 0x100 - (ulong)statusSize + 1;

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                VideoOutExports.VideoOutGetFlipStatus(context));
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

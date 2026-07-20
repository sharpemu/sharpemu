// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutFlipStatusTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;
    private const int SceVideoOutBusTypeMain = 0;

    // SceVideoOutFlipStatus field offsets.
    private const int CountOffset = 0x00;
    private const int FlipArgOffset = 0x18;
    private const int SubmitTscOffset = 0x20;
    private const int CurrentBufferOffset = 0x38;

    [Fact]
    public void GetFlipStatus_WritesCurrentBufferAndFlipArgAtDocumentedOffsets()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        // Open a main video-out port; a freshly opened port has CurrentBuffer -1.
        context[CpuRegister.Rdi] = 0; // userId
        context[CpuRegister.Rsi] = SceVideoOutBusTypeMain;
        context[CpuRegister.Rdx] = 0; // index
        var handle = VideoOutExports.VideoOutOpen(context);
        Assert.True(handle > 0);

        // Sentinel-fill the status structure so a field the export fails to
        // populate is distinguishable from one it deliberately writes.
        var sentinel = new byte[0x40];
        Array.Fill(sentinel, (byte)0xCC);
        Assert.True(memory.TryWrite(StatusAddress, sentinel));

        context[CpuRegister.Rdi] = (ulong)handle;
        context[CpuRegister.Rsi] = StatusAddress;
        Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(context));

        var buffer = new byte[0x40];
        Assert.True(memory.TryRead(StatusAddress, buffer));

        // count starts at 0.
        Assert.Equal(0UL, BitConverter.ToUInt64(buffer, CountOffset));
        // No flip submitted yet, so flipArg is 0 (not the sentinel).
        Assert.Equal(0UL, BitConverter.ToUInt64(buffer, FlipArgOffset));
        // currentBuffer must land in its own field (0x38), reported as -1.
        Assert.Equal(0xFFFF_FFFFu, BitConverter.ToUInt32(buffer, CurrentBufferOffset));
        // The submitTsc slot (0x20) must not be clobbered with currentBuffer,
        // which is the bug this test guards against.
        Assert.Equal(0UL, BitConverter.ToUInt64(buffer, SubmitTscOffset));
    }
}

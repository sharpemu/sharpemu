// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class SubmittedVertexDataTests
{
    private const ulong Address = 0x1_0000_0000;

    [Fact]
    public void CaptureKeepsSubmittedBytesAndIndependentDrawCopies()
    {
        var memory = new FakeCpuMemory(Address, 64);
        byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.True(memory.TryWrite(Address, original));
        var input = new VertexInputInfo { Buffers = [new(Address, 4, 2)] };
        Assert.True(SubmittedVertexData.TryCapture(memory, input, 8, out var snapshot));
        Assert.True(memory.TryWrite(Address, new byte[8]));
        Assert.Equal(original, snapshot!.CopyBuffer(0));
        var firstDraw = snapshot.CopyBuffer(0);
        firstDraw[0] = 99;
        Assert.Equal(original, snapshot.CopyBuffer(0));
        input.Buffers[0] = new(Address, 8, 1);
        Assert.False(snapshot.Matches(input));
    }

    [Theory]
    [InlineData(7, 0ul, 2u)]
    [InlineData(64, 60ul, 2u)]
    [InlineData(64, 0ul, uint.MaxValue)]
    public void CaptureRejectsOversizedOrUnreadableRanges(long budget, ulong offset, uint records)
    {
        var memory = new FakeCpuMemory(Address, 64);
        var input = new VertexInputInfo { Buffers = [new(Address + offset, 4, records)] };
        Assert.False(SubmittedVertexData.TryCapture(memory, input, budget, out var snapshot));
        Assert.Null(snapshot);
    }

    [Fact]
    public void CaptureCountsSharedRangesOnce()
    {
        var memory = new FakeCpuMemory(Address, 64);
        var input = new VertexInputInfo { Buffers = [new(Address, 4, 2), new(Address, 4, 2, true)] };
        Assert.True(SubmittedVertexData.TryCapture(memory, input, 8, out var snapshot));
        Assert.Equal(8, snapshot!.ByteCount);
        Assert.Equal(snapshot.CopyBuffer(0), snapshot.CopyBuffer(1));
        Assert.True(snapshot.Matches(input));
        Assert.False(snapshot.Matches(new VertexInputInfo { Buffers = [new(Address, 4, 2), new(Address, 4, 2)] }));
    }

    [Fact]
    public void CaptureChecksTheFullAttributeLayout()
    {
        var memory = new FakeCpuMemory(Address, 64);
        var input = new VertexInputInfo
        {
            Buffers = [new(Address, 4, 2)],
            Attributes = [new(default, 0, 1, 0, 0, 0, 0)],
        };
        Assert.True(SubmittedVertexData.TryCapture(memory, input, 8, out var snapshot));
        input.Attributes[0] = input.Attributes[0] with { OffsetBytes = 4 };
        Assert.False(snapshot!.Matches(input));
    }
}

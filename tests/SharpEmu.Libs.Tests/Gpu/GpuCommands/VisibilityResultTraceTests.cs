// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class VisibilityResultTraceTests
{
    [Fact]
    public void DifferentSlots_ChecksAllDepthBlocksButNotTheOtherCounter()
    {
        var data = new byte[256];
        const ulong expected = (1UL << 63) | 17;
        for (var slot = 0; slot < 16; slot++)
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(slot * 16), expected);
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data));
        data[240] = 0;
        Assert.Equal(1u << 15, VisibilityResultTrace.DifferentSlots(0x1000, expected, 0x1000, data));
    }

    [Fact]
    public void DifferentSlots_RequiresACompleteOverlappingQword()
    {
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x1001, new byte[7]));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x1008, new byte[8]));
        Assert.Equal(2u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x100F, new byte[9]));
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(0x1000, 1, 0x1100, new byte[256]));
    }

    [Fact]
    public void DifferentSlots_DoesNotWrapAnAddress()
    {
        Assert.Equal(0u, VisibilityResultTrace.DifferentSlots(ulong.MaxValue - 15, 1, 0, new byte[256]));
    }
}

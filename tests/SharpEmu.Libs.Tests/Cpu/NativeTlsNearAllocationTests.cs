// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeTlsNearAllocationTests
{
    private const ulong Entry = 0x0F20_0000;
    private const ulong Size = 0x10000;

    [Fact]
    public void OrderNearEntryCandidates_PicksNearestFreeGapAcrossBothSides()
    {
        var free = new List<(ulong Start, ulong End)>
        {
            (0x1_0000, 0x5_0000),
            (0x0F00_0000, 0x0F10_0000),
            (0x1000_0000, 0x1001_0000),
            (0x5000_0000, 0x6000_0000),
        };

        var result = DirectExecutionBackend.OrderNearEntryCandidates(free, Entry, Size, 0x1_0000, Entry + 0x7000_0000);

        Assert.Equal(0x0F0F_0000ul, result[0]);
        Assert.Equal(0x1000_0000ul, result[1]);
        Assert.Equal(0x0004_0000ul, result[2]);
        Assert.Equal(0x5000_0000ul, result[3]);
    }

    [Fact]
    public void OrderNearEntryCandidates_SkipsGapsTooSmallOrOutsideTheWindow()
    {
        var free = new List<(ulong Start, ulong End)>
        {
            (0x0F30_0000, 0x0F30_8000),
            (0x1_4E00_0000, 0x1_5000_0000),
        };

        var result = DirectExecutionBackend.OrderNearEntryCandidates(free, Entry, Size, 0x1_0000, Entry + 0x7000_0000);

        Assert.Empty(result);
    }

    [Fact]
    public void OrderNearEntryCandidates_AlignsToAllocationGranularity()
    {
        var free = new List<(ulong Start, ulong End)> { (0x0F40_1000, 0x0F42_0000) };

        var result = DirectExecutionBackend.OrderNearEntryCandidates(free, Entry, Size, 0x1_0000, Entry + 0x7000_0000);

        Assert.Equal(0x0F41_0000ul, result[0]);
    }
}

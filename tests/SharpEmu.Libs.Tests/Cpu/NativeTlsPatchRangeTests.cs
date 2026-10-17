// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeTlsPatchRangeTests
{
    [Fact]
    public void BuildTlsPatchScanRanges_UsesTheCompleteExecutableRegion()
    {
        const ulong imageBase = 0x0000_0008_0000_0000;
        const ulong imageSize = 0x0B7B_44EC;
        var regions = new[]
        {
            new VirtualMemoryRegion(
                imageBase,
                imageSize,
                fileOffset: 0,
                fileSize: imageSize,
                ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute),
        };

        var range = Assert.Single(DirectExecutionBackend.BuildTlsPatchScanRanges(regions));

        Assert.Equal(imageBase, range.Start);
        Assert.Equal(imageBase + imageSize, range.End);
        Assert.True(range.End > imageBase + 0x0800_0000);
    }

    [Fact]
    public void BuildTlsPatchScanRanges_MergesAdjacentExecutableRegions()
    {
        var regions = new[]
        {
            new VirtualMemoryRegion(0x3000, 0x1000, 0, 0x1000, ProgramHeaderFlags.Read),
            new VirtualMemoryRegion(0x1000, 0x1000, 0, 0x1000, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute),
            new VirtualMemoryRegion(0x2000, 0x1000, 0, 0x1000, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute),
        };

        var range = Assert.Single(DirectExecutionBackend.BuildTlsPatchScanRanges(regions));

        Assert.Equal(0x1000UL, range.Start);
        Assert.Equal(0x3000UL, range.End);
    }
}

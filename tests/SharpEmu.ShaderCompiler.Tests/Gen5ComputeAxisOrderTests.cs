// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ComputeAxisOrderTests
{
    [Fact]
    public void OrdinaryWorkgroupKeepsGuestAxes()
    {
        Assert.Equal(
            [0, 1, 2],
            Gen5SpirvTranslator.ComputeWorkgroupAxisOrder(8, 8, 64));
    }

    [Theory]
    [InlineData(1, 1, 256, 1, 2, 0)]
    [InlineData(0, 1, 256, 1, 2, 0)]
    [InlineData(8, 16, 128, 2, 1, 0)]
    [InlineData(256, 1, 65, 0, 2, 1)]
    public void TallZWorkgroupMovesLargestAxisToPhysicalX(
        uint sizeX,
        uint sizeY,
        uint sizeZ,
        int physicalX,
        int physicalY,
        int physicalZ)
    {
        Assert.Equal(
            [physicalX, physicalY, physicalZ],
            Gen5SpirvTranslator.ComputeWorkgroupAxisOrder(
                sizeX,
                sizeY,
                sizeZ));
    }
}

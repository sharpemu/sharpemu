// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPolygonModePolicyTests
{
    [Theory]
    [InlineData(PolygonMode.Fill, false, true)]
    [InlineData(PolygonMode.Fill, true, true)]
    [InlineData(PolygonMode.Line, false, false)]
    [InlineData(PolygonMode.Line, true, true)]
    [InlineData(PolygonMode.Point, false, false)]
    [InlineData(PolygonMode.Point, true, true)]
    public void FillModeNonSolid_GatesOnlyPointAndLine(
        PolygonMode mode,
        bool fillModeNonSolid,
        bool expected)
    {
        Assert.Equal(expected, VulkanPolygonModePolicy.IsSupported(mode, fillModeNonSolid));
    }

    [Fact]
    public void UnknownPolygonMode_IsRejected()
    {
        Assert.False(VulkanPolygonModePolicy.IsSupported((PolygonMode)99, fillModeNonSolid: true));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGraphicsSubgroupPolicyTests
{
    [Theory]
    [InlineData(32u, null, true)]
    [InlineData(64u, null, false)]
    [InlineData(64u, "1", true)]
    [InlineData(32u, "0", false)]
    [InlineData(32u, "invalid", true)]
    public void ResolvesNativeSizeAndDiagnosticOverride(
        uint nativeSubgroupSize,
        string? overrideValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanGraphicsSubgroupPolicy.Resolve(
                nativeSubgroupSize,
                overrideValue));
    }
}

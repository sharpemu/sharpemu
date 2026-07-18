// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanComputeDispatchPolicyTests
{
    [Fact]
    public void WritableGlobalBuffer_IsAComputeOutput()
    {
        Assert.True(
            VulkanVideoPresenter.HasComputeOutput(
                [],
                [new GuestMemoryBuffer(0x1234_0000, new byte[16], 16, false, Writable: true)]));
    }

    [Fact]
    public void ReadOnlyGlobalBuffer_IsNotAComputeOutput()
    {
        Assert.False(
            VulkanVideoPresenter.HasComputeOutput(
                [],
                [new GuestMemoryBuffer(0x1234_0000, new byte[16], 16, false)]));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDepthAttachmentPolicyTests
{
    private static readonly GuestDepthTarget Target = new(
        ReadAddress: 0x1000,
        WriteAddress: 0x1000,
        Width: 1920,
        Height: 1080,
        GuestFormat: 1,
        SwizzleMode: 0,
        ClearDepth: 1f,
        ReadOnly: false);

    [Fact]
    public void ExperimentalDepthDisabled_DoesNotAttachGuestDepth()
    {
        var state = new GuestDepthState(
            TestEnable: true,
            WriteEnable: true,
            CompareOp: 3);

        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            depthAttachmentsEnabled: false,
            Target,
            state));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ExperimentalDepthEnabled_AttachesForDepthWork(
        bool testEnable,
        bool writeEnable)
    {
        var state = new GuestDepthState(testEnable, writeEnable, CompareOp: 3);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(
            depthAttachmentsEnabled: true,
            Target,
            state));
    }

    [Fact]
    public void ExperimentalDepthEnabled_RequiresTargetAndDepthWork()
    {
        var state = GuestDepthState.Default;

        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            depthAttachmentsEnabled: true,
            Target,
            state));
        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            depthAttachmentsEnabled: true,
            target: null,
            new GuestDepthState(true, false, CompareOp: 3)));
    }
}

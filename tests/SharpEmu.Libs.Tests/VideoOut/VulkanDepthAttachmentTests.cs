// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDepthAttachmentTests
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
    public void GuestDepthTarget_AttachesForDepthWork()
    {
        var state = new GuestDepthState(
            TestEnable: true,
            WriteEnable: true,
            CompareOp: 3);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GuestDepthTarget_AttachesForEitherDepthOperation(
        bool testEnable,
        bool writeEnable)
    {
        var state = new GuestDepthState(testEnable, writeEnable, CompareOp: 3);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Fact]
    public void GuestDepthTarget_RequiresTargetAndDepthWork()
    {
        var state = GuestDepthState.Default;

        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            target: null,
            new GuestDepthState(true, false, CompareOp: 3)));
    }

    [Fact]
    public void GuestDepthTarget_AttachesForDepthClear()
    {
        var state = new GuestDepthState(
            TestEnable: false,
            WriteEnable: false,
            CompareOp: 7,
            ClearEnable: true);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Theory]
    [InlineData(0x41u, true)]
    [InlineData(0x40u, false)]
    public void DepthState_DecodesRenderControlClearBit(
        uint renderControl,
        bool clearEnable)
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = renderControl,
            [0x200] = 0x776,
        };

        var state = AgcExports.DecodeDepthState(registers);

        Assert.True(state.TestEnable);
        Assert.True(state.WriteEnable);
        Assert.Equal(7u, state.CompareOp);
        Assert.Equal(clearEnable, state.ClearEnable);
    }

    [Fact]
    public void DepthTargetRenderState_PreservesDepthAndViewportWithoutColorTarget()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x200] = 0x36,
            [0x10F] = BitConverter.SingleToUInt32Bits(512f),
            [0x110] = BitConverter.SingleToUInt32Bits(512f),
            [0x111] = BitConverter.SingleToUInt32Bits(-512f),
            [0x112] = BitConverter.SingleToUInt32Bits(512f),
        };
        var depthTarget = Target with { Width = 1024, Height = 1024 };

        var state = AgcExports.CreateDepthTargetRenderState(registers, depthTarget);

        Assert.True(state.Depth.TestEnable);
        Assert.True(state.Depth.WriteEnable);
        Assert.Equal(3u, state.Depth.CompareOp);
        Assert.Equal(0u, state.Blend.WriteMask);
        Assert.Equal(new GuestViewport(0, 1024, 1024, -1024, 0, 1), state.Viewport);
    }
}

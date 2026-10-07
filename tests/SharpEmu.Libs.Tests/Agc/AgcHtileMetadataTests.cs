// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcHtileMetadataTests
{

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public void DepthClearModePreservesDepthStateForMetadataClear(
        bool directClear,
        bool metadataClear,
        bool expectedAttachmentClear,
        bool expectedDepthStateSuppression)
    {
        var depthState = new GuestDepthState(
            TestEnable: true,
            WriteEnable: true,
            CompareOp: 3,
            ClearEnable: directClear);
        var depthTarget = new GuestDepthTarget(
            ReadAddress: 0x1000,
            WriteAddress: 0x1000,
            Width: 64,
            Height: 64,
            GuestFormat: 3,
            SwizzleMode: 0x18,
            ClearDepth: 1.0f,
            ReadOnly: false,
            MetadataClear: metadataClear);

        var mode = GuestDepthClearMode.Resolve(depthState, depthTarget);

        Assert.Equal(expectedAttachmentClear, mode.ClearAttachment);
        Assert.Equal(expectedDepthStateSuppression, mode.SuppressDrawDepthState);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    public void DrawAttachmentRequirementPreservesActiveDepthAndStencil(
        bool test, bool write, bool stencil, bool expected)
    {
        var state = new GuestDepthState(test, write, 3, StencilTestEnable: stencil);
        Assert.Equal(expected, state.RequiresDrawAttachment);
    }

    [Fact]
    public void DirectClearDoesNotRequireDepthOnTheFollowingColorDraw()
    {
        var state = new GuestDepthState(true, true, 3, ClearEnable: true);
        var mode = GuestDepthClearMode.Resolve(state, null);
        Assert.True(mode.ClearDepthAttachment);
        Assert.True(mode.SuppressDrawDepthState);
        var drawState = state with { TestEnable = false, WriteEnable = false, ClearEnable = false };
        Assert.False(drawState.RequiresDrawAttachment);
        Assert.True((drawState with { StencilTestEnable = true }).RequiresDrawAttachment);
    }
}

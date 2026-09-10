// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcHtileMetadataTests
{
    [Fact]
    public void ClearRequiresRegisteredMetadataAddress()
    {
        var tracker = new AgcHtileMetadataTracker();

        Assert.False(tracker.TryMarkAllLayersCleared(0x123400));

        tracker.Register(0x123400);

        Assert.True(tracker.TryMarkAllLayersCleared(0x123400));
    }

    [Fact]
    public void ClearIsConsumedOnceForEachLayer()
    {
        var tracker = new AgcHtileMetadataTracker();
        tracker.Register(0x123400);
        Assert.True(tracker.TryMarkAllLayersCleared(0x123400));

        Assert.True(tracker.TryConsumeClearedLayer(0x123400, 3));
        Assert.False(tracker.TryConsumeClearedLayer(0x123400, 3));
        Assert.True(tracker.TryConsumeClearedLayer(0x123400, 4));
    }

    [Fact]
    public void ClearDoesNotCrossMetadataAddresses()
    {
        var tracker = new AgcHtileMetadataTracker();
        tracker.Register(0x123400);
        tracker.Register(0x567800);
        Assert.True(tracker.TryMarkAllLayersCleared(0x123400));

        Assert.False(tracker.TryConsumeClearedLayer(0x567800, 0));
        Assert.True(tracker.TryConsumeClearedLayer(0x123400, 0));
    }

    [Fact]
    public void UnmappedRangeRemovesRegistrationAndPendingClear()
    {
        var tracker = new AgcHtileMetadataTracker();
        tracker.Register(0x123400);
        tracker.Register(0x567800);
        Assert.True(tracker.TryMarkAllLayersCleared(0x123400));
        Assert.True(tracker.TryMarkAllLayersCleared(0x567800));

        Assert.Equal(1, tracker.UnregisterRange(0x120000, 0x10000));

        Assert.False(tracker.IsRegistered(0x123400));
        Assert.False(tracker.TryConsumeClearedLayer(0x123400, 0));
        Assert.True(tracker.IsRegistered(0x567800));
        Assert.True(tracker.TryConsumeClearedLayer(0x567800, 0));
    }

    [Theory]
    [InlineData("BufferStoreDword", true)]
    [InlineData("TBufferStoreFormatX", true)]
    [InlineData("GlobalStoreDword", true)]
    [InlineData("FlatStoreDword", true)]
    [InlineData("BufferLoadDword", false)]
    [InlineData("GlobalLoadDword", false)]
    [InlineData("BufferAtomicAnd", false)]
    [InlineData("FlatAtomicOr", false)]
    public void MetadataClearAcceptsOnlyPureStoreOpcodes(
        string opcode,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.IsHtileMetadataWriteOnlyOpcode(opcode));
    }

    [Fact]
    public void DecoderUsesHtileHighBitsAndDepthLayer()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x010] = 0x20000003,
            [0x005] = 0x12345678,
            [0x01E] = 0x2A,
            [0x002] = 0x00001234,
        };

        Assert.True(AgcExports.TryDecodeHtileMetadataBinding(
            registers,
            out var address,
            out var layer));
        Assert.Equal(0x2A1234567800ul, address);
        Assert.Equal(0x1234u, layer);
    }

    [Fact]
    public void DecoderRejectsInactiveHtile()
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x010] = 0x00000003,
            [0x005] = 0x12345678,
        };

        Assert.False(AgcExports.TryDecodeHtileMetadataBinding(
            registers,
            out _,
            out _));
    }

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

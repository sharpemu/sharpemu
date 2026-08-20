// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcGpuCacheSemanticsTests
{
    [Fact]
    public void AcquireDecoder_UsesAcquireSpecificBitLayout()
    {
        var control = new AcquireMemGcrControl(
            (1u << 5) |
            (1u << 7) |
            (1u << 14) |
            (1u << 15) |
            (2u << 16));

        Assert.True(control.Gl2MetadataInvalidate);
        Assert.True(control.Gl0ScalarInvalidate);
        Assert.True(control.Gl2Invalidate);
        Assert.True(control.Gl2Writeback);
        Assert.Equal(2u, control.Order);
        Assert.False(control.Gl0VectorInvalidate);
    }

    [Fact]
    public void ReleaseDecoder_UsesReleaseSpecificBitLayout()
    {
        var control = new ReleaseMemGcrControl(
            (1u << 1) |
            (1u << 2) |
            (1u << 8) |
            (1u << 9) |
            (2u << 10));

        Assert.True(control.Gl2MetadataInvalidate);
        Assert.True(control.Gl0VectorInvalidate);
        Assert.True(control.Gl2Invalidate);
        Assert.True(control.Gl2Writeback);
        Assert.Equal(2u, control.Order);
        Assert.False(control.Gl1Invalidate);
    }

    [Fact]
    public void Decoders_DoNotInventOperationsFromReservedBits()
    {
        var acquire = new AcquireMemGcrControl(
            (1u << 4) | (1u << 6));
        var release = new ReleaseMemGcrControl(
            (1u << 0) | (1u << 7) | (1u << 12));

        Assert.False(acquire.HasResourceOperation);
        Assert.False(release.HasResourceOperation);
        Assert.False(release.IsKnownEncoding);
    }

    [Fact]
    public void AcquireDecoder_RestoresHardwareGl2Discard()
    {
        var semantics = new AcquireMemGcrControl(1u << 13)
            .ToSemantics(sizeIsAllMemory: false);

        Assert.Equal(AgcGpuCacheDomain.ShaderL2, semantics.Domains);
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.Discard));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.Invalidate));
        Assert.True(semantics.CoversAllMemory);
    }

    [Theory]
    [InlineData(0x000u)]
    [InlineData(0x200u)]
    [InlineData(0x30Eu)]
    [InlineData(0xF1Eu)]
    public void ReleaseDecoder_AcceptsOnlyDefinedGcrBits(uint raw) =>
        Assert.True(new ReleaseMemGcrControl(raw).IsKnownEncoding);

    [Fact]
    public void ReleaseDecoder_RecognizesPureGl2Writeback()
    {
        var semantics = new ReleaseMemGcrControl(1u << 9).ToSemantics();

        Assert.Equal(AgcGpuCacheDomain.ShaderL2, semantics.Domains);
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.MakeAvailable));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.WriteBack));
        Assert.False(semantics.Actions.HasFlag(AgcGpuCacheAction.Invalidate));
    }

    [Fact]
    public void AcquireSemantics_IncludeWritebackAndRangeScope()
    {
        var control = new AcquireMemGcrControl(
            (2u << 2) |
            (2u << 11) |
            (1u << 15));

        var semantics = control.ToSemantics(sizeIsAllMemory: false);

        Assert.Equal(AgcGpuCacheDomain.ShaderL2, semantics.Domains);
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.MakeVisible));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.WriteBack));
        Assert.False(semantics.CoversAllMemory);
    }

    [Fact]
    public void AcquireSemantics_ExpandZeroSizeOrAllRange()
    {
        var rangeAll = new AcquireMemGcrControl((1u << 14));
        var explicitAll = new AcquireMemGcrControl((2u << 11) | (1u << 14));

        Assert.True(rangeAll.ToSemantics(sizeIsAllMemory: false).CoversAllMemory);
        Assert.True(explicitAll.ToSemantics(sizeIsAllMemory: true).CoversAllMemory);
        Assert.False(explicitAll.ToSemantics(sizeIsAllMemory: false).CoversAllMemory);
    }

    [Fact]
    public void ReleaseSemantics_MakeProducerWritesAvailable()
    {
        var control = new ReleaseMemGcrControl(
            (1u << 1) |
            (1u << 2) |
            (1u << 8) |
            (1u << 9));

        var semantics = control.ToSemantics();

        Assert.True(semantics.Domains.HasFlag(AgcGpuCacheDomain.Metadata));
        Assert.True(semantics.Domains.HasFlag(AgcGpuCacheDomain.Vector));
        Assert.True(semantics.Domains.HasFlag(AgcGpuCacheDomain.ShaderL2));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.MakeAvailable));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.Invalidate));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.WriteBack));
    }

    [Fact]
    public void CbDbAction_RemainsSeparateAndConservative()
    {
        Assert.True(new EndOfPipeCbDbAction(0x28).IsNone);
        Assert.True(new EndOfPipeCbDbAction(0x28).IsKnown);

        var semantics = new EndOfPipeCbDbAction(0x38).ToSemantics();
        Assert.True(semantics.Domains.HasFlag(AgcGpuCacheDomain.Color));
        Assert.True(semantics.Domains.HasFlag(AgcGpuCacheDomain.Depth));
        Assert.True(semantics.Domains.HasFlag(AgcGpuCacheDomain.Metadata));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.WriteBack));
    }

    [Theory]
    [InlineData(0x04u, true, true, false)]
    [InlineData(0x14u, true, true, true)]
    [InlineData(0x2Bu, false, true, true)]
    [InlineData(0x2Du, true, false, true)]
    public void CbDbAction_DecodesDefinedOperations(
        uint raw,
        bool hasColor,
        bool hasDepth,
        bool invalidates)
    {
        var action = new EndOfPipeCbDbAction(raw);
        var semantics = action.ToSemantics();

        Assert.True(action.IsKnown);
        Assert.Equal(hasColor, semantics.Domains.HasFlag(AgcGpuCacheDomain.Color));
        Assert.Equal(hasDepth, semantics.Domains.HasFlag(AgcGpuCacheDomain.Depth));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.MakeAvailable));
        Assert.True(semantics.Actions.HasFlag(AgcGpuCacheAction.WriteBack));
        Assert.Equal(
            invalidates,
            semantics.Actions.HasFlag(AgcGpuCacheAction.Invalidate));
    }

    [Theory]
    [InlineData(0x2Fu, "cs_done")]
    [InlineData(0x30u, "ps_done")]
    public void ReleaseDecoder_SeparatesShaderCompletionFromCbDbAction(
        uint raw,
        string name)
    {
        var control = AgcExports.DecodeAgcReleaseMemCacheControl(raw, 0);

        Assert.True(control.IsEndOfShaderAction);
        Assert.False(control.IsEndOfPipeAction);
        Assert.Equal(name, control.ActionName);
        Assert.Equal(AgcGpuCacheDomain.None, control.ActionSemantics.Domains);
        Assert.Equal(
            AgcGpuCacheAction.MakeAvailable,
            control.ActionSemantics.Actions);
    }
}

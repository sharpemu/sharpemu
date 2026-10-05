// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

// The context lifecycle over the typed banks: push copies, pop restores, clear returns to the defaults.
public sealed class RegisterBanksTests
{
    private static RegisterBanks NewBanks() => new(static message => new InvalidOperationException(message));

    [Fact]
    public void Defaults_MatchTheHardwareResetValues()
    {
        var context = NewBanks().Context;

        Assert.Equal(1f, context.LineWidth);
        Assert.Equal(0xFFFF_FFFFu, context.PrimitiveResetIndex);
        Assert.Equal(1f, context.DepthBoundsMax);
        Assert.Equal(1087u, context.ScreenViewport.TransformControl);
        Assert.Equal(0xFFFF, context.ScreenViewport.ClipRectangleRule);
        Assert.Equal(1, context.ColorControl.Mode);
        Assert.Equal(0xCC, context.ColorControl.LogicOperation);
        Assert.True(context.ScanMode.ViewportScissorEnable);
        Assert.Equal(-23, context.PolygonOffset.NegativeDepthBits);
        Assert.True(context.PolygonOffset.DepthIsFloat);
        Assert.All(context.BlendControls, static blend =>
        {
            Assert.True(blend.SeparateAlpha);
            Assert.Equal(1, blend.ColorSourceFactor);
            Assert.Equal(1, blend.AlphaSourceFactor);
        });
        Assert.Equal(64, NewBanks().Shader.Compute.WaveSize);
    }

    [Fact]
    public void PushCopiesAndPopRestoresTheWholeContext()
    {
        var banks = NewBanks();
        banks.Context.RenderTargetMask = 0xF;
        banks.Context.ScreenViewport.Viewports[2].XScale = 4f;
        banks.CompositeDepthSizeXy = 9;

        banks.ApplyContextState(ContextStateOperation.Push);
        banks.Context.RenderTargetMask = 0x1;
        banks.Context.ScreenViewport.Viewports[2].XScale = 8f;
        banks.CompositeDepthSizeXy = null;
        Assert.True(banks.ContextPushed);
        banks.ApplyContextState(ContextStateOperation.Pop);

        Assert.Equal(0xFu, banks.Context.RenderTargetMask);
        Assert.Equal(4f, banks.Context.ScreenViewport.Viewports[2].XScale);
        Assert.Equal(9u, banks.CompositeDepthSizeXy);
        Assert.False(banks.ContextPushed);
    }

    [Fact]
    public void PushClearSavesThenResetsAndClearResetsInPlace()
    {
        var banks = NewBanks();
        banks.Context.RenderTargetMask = 0xF;
        banks.Context.LineWidth = 3f;

        banks.ApplyContextState(ContextStateOperation.PushClear);
        Assert.Equal(0u, banks.Context.RenderTargetMask);
        Assert.Equal(1f, banks.Context.LineWidth);
        banks.ApplyContextState(ContextStateOperation.Pop);
        Assert.Equal(0xFu, banks.Context.RenderTargetMask);

        banks.ApplyContextState(ContextStateOperation.Clear);
        Assert.Equal(0u, banks.Context.RenderTargetMask);
        Assert.Equal(1f, banks.Context.LineWidth);
    }

    [Fact]
    public void UnbalancedPushAndPop_AreFatal()
    {
        var banks = NewBanks();

        Assert.Contains("not pushed", Assert.Throws<InvalidOperationException>(() => banks.ApplyContextState(ContextStateOperation.Pop)).Message);
        banks.ApplyContextState(ContextStateOperation.Push);
        Assert.Contains("already pushed", Assert.Throws<InvalidOperationException>(() => banks.ApplyContextState(ContextStateOperation.Push)).Message);
        Assert.Contains("already pushed", Assert.Throws<InvalidOperationException>(() => banks.ApplyContextState(ContextStateOperation.PushClear)).Message);
        Assert.Contains("unknown", Assert.Throws<InvalidOperationException>(() => banks.ApplyContextState((ContextStateOperation)7)).Message);
    }

    [Fact]
    public void Reset_ReturnsEveryBankAndMarkerToTheDefaults()
    {
        var banks = NewBanks();
        banks.Context.RenderTargetMask = 0xF;
        banks.Shader.Pixel.Address = 0x1000;
        banks.UserConfig.PrimitiveType = 4;
        banks.UserDataMarker = UserScalarKind.BufferResource;
        banks.IndexTypeAndSize = 1;
        banks.ApplyContextState(ContextStateOperation.Push);

        banks.Reset();

        Assert.Equal(0u, banks.Context.RenderTargetMask);
        Assert.Equal(0ul, banks.Shader.Pixel.Address);
        Assert.Equal(0u, banks.UserConfig.PrimitiveType);
        Assert.Equal(UserScalarKind.Unknown, banks.UserDataMarker);
        Assert.Equal(0u, banks.IndexTypeAndSize);
        Assert.False(banks.ContextPushed);
    }
}

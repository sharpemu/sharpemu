// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class HostVideoOptionsTests
{
    [Fact]
    public void NormalizeClampsUnsafeHostValues()
    {
        var options = new HostVideoOptions
        {
            Width = 1,
            Height = 99_999,
            DisplayIndex = -2,
            RefreshRate = 5000,
            HdrMode = (HostHdrMode)99,
            GuestResolution = (GuestDisplayResolution)99,
        }.Normalize();

        Assert.Equal(640, options.Width);
        Assert.Equal(16384, options.Height);
        Assert.Equal(0, options.DisplayIndex);
        Assert.Equal(1000, options.RefreshRate);
        Assert.Equal(HostHdrMode.Auto, options.HdrMode);
        Assert.Equal(GuestDisplayResolution.Hd, options.GuestResolution);
    }

    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.75f, 0.75f)]
    [InlineData(1.0f, 1.0f)]
    [InlineData(2.0f, 2.0f)]
    [InlineData(0.0f, HostVideoOptions.MinimumRenderScale)]
    [InlineData(-3.0f, HostVideoOptions.MinimumRenderScale)]
    [InlineData(9.0f, HostVideoOptions.MaximumRenderScale)]
    [InlineData(float.NaN, 1.0f)]
    [InlineData(float.PositiveInfinity, 1.0f)]
    [InlineData(0.333f, 0.33f)]
    public void NormalizeClampsRenderScale(float requested, float expected)
    {
        Assert.Equal(expected, new HostVideoOptions { RenderScale = requested }.Normalize().RenderScale);
        Assert.Equal(expected, HostVideoOptions.NormalizeRenderScale(requested));
    }

    [Fact]
    public void RenderScaleDefaultsToNative()
    {
        Assert.Equal(1.0f, HostVideoOptions.Default.RenderScale);
        Assert.Equal(1.0f, HostVideoOptions.Default.Normalize().RenderScale);
    }
    [Theory]
    [InlineData(1920, 1080, GuestDisplayResolution.UltraHd)]
    [InlineData(3840, 2160, GuestDisplayResolution.Hd)]
    public void GuestModeIsIndependentOfWindowSize(int width, int height, GuestDisplayResolution mode)
    {
        var options = new HostVideoOptions { Width = width, Height = height, GuestResolution = mode }.Normalize();
        Assert.Equal(mode, options.GuestResolution);
        Assert.Equal(GuestDisplayResolution.Hd, HostVideoOptions.Default.GuestResolution);
        Span<byte> status = stackalloc byte[0x30];
        VideoOutExports.WriteOutputStatus(status, options.GuestResolution, 60, false);
        Assert.Equal((int)mode, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(status));
    }
}

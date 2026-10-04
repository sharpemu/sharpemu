// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class RenderScalePolicyTests
{
    private const float Half = 0.5f;

    // Without a device nothing can be resized; these tests ask about the geometry rules.
    static RenderScalePolicyTests() => RenderScalePolicy.ConfigureFormatSupport(_ => true);

    private static ImageDescription ColorTarget(uint width = 1920, uint height = 1080)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(0x1_0000, (ulong)width * height * 4);
        description.PixelFormat = Format.R8G8B8A8Unorm;
        description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UNorm;
        description.Extent = new Extent3D(width, height, 1);
        description.Pitch = width;
        description.BytesPerBlock = 4;
        description.TileMode = GuestTileMode.RenderTarget;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = description.Data.Size, Pitch = width, Height = height };
        return description;
    }

    [Fact]
    public void ScreenClassRenderTargetsScale()
    {
        Assert.Equal(Half, RenderScalePolicy.ScaleFor(ColorTarget(), Half));
        Assert.Equal(Half, RenderScalePolicy.ScaleFor(ColorTarget(3840, 2160), Half));
        Assert.Equal(2.0f, RenderScalePolicy.ScaleFor(ColorTarget(2304, 1296), 2.0f));
    }

    [Fact]
    public void NativeScaleNeverChangesAnything()
    {
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(ColorTarget(), 1.0f));
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(ColorTarget(640, 360), 1.0f));
    }

    [Fact]
    public void DepthTargetsScale()
    {
        var description = ColorTarget();
        description.PixelFormat = Format.D32Sfloat;
        description.GuestFormat = GuestPixelFormat.Bits32Float;
        description.TileMode = GuestTileMode.Depth;
        Assert.Equal(Half, RenderScalePolicy.ScaleFor(description, Half));
    }

    [Theory]
    [InlineData(1279u, 1080u)]
    [InlineData(1920u, 719u)]
    [InlineData(64u, 64u)]
    public void SmallTargetsStayNative(uint width, uint height)
    {
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(ColorTarget(width, height), Half));
    }

    [Fact]
    public void SquareTargetsStayNative()
    {
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(ColorTarget(2048, 2048), Half));
    }

    [Fact]
    public void AssetTilingsStayNative()
    {
        foreach (var tileMode in new[] { GuestTileMode.Linear, GuestTileMode.Standard64KB, GuestTileMode.Standard4KB, GuestTileMode.Prt })
        {
            var description = ColorTarget();
            description.TileMode = tileMode;
            Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(description, Half));
        }
    }

    [Fact]
    public void FootprintsTheHostCannotReproduceStayNative()
    {
        var multisample = ColorTarget();
        multisample.Samples = 4;
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(multisample, Half));

        var mipped = ColorTarget();
        mipped.Resources = new SubresourceCount(4, 1);
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(mipped, Half));

        var layered = ColorTarget();
        layered.Resources = new SubresourceCount(1, 6);
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(layered, Half));

        var volume = ColorTarget();
        volume.Type = GuestImageType.Color3D;
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(volume, Half));

        var compressed = ColorTarget();
        compressed.GuestFormat = GuestPixelFormat.Bc7UNorm;
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(compressed, Half));

        var htile = ColorTarget();
        htile.PixelFormat = Format.D32Sfloat;
        htile.GuestFormat = GuestPixelFormat.Bits32Float;
        htile.TileMode = GuestTileMode.Depth;
        htile.Metadata = new MetadataDescription { Kind = MetadataKind.Htile, Range = new GuestSpan(0x40_0000, 0x1000) };
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(htile, Half));

        var stencil = ColorTarget();
        stencil.Stencil = new GuestSpan(0x80_0000, 0x1000);
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(stencil, Half));
    }

    // DCC describes guest bytes the host image never stores, so the backing can be any size.
    [Fact]
    public void ColourCompressionMetadataStillScales()
    {
        var description = ColorTarget();
        description.Metadata = new MetadataDescription { Kind = MetadataKind.Dcc, Range = new GuestSpan(0x40_0000, 0x1000) };
        Assert.Equal(Half, RenderScalePolicy.ScaleFor(description, Half));
    }

    [Fact]
    public void HelperImagesWithoutGuestMemoryStayNative()
    {
        var description = ColorTarget();
        description.Data = default;
        Assert.Equal(1.0f, RenderScalePolicy.ScaleFor(description, Half));
    }

    [Theory]
    [InlineData(1920u, 0.5f, 960u)]
    [InlineData(1080u, 0.5f, 540u)]
    [InlineData(1281u, 0.5f, 641u)]
    [InlineData(1920u, 0.75f, 1440u)]
    [InlineData(1920u, 2.0f, 3840u)]
    [InlineData(1u, 0.5f, 1u)]
    [InlineData(0u, 0.5f, 0u)]
    public void LengthsRoundUpSoNoGuestPixelIsLost(uint length, float scale, uint expected)
    {
        Assert.Equal(expected, RenderScalePolicy.ScaleLength(length, scale));
    }

    [Fact]
    public void ExtentScalingLeavesDepthAlone()
    {
        var scaled = RenderScalePolicy.ScaleExtent(new Extent3D(1920, 1080, 4), Half);
        Assert.Equal(new Extent3D(960, 540, 4), scaled);
    }
}

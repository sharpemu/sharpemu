// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// Locks the four resolution outcomes used when binding guest depth against color RTs.
/// Wrong branch selection undersizes D/S images or skips depth entirely.
/// </summary>
public sealed class GuestDepthExtentResolverTests
{
    [Fact]
    public void Resolve_DepthCoversColor_IsExact()
    {
        var depth = MakeDepth(width: 1920, height: 1080);
        var result = GuestDepthExtentResolver.Resolve(depth, colorWidth: 1280, colorHeight: 720, textures: []);

        Assert.Equal(GuestDepthExtentResolutionKind.Exact, result.Kind);
        Assert.True(result.IsUsable);
        Assert.Equal(1920u, result.Width);
        Assert.Equal(1080u, result.Height);
    }

    [Fact]
    public void Resolve_MatchingTextureAlias_UsesTextureExtent()
    {
        // Depth is smaller than color, but a texture at the depth address has full size.
        var depth = MakeDepth(width: 64, height: 64, writeAddress: 0x1000, readAddress: 0x1000);
        var textures = new[]
        {
            MakeTexture(address: 0x1000, width: 1920, height: 1080),
        };

        var result = GuestDepthExtentResolver.Resolve(depth, colorWidth: 1920, colorHeight: 1080, textures);

        Assert.Equal(GuestDepthExtentResolutionKind.TextureAlias, result.Kind);
        Assert.True(result.IsUsable);
        Assert.Equal(1920u, result.Width);
        Assert.Equal(1080u, result.Height);
    }

    [Fact]
    public void Resolve_TextureMustMatchDepthAddress_AndCoverColor()
    {
        var depth = MakeDepth(width: 64, height: 64, writeAddress: 0x1000, readAddress: 0x1000);
        // Wrong address: no alias.
        var wrongAddress = new[]
        {
            MakeTexture(address: 0x2000, width: 1920, height: 1080),
        };
        var mismatch = GuestDepthExtentResolver.Resolve(depth, 1920, 1080, wrongAddress);
        Assert.Equal(GuestDepthExtentResolutionKind.Mismatch, mismatch.Kind);
        Assert.False(mismatch.IsUsable);

        // Right address but still smaller than color: no alias.
        var tooSmall = new[]
        {
            MakeTexture(address: 0x1000, width: 640, height: 360),
        };
        var stillMismatch = GuestDepthExtentResolver.Resolve(depth, 1920, 1080, tooSmall);
        Assert.Equal(GuestDepthExtentResolutionKind.Mismatch, stillMismatch.Kind);
    }

    [Fact]
    public void Resolve_StaleOneByOneDepth_FallsBackToColorExtent()
    {
        var depth = MakeDepth(width: 1, height: 1);
        var result = GuestDepthExtentResolver.Resolve(depth, colorWidth: 2560, colorHeight: 1440, textures: []);

        Assert.Equal(GuestDepthExtentResolutionKind.StaleOneByOne, result.Kind);
        Assert.True(result.IsUsable);
        Assert.Equal(2560u, result.Width);
        Assert.Equal(1440u, result.Height);
    }

    [Fact]
    public void Resolve_UndersizedDepthWithoutAlias_IsMismatch()
    {
        var depth = MakeDepth(width: 640, height: 360);
        var result = GuestDepthExtentResolver.Resolve(depth, colorWidth: 1920, colorHeight: 1080, textures: []);

        Assert.Equal(GuestDepthExtentResolutionKind.Mismatch, result.Kind);
        Assert.False(result.IsUsable);
        Assert.Equal(640u, result.Width);
        Assert.Equal(360u, result.Height);
    }

    [Fact]
    public void Resolve_ReadAddressAlias_WhenWriteAddressIsZero()
    {
        // Address property prefers WriteAddress, else ReadAddress.
        var depth = new GuestDepthTarget(
            ReadAddress: 0xABCD,
            WriteAddress: 0,
            Width: 8,
            Height: 8,
            GuestFormat: 0,
            SwizzleMode: 0,
            ClearDepth: 1f,
            ReadOnly: true);
        var textures = new[]
        {
            MakeTexture(address: 0xABCD, width: 800, height: 600),
        };

        var result = GuestDepthExtentResolver.Resolve(depth, colorWidth: 800, colorHeight: 600, textures);
        Assert.Equal(GuestDepthExtentResolutionKind.TextureAlias, result.Kind);
        Assert.Equal(800u, result.Width);
        Assert.Equal(600u, result.Height);
    }

    private static GuestDepthTarget MakeDepth(
        uint width,
        uint height,
        ulong writeAddress = 0x1000,
        ulong readAddress = 0x1000) =>
        new(
            ReadAddress: readAddress,
            WriteAddress: writeAddress,
            Width: width,
            Height: height,
            GuestFormat: 0,
            SwizzleMode: 0,
            ClearDepth: 1f,
            ReadOnly: false);

    private static GuestDrawTexture MakeTexture(ulong address, uint width, uint height) =>
        new(
            Address: address,
            Width: width,
            Height: height,
            Format: 0,
            NumberType: 0,
            RgbaPixels: Array.Empty<byte>(),
            IsFallback: false,
            IsStorage: false);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class HostMovieTextureBindingTests
{
    [Fact]
    public void RememberedAddressDoesNotSelectAuxiliaryOneByOneTexture()
    {
        const ulong lumaAddress = 0x1000;
        const ulong chromaAddress = 0x2000;
        GuestDrawTexture[] textures =
        [
            CreateTexture(lumaAddress, 3840, 2160, format: 1),
            CreateTexture(chromaAddress, 1920, 1080, format: 3),
            CreateTexture(lumaAddress, 1, 1, format: 1),
            CreateTexture(lumaAddress, 1, 1, format: 1),
        ];

        var selected = VulkanVideoPresenter.SelectRememberedHostMovieTextureBindings(
            textures,
            lumaAddress,
            chromaAddress,
            hostWidth: 1920,
            hostHeight: 1080);

        Assert.Equal(0, selected.Luma);
        Assert.Equal(1, selected.Chroma);
    }

    private static GuestDrawTexture CreateTexture(
        ulong address,
        uint width,
        uint height,
        uint format) =>
        new(
            Address: address,
            Width: width,
            Height: height,
            Format: format,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false);
}

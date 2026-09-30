// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class NativeColorClearRequestTests
{
    [Theory]
    [InlineData(GuestTileMode.RenderTarget, 0, 0, true)]
    [InlineData(GuestTileMode.Linear, 0, 0, false)]
    [InlineData(GuestTileMode.RenderTarget, 1, 0, false)]
    [InlineData(GuestTileMode.RenderTarget, 0, 1, false)]
    public void SelectsOnlySupportedTargetLayouts(GuestTileMode tile, uint maxMip, uint samplesLog2, bool expected)
    {
        var words = RegisterWords.Color(0x100000, 64, 64, tile, maxMip: maxMip,
            sliceMax: 2, sliceStart: 1, samplesLog2: samplesLog2, fragmentsLog2: samplesLog2);
        words = words with { Info = words.Info | (1u << 28), DccAddress = 0x200000 };
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false);
        Assert.NotNull(resolution);
        var metadata = resolution.Value.Request.Description.Metadata;
        Assert.Equal(expected, metadata.NativeColorClear);
        if (expected)
        {
            Assert.Equal(12288UL, metadata.Range.Size);
            Assert.Equal(1u, metadata.ColorMetadataBaseLayer);
            Assert.Equal(2u, resolution.Value.Request.View.LayerCount);
        }
    }
}

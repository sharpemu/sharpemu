// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// A streamed texture maps only the resident start of its tiled chain (the small levels; level 0 is
// last) and its descriptor starts at the first resident level. Its image holds only those levels.
public sealed partial class GuestImageCacheTests
{
    private const uint StreamedSize = 512;
    private const uint StreamedLevels = 10;

    private static readonly ShaderImageShape StreamedShape = new(false, false, false, false, TextureNumericClass.Float);

    private static ImageRequest StreamedTexture(ulong address, uint baseLevel) =>
        ImageRequestBuilders.Texture(
            RegisterWords.Texture(address, GuestPixelFormat.Bits8_8_8_8UNorm, StreamedSize, StreamedSize, tile: GuestTileMode.Standard64KB,
                baseLevel: baseLevel, lastLevel: StreamedLevels - 1, maxMip: StreamedLevels - 1),
            StreamedShape).Request;

    // The image counts the memory of its resident levels only, not of the unmapped chain before them.
    [Fact]
    public void StreamedTexture_AccountsOnlyItsResidentLevels()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var layout = StreamedTexture(0x1_0000_0000, 1).Description;
        var resident = layout.MipLayout[0].Offset / harness.Granularity * harness.Granularity;
        var address = harness.MapBackedPrefix(layout.Data.Size, resident);

        var request = StreamedTexture(address, 1);
        var image = harness.Images.Owner(harness.Find(ref request))!;

        Assert.Equal(1u, image.Description.FirstLevel);
        // Less than the chain's level 0 alone, which is not mapped.
        Assert.True(image.AccountedSize < (ulong)StreamedSize * StreamedSize * 4 / 2);
        Assert.True(image.AccountedSize >= layout.MipLayout[1].Size);
    }

    // When the whole chain is mapped (or another image holds it), the request for resident levels
    // is a view of the chain's image from the descriptor's base level, not a second image.
    [Fact]
    public void StreamedTexture_ViewsTheWholeChainWhenItIsMapped()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var size = StreamedTexture(0x1_0000_0000, 0).Description.Data.Size;
        var address = harness.MapBacked(size, ReadWrite);

        var whole = StreamedTexture(address, 0);
        var wholeIdentifier = harness.Find(ref whole);
        var streamed = StreamedTexture(address, 2);
        Assert.Equal(2u, streamed.Description.FirstLevel);
        var streamedIdentifier = harness.Find(ref streamed);

        Assert.Equal(wholeIdentifier, streamedIdentifier);
        Assert.Equal(0u, harness.Images.Owner(streamedIdentifier)!.Description.FirstLevel);
        Assert.Equal(2u, streamed.View.BaseLevel);
        Assert.Equal(StreamedLevels - 2, streamed.View.LevelCount);
    }

    // A texture whose resident image starts later than a new request is replaced by one holding
    // the requested levels; an earlier-starting one serves the later request through a view.
    [Fact]
    public void StreamedTexture_LoadingMoreLevelsReplacesTheImage()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var layout = StreamedTexture(0x1_0000_0000, 1).Description;
        var resident = layout.MipLayout[0].Offset / harness.Granularity * harness.Granularity;
        var address = harness.MapBackedPrefix(layout.Data.Size, resident);

        var coarse = StreamedTexture(address, 3);
        var coarseIdentifier = harness.Find(ref coarse);
        Assert.Equal(3u, harness.Images.Owner(coarseIdentifier)!.Description.FirstLevel);

        var finer = StreamedTexture(address, 1);
        var finerIdentifier = harness.Find(ref finer);
        Assert.NotEqual(coarseIdentifier, finerIdentifier);
        Assert.False(harness.Images.Contains(coarseIdentifier));
        Assert.Equal(1u, harness.Images.Owner(finerIdentifier)!.Description.FirstLevel);

        var coarseAgain = StreamedTexture(address, 3);
        Assert.Equal(finerIdentifier, harness.Find(ref coarseAgain));
        Assert.Equal(2u, coarseAgain.View.BaseLevel);
    }
}

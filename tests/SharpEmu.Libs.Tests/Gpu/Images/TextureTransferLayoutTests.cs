// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed class TextureTransferLayoutTests
{
    [Fact]
    public void LinearLayout_PadsRowsAndOrdersMipsSmallestFirst()
    {
        var layout = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 100, 100, 1, 1, GuestTileMode.Linear, 0, false, false, "test");
        Assert.Equal(128u, layout.Pitch);
        Assert.Equal((0UL, 51200UL, 128u, 100u), (layout.Mips[0].Offset, layout.Mips[0].Size, layout.Mips[0].RowLength, layout.Mips[0].ImageHeight));
        Assert.Equal(51200UL, layout.SliceStride);
        var copies = layout.BuildCopies();
        var copy = Assert.Single(copies);
        Assert.Equal((128u, 100u), (copy.BufferRowLength, copy.BufferImageHeight));
        Assert.Equal(new Extent3D(100, 100, 1), copy.ImageExtent);
        Assert.Equal(ImageAspectFlags.ColorBit, copy.ImageSubresource.AspectMask);

        var mips = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, 2, 1, GuestTileMode.Linear, 0, false, false, "test");
        Assert.Equal(8192UL, mips.Mips[0].Offset);
        Assert.Equal(0UL, mips.Mips[1].Offset);
        Assert.Equal(24576UL, mips.SliceStride);
        Assert.Equal(2, mips.BuildCopies().Count);
        Assert.False(mips.TryBuildTileTransfers(24576, mips.BuildCopies(), 2, out _));
    }

    [Fact]
    public void LayeredLinearLayout_UsesTheGuestSliceStrideWhenLarger()
    {
        var layout = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, 1, 3, GuestTileMode.Linear, 3 * 20480, false, false, "test");
        Assert.Equal(20480UL, layout.SliceStride);
        var copies = layout.BuildCopies();
        Assert.Equal(3, copies.Count);
        Assert.Equal(2 * 20480UL, copies[2].BufferOffset);
        Assert.Equal(2u, copies[2].ImageSubresource.BaseArrayLayer);
    }

    [Fact]
    public void TiledLayout_ProducesOneTransferPerRegion()
    {
        var layout = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 256, 256, 3, 1, GuestTileMode.Standard64KB, 0, false, false, "test");
        Assert.Equal(256u, layout.Pitch);
        Assert.Equal(2u, layout.Surface.FirstTailLevel);
        Assert.Equal(393216UL, layout.SourceSliceStride);
        Assert.Equal((0UL, 262144UL), (layout.Mips[0].Offset, layout.Mips[0].Size));
        Assert.Equal((262144UL, 65536UL), (layout.Mips[1].Offset, layout.Mips[1].Size));
        Assert.Equal((327680UL, 16384UL), (layout.Mips[2].Offset, layout.Mips[2].Size));
        Assert.Equal(344064UL, layout.SliceStride);

        var copies = layout.BuildCopies();
        Assert.Equal(3, copies.Count);
        Assert.Equal(0u, copies[0].BufferRowLength);
        Assert.Equal(262144UL, copies[1].BufferOffset);
        Assert.True(layout.TryBuildTileTransfers(393216, copies, 3, out var transfers));
        Assert.Equal(3, transfers.Count);

        var first = transfers[0];
        Assert.Equal((TileBlockKind.Standard64KB, 4u), (first.Kind, first.BytesPerElement));
        Assert.Equal((0UL, 262144UL, 131072UL, 262144UL), (first.LinearOffset, first.LinearSize, first.TiledOffset, first.TiledSize));
        Assert.Equal((256u, 256u, 1u, 256u), (first.Width, first.Height, first.Depth, first.Pitch));
        Assert.False(first.Tail);
        Assert.Equal((256u, 256u, 0u), (first.TiledWidth, first.TiledHeight, first.SurfaceZ));

        var tail = transfers[2];
        Assert.True(tail.Tail);
        Assert.Equal((64u, 0u), (tail.TailX, tail.TailY));
        Assert.Equal((327680UL, 16384UL, 0UL, 65536UL), (tail.LinearOffset, tail.LinearSize, tail.TiledOffset, tail.TiledSize));
        Assert.Equal((64u, 64u, 128u, 128u), (tail.Width, tail.Height, tail.TiledWidth, tail.TiledHeight));

        Assert.False(layout.TryBuildTileTransfers(65536, copies, 3, out _));
        Assert.False(layout.TryBuildTileTransfers(393216, copies, 0, out _));
        Assert.False(layout.TryBuildTileTransfers(393216, copies, 2, out _));
        Assert.False(layout.TryBuildTileTransfers(0, copies, 3, out _));
    }

    [Fact]
    public void LayeredTiledLayout_OffsetsEachSliceByTheBlockSlice()
    {
        var layout = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 128, 128, 1, 4, GuestTileMode.Standard64KB, 0, false, false, "test");
        Assert.Equal(65536UL, layout.SourceSliceStride);
        var copies = layout.BuildCopies();
        Assert.Equal(4, copies.Count);
        Assert.True(layout.TryBuildTileTransfers(4 * 65536, copies, 1, out var transfers));
        Assert.Equal(4, transfers.Count);
        Assert.Equal(3 * 65536UL, transfers[3].TiledOffset);
        Assert.Equal(3 * 65536UL, transfers[3].LinearOffset);
        Assert.Equal(0u, transfers[3].SurfaceZ);

        var target = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 128, 128, 1, 2, GuestTileMode.RenderTarget, 0, false, false, "test");
        Assert.True(target.TryBuildTileTransfers(2 * 65536, target.BuildCopies(), 1, out var targetTransfers));
        Assert.Equal(1u, targetTransfers[1].SurfaceZ);
        Assert.Equal(TileBlockKind.RenderTarget64KB, targetTransfers[1].Kind);
    }

    [Fact]
    public void VolumeTiledLayout_SplitsDepthIntoBlockSlabs()
    {
        var layout = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 32, 32, 1, 40, GuestTileMode.Standard64KB, 0, false, true, "test");
        Assert.Equal(TileBlockKind.Standard64KB3D, layout.Surface.Texture.Block.Kind);
        Assert.Equal(32u * 32 * 4, layout.SliceStride);
        var copies = layout.BuildCopies();
        Assert.Equal(40, copies.Count);
        Assert.Equal(7, copies[7].ImageOffset.Z);
        Assert.True(layout.TryBuildTileTransfers(3 * 65536, copies, 1, out var transfers));
        Assert.Equal(3, transfers.Count);
        Assert.Equal((16u, 16u, 8u), (transfers[0].Depth, transfers[1].Depth, transfers[2].Depth));
        Assert.Equal(2 * 65536UL, transfers[2].TiledOffset);
        Assert.Equal(32 * layout.SliceStride, transfers[2].LinearOffset);
        Assert.Equal(layout.SliceStride, transfers[2].LinearSliceStride);
    }

    [Fact]
    public void FmaskAndDepthTiles_AreRejectedWhereTheReferenceRejectsThem()
    {
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, 1, 1, GuestTileMode.Depth, 0, false, false, "owner"));
        Assert.Contains(fatal.Messages, message => message.StartsWith("owner: the tiled texture upload is not supported", StringComparison.Ordinal));
        var depth = TextureTransferLayout.Compute(GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, 1, 1, GuestTileMode.Depth, 0, true, false, "owner");
        Assert.Equal(TileBlockKind.Depth64KB, depth.Surface.Texture.Block.Kind);
        Assert.Throws<SchedulerFatalException>(() => TextureTransferLayout.Compute(GuestPixelFormat.Invalid, 64, 64, 1, 1, GuestTileMode.Linear, 0, false, false, "owner"));
        Assert.Contains(fatal.Messages, message => message.Contains("upload format is unknown"));
    }

    [Fact]
    public void RenderTargetFormats_CombineHostSwizzleAndOrder()
    {
        var standard = TextureTransferLayout.RenderTargetFormat(ChannelLayout.Bits8_8_8_8, ChannelType.UNorm, ChannelOrder.Standard);
        Assert.Equal((Format.R8G8B8A8Unorm, 4u, ColorComponentMap.Identity), (standard.HostFormat, standard.BytesPerElement, standard.ExportMapping));
        var alternate = TextureTransferLayout.RenderTargetFormat(ChannelLayout.Bits8_8_8_8, ChannelType.UNorm, ChannelOrder.Alternate);
        Assert.Equal((Format.B8G8R8A8Unorm, ColorComponentMap.Identity), (alternate.HostFormat, alternate.ExportMapping));
        var reversed = TextureTransferLayout.RenderTargetFormat(ChannelLayout.Bits8_8_8_8, ChannelType.Srgb, ChannelOrder.Reversed);
        Assert.Equal((Format.R8G8B8A8Srgb, ColorComponentMap.Abgr), (reversed.HostFormat, reversed.ExportMapping));
        var twoChannels = TextureTransferLayout.RenderTargetFormat(ChannelLayout.Bits8_8, ChannelType.UNorm, ChannelOrder.Alternate);
        Assert.Equal(ColorComponentMap.Rabg, twoChannels.ExportMapping);
        Assert.Equal(2u, twoChannels.BytesPerElement);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => TextureTransferLayout.RenderTargetFormat(ChannelLayout.Bits10_10_10_2Float, ChannelType.Float, ChannelOrder.Alternate));
        Assert.Throws<SchedulerFatalException>(() => TextureTransferLayout.RenderTargetFormat(ChannelLayout.Bits32, ChannelType.UNorm, ChannelOrder.Standard));
        Assert.All(fatal.Messages, message => Assert.Contains("render-target format combination is not supported", message));
    }

    [Fact]
    public void SurfaceFormats_RemapAndReportTheConversion()
    {
        Assert.Equal(new SurfaceFormatInfo(Format.R32Uint, GuestPixelFormat.Bits11_11_10UInt), TextureTransferLayout.SurfaceFormat(GuestPixelFormat.Bits11_11_10UInt));
        Assert.Equal(new SurfaceFormatInfo(Format.R8G8B8A8Unorm, GuestPixelFormat.Invalid), TextureTransferLayout.SurfaceFormat(GuestPixelFormat.Bits8_8_8_8UNorm));
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => TextureTransferLayout.SurfaceFormat(GuestPixelFormat.Invalid));
        Assert.Contains(fatal.Messages, message => message.Contains("has no host format"));
    }
}

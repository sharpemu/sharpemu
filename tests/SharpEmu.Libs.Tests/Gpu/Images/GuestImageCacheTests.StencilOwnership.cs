// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(false, 1u, true)]
    [InlineData(false, 2u, true)]
    [InlineData(true, 1u, true)]
    [InlineData(true, 2u, true)]
    [InlineData(false, 1u, false)]
    [InlineData(false, 2u, false)]
    [InlineData(true, 1u, false)]
    [InlineData(true, 2u, false)]
    public void StencilBufferHandoversPreserveGpuContents(bool tiled, uint layers, bool writeWindow)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        const ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.DepthStencilAttachmentBit;
        if (!_vulkan.DeviceInfo.TryGetImageFormatProperties(Format.D32SfloatS8Uint, ImageType.Type2D, ImageTiling.Optimal, usage, 0, out _)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong windowSize = 0x80000;
        var address = harness.MapBacked(windowSize, ReadWrite);
        var stencilAddress = address + 0x40000;
        var extent = tiled ? new Extent3D(128, 128, 1) : new Extent3D(4, 1, 1);
        var depthSize = (tiled ? 0x10000UL : 16UL) * layers;
        var stencilSize = (tiled ? 0x10000UL : 4UL) * layers;
        var depth = LinearRequest(address, depthSize, Format.D32SfloatS8Uint, GuestPixelFormat.Bits32Float,
            GuestImageType.Color2D, extent, layers, 4, 1);
        depth.Description.TileMode = tiled ? GuestTileMode.Depth : GuestTileMode.Linear;
        depth = AsDepthTarget(depth, Format.D32SfloatS8Uint);
        depth.Description.Stencil = new GuestSpan(stencilAddress, stencilSize);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var id = harness.Acquire(ref depth);
        Assert.True(harness.Image(harness.ProxyAt(stencilAddress, stencilSize)).IsGpuModified);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(stencilAddress, stencilSize, 0x08080808)));

        if (writeWindow) harness.Worker.Run(() =>
        {
            harness.Images.PublishImagesUnderFormattedWrite(address, windowSize);
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, windowSize, isWritten: true, isTexelBuffer: true);
            harness.Images.InvalidateMemoryFromGpuAfterPublish(address, windowSize);
            buffer.Fill(offset + windowSize - 4, 4, 0x12345678);
        });
        else
        {
            var (buffer, offset) = harness.Worker.Run(() =>
            {
                var (bound, _) = harness.Cache.ObtainBuffer(address, windowSize, isWritten: false, isTexelBuffer: true);
                Assert.True(harness.Images.TrySynchronizeBufferFromImage(bound, stencilAddress, stencilSize));
                return (bound, bound.Offset(stencilAddress));
            });
            var sliceSize = stencilSize / layers;
            for (uint layer = 0; layer < layers; layer++)
                Assert.Equal(new byte[] { 8, 8, 8, 8 }, harness.ReadBufferBytes(buffer, offset + layer * sliceSize, 4));
        }

        Assert.Equal(id, harness.Acquire(ref depth));
        var actual = harness.ReadImageBytes(harness.Image(id), ImageAspectFlags.StencilBit);
        var texels = checked((int)(extent.Width * extent.Height * layers));
        Assert.All(actual[..texels], value => Assert.Equal((byte)8, value));
        harness.Shutdown();
    }
}

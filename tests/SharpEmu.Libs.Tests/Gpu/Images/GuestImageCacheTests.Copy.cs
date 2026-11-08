// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void NativeSurfaceCopyFindsTheRenderedImageThroughACompatibleColorView()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var source = AsColorTarget(LinearRequest(address, 4, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm,
            GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1));
        _ = harness.Acquire(ref source);
        var copySource = source;
        copySource.Description.PixelFormat = Format.B8G8R8A8Unorm;
        copySource.View = copySource.View with { Format = Format.B8G8R8A8Unorm };
        var destination = copySource;
        destination.Description.Data = destination.Description.Data with { Address = address + 0x10000 };
        harness.Worker.Run(() =>
        {
            Assert.True(harness.Images.TryClearImageFromBuffer(address, 4, 0xFF3366AA));
            Assert.True(harness.Images.TryCopyImage(copySource, destination));
        });
        var id = harness.Acquire(ref destination);
        Assert.True(harness.Image(id).IsGpuModified);
        Assert.Equal(Bytes(0xFF3366AAu), harness.ReadImageBytes(harness.Image(id)));
        harness.Shutdown();
    }

    [Fact]
    public void NativeSurfaceCopyKeepsRenderedTexelsAndReplacesStaleDestinationBytes()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var source = AsColorTarget(Color32(address));
        var destination = AsColorTarget(Color32(address + 0x10000));
        var sourceId = harness.Acquire(ref source);
        var destinationId = harness.Acquire(ref destination);
        harness.Worker.Run(() =>
        {
            Assert.True(harness.Images.TryClearImageFromBuffer(address, 4, 0xFF3366AA));
            Assert.True(harness.Images.TryClearImageFromBuffer(address + 0x10000, 4, 0xFF808080));
            Assert.True(harness.Images.TryCopyImage(source, destination));
        });
        Assert.True(harness.Image(sourceId).IsGpuModified);
        Assert.True(harness.Image(destinationId).IsGpuModified);
        Assert.False(harness.Image(destinationId).IsBufferModified);
        Assert.Equal(Bytes(0xFF3366AAu), harness.ReadImageBytes(harness.Image(destinationId)));
        Assert.Equal(destinationId, harness.Acquire(ref destination));
        Assert.Equal(Bytes(0xFF3366AAu), harness.ReadImageBytes(harness.Image(destinationId)));

        // A CPU overwrite of the source makes the native GPU snapshot ineligible.
        Assert.True(harness.WriteFault(address));
        harness.Write(address, Bytes(0x12345678u));
        Assert.False(harness.Worker.Run(() => harness.Images.TryCopyImage(source, destination)));
        harness.Shutdown();
    }
}

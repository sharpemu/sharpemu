// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(false, 0, false)] // A new proxy already needs a guest upload.
    [InlineData(true, 0, false)] // A clean native image must survive becoming a proxy.
    [InlineData(true, 1, false)] // A tracked CPU update supersedes the native image.
    [InlineData(true, 2, false)] // A buffer update supersedes the native image.
    [InlineData(true, 3, false)] // A page-only invalidation leaves the plane unchanged.
    [InlineData(true, 4, false)] // A changed maybe-dirty hash supersedes the native image.
    [InlineData(false, 0, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    [InlineData(true, 3, true)]
    [InlineData(true, 4, true)]
    public void StencilAssociation_PreservesSelectedPlane(bool materialize, int update, bool normalized)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        byte[] a = [0x00, 0x01, 0x11, 0xa0];
        byte[] b = [0x80, 0xa0, 0x11, 0x99];
        var depthBytes = Bytes(0.75f, 0.75f, 0.75f, 0.75f);
        harness.Write(depthAddress, depthBytes);
        harness.Write(aAddress, a);
        harness.Write(bAddress, b);

        var ordinary = LinearRequest(aAddress, 4, normalized ? Format.R8Unorm : Format.R8Uint,
            normalized ? GuestPixelFormat.Bits8UNorm : GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 1, 1);
        var aId = materialize ? harness.Acquire(ref ordinary) : default;
        if (materialize)
        {
            harness.Finish();
            var source = harness.Image(aId);
            Assert.True(source.Backing.Exists);
            Assert.False(source.DepthOwner.IsValid);
            Assert.False(source.IsCpuDirty);
            Assert.False(source.IsBufferModified);
            Assert.False(source.IsGpuModified);
            Assert.Equal(a, harness.ReadImageBytes(source));
        }

        var depth = AsDepthTarget(LinearRequest(depthAddress, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var depthId = harness.Acquire(ref depth);
        byte[] Stencil() => harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.StencilBit)[..4];
        Assert.Equal(depthBytes, harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.DepthBit));
        Assert.Equal(b, Stencil());

        // Reuse must not restore an old plane over the selected attachment.
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(b, Stencil());
        if (update == 1)
        {
            a = [0x07, 0x08, 0x09, 0x0a];
            harness.Write(aAddress, a);
            Assert.True(harness.WriteFault(aAddress));
        }
        else if (update == 2)
        {
            a = [0x42, 0x42, 0x42, 0x42];
            harness.Worker.Run(() => harness.Cache.FillBuffer(aAddress, 4, 0x42424242, false));
        }
        else if (update is 3 or 4)
        {
            harness.Worker.Run(() => harness.Images.InvalidateMemory(aAddress + 16, 4));
            Assert.True(harness.Image(aId).IsMaybeCpuDirty);
            if (update == 4)
            {
                a = [0x31, 0x72, 0xa5, 0xfe];
                harness.Write(aAddress, a);
            }
        }

        depth.Description.Stencil = new GuestSpan(aAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        var proxy = harness.ProxyAt(aAddress, 4);
        if (materialize) Assert.Equal(aId, proxy);
        Assert.Equal(depthId, harness.Image(proxy).DepthOwner);
        harness.Finish();
        Assert.Equal(depthBytes, harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.DepthBit));
        Assert.Equal(a, Stencil());
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(a, Stencil());
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public unsafe void StencilAssociation_RecordedWriterSupersedesOrdinaryPlane()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var aAddress = address + 0xd000;
        harness.Write(aAddress, [0x00, 0x01, 0x11, 0xa0]);
        var ordinary = LinearRequest(aAddress, 4, Format.R8Uint, GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 1, 1);
        var aId = harness.Acquire(ref ordinary);
        Assert.Equal(new byte[] { 0, 1, 17, 160 }, harness.ReadImageBytes(harness.Image(aId)));
        var depth = AsDepthTarget(LinearRequest(address + 0xc000, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        depth.Description.Stencil = new GuestSpan(aAddress, 4);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        // Finding is sufficient for a writer that completely defines both aspects.
        var depthId = harness.Find(ref depth);
        harness.Worker.Run(() =>
        {
            var owner = harness.Image(depthId);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            owner.Transition(AccessFlags.TransferWriteBit, null, command);
            var value = new ClearDepthStencilValue(0.75f, 0x6d);
            var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, 0, 1, 0, 1);
            _vulkan.Vk.CmdClearDepthStencilImage(command, owner.Backing.Handle, ImageLayout.General, &value, 1, &range);
            harness.Images.MarkGpuWritten(depthId);
        });
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(new byte[] { 0x6d, 0x6d, 0x6d, 0x6d },
            harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.StencilBit)[..4]);
        Assert.Equal(Bytes(0.75f, 0.75f, 0.75f, 0.75f), harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.DepthBit));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public unsafe void StencilAssociation_PreservesNativeWriter(bool normalized, bool replaceDepth)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        var depthBytes = Bytes(0.75f, 0.5f, 0.25f, 1f);
        harness.Write(depthAddress, depthBytes);
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x80, 0xa0, 0x11, 0x99]);
        var ordinary = LinearRequest(aAddress, 4, normalized ? Format.R8Unorm : Format.R8Uint,
            normalized ? GuestPixelFormat.Bits8UNorm : GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 1, 1);
        var aId = harness.Acquire(ref ordinary);
        harness.Worker.Run(() =>
        {
            var source = harness.Image(aId);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            source.Transition(AccessFlags.TransferWriteBit, null, command);
            var value = normalized ? new ClearColorValue(1f, 0f, 0f, 0f) : new ClearColorValue { Uint32_0 = 255 };
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _vulkan.Vk.CmdClearColorImage(command, source.Backing.Handle, ImageLayout.General, &value, 1, &range);
            harness.Images.MarkGpuWritten(aId);
        });
        byte[] a = [255, 255, 255, 255];
        Assert.Equal(a, harness.ReadImageBytes(harness.Image(aId)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, harness.Read(aAddress, 4));
        Assert.True(harness.Image(aId).IsGpuModified);

        var depth = AsDepthTarget(LinearRequest(depthAddress, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var depthId = harness.Acquire(ref depth);
        if (replaceDepth)
        {
            var oldDepth = depthId;
            var color = AsStorage(LinearRequest(depthAddress, 16, Format.R32Sfloat, GuestPixelFormat.Bits32Float,
                GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1));
            var colorId = harness.Acquire(ref color);
            Assert.NotEqual(oldDepth, colorId);
            Assert.Equal(depthBytes, harness.ReadImageBytes(harness.Image(colorId)));
            depthId = harness.Acquire(ref depth);
            Assert.NotEqual(oldDepth, depthId);
            Assert.NotEqual(colorId, depthId);
        }

        Assert.Equal(new byte[] { 0x80, 0xa0, 0x11, 0x99 },
            harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.StencilBit)[..4]);
        depth.Description.Stencil = new GuestSpan(aAddress, 4);
        var tick = harness.Scheduler.CurrentTick;
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(tick, harness.Scheduler.CurrentTick);
        Assert.Equal(aId, harness.ProxyAt(aAddress, 4));
        Assert.Equal(a, harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.StencilBit)[..4]);
        Assert.Equal(depthBytes, harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.DepthBit));

        // A later attachment writer supersedes the ordinary backing. Reuse may
        // not copy that stale backing back over the current S8 representation.
        harness.Worker.Run(() =>
        {
            var owner = harness.Image(depthId);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            owner.Transition(AccessFlags.TransferWriteBit, null, command);
            var value = new ClearDepthStencilValue(0f, 0x6d);
            var range = new ImageSubresourceRange(ImageAspectFlags.StencilBit, 0, 1, 0, 1);
            _vulkan.Vk.CmdClearDepthStencilImage(command, owner.Backing.Handle, ImageLayout.General, &value, 1, &range);
            harness.Images.MarkGpuWritten(depthId);
        });
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(new byte[] { 0x6d, 0x6d, 0x6d, 0x6d },
            harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.StencilBit)[..4]);
        Assert.Equal(depthBytes, harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.DepthBit));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public unsafe void StencilAssociation_SecondDepthKeepsOrdinaryCopyOfGpuWrittenPlane()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var depthBytes = Bytes(0.75f, 0.5f, 0.25f, 1f);
        harness.Write(depthAddress, depthBytes);
        harness.Write(depthAddress + 0x100, depthBytes);
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        var first = AsDepthTarget(LinearRequest(depthAddress, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        first.Description.Stencil = new GuestSpan(aAddress, 4);
        first.View = first.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var firstId = harness.Acquire(ref first);
        Assert.Equal(firstId, harness.Acquire(ref first));
        harness.Worker.Run(() =>
        {
            var owner = harness.Image(firstId);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            owner.Transition(AccessFlags.TransferWriteBit, null, command);
            var value = new ClearDepthStencilValue(0f, 0x6d);
            var range = new ImageSubresourceRange(ImageAspectFlags.StencilBit, 0, 1, 0, 1);
            _vulkan.Vk.CmdClearDepthStencilImage(command, owner.Backing.Handle, ImageLayout.General, &value, 1, &range);
            harness.Images.MarkGpuWritten(firstId);
        });

        // An ordinary image of the plane copies the GPU-written stencil, with a write sequence older than the depth write.
        var ordinary = LinearRequest(aAddress, 4, Format.R8Uint, GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 1, 1);
        var ordinaryId = harness.Acquire(ref ordinary);
        Assert.NotEqual(harness.ProxyAt(aAddress, 4), ordinaryId);
        Assert.False(harness.Image(ordinaryId).DepthOwner.IsValid);
        Assert.Equal(new byte[] { 0x6d, 0x6d, 0x6d, 0x6d }, harness.ReadImageBytes(harness.Image(ordinaryId)));

        // Another depth surface names the same plane and must see those bytes.
        var second = AsDepthTarget(LinearRequest(depthAddress + 0x100, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        second.Description.Stencil = new GuestSpan(aAddress, 4);
        second.View = second.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var secondId = harness.Acquire(ref second);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(new byte[] { 0x6d, 0x6d, 0x6d, 0x6d },
            harness.ReadImageBytes(harness.Image(secondId), ImageAspectFlags.StencilBit)[..4]);
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    private unsafe void ClearStencilPlane(CacheHarness harness, ResourceSlotIdentifier depthId, byte value)
    {
        harness.Worker.Run(() =>
        {
            var owner = harness.Image(depthId);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            owner.Transition(AccessFlags.TransferWriteBit, null, command);
            var clear = new ClearDepthStencilValue(0f, value);
            var range = new ImageSubresourceRange(ImageAspectFlags.StencilBit, 0, 1, 0, 1);
            _vulkan.Vk.CmdClearDepthStencilImage(command, owner.Backing.Handle, ImageLayout.General, &clear, 1, &range);
            harness.Images.MarkGpuWritten(depthId);
        });
    }

    private static byte[] Filled(byte value) => [value, value, value, value];

    [Fact]
    public void StencilAssociation_DepthKeepsEachPlaneAcrossPlaneSwitches()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        harness.Write(depthAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x05, 0x06, 0x07, 0x08]);
        var depth = AsDepthTarget(LinearRequest(depthAddress, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        byte[] Aspect(ResourceSlotIdentifier id) => harness.ReadImageBytes(harness.Image(id), ImageAspectFlags.StencilBit)[..4];

        depth.Description.Stencil = new GuestSpan(aAddress, 4);
        var depthId = harness.Acquire(ref depth);
        ClearStencilPlane(harness, depthId, 0x11);

        // Plane B takes the aspect: plane A's values must survive in guest memory.
        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(new byte[] { 0x05, 0x06, 0x07, 0x08 }, Aspect(depthId));
        ClearStencilPlane(harness, depthId, 0x22);
        Assert.Equal(Filled(0x22), Aspect(depthId));

        // A reader of plane A sees its values, not the aspect's current plane.
        var reader = LinearRequest(aAddress, 4, Format.R8Uint, GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 1, 1);
        var readerId = harness.Acquire(ref reader);
        Assert.False(harness.Image(readerId).DepthOwner.IsValid);
        Assert.Equal(Filled(0x11), harness.ReadImageBytes(harness.Image(readerId)));

        // Back to plane A: the aspect holds A's values again.
        depth.Description.Stencil = new GuestSpan(aAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(Filled(0x11), Aspect(depthId));

        // Plane B kept what the GPU wrote to it.
        var bReader = LinearRequest(bAddress, 4, Format.R8Uint, GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 1, 1);
        var bReaderId = harness.Acquire(ref bReader);
        Assert.Equal(Filled(0x22), harness.ReadImageBytes(harness.Image(bReaderId)));
        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(Filled(0x22), Aspect(depthId));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public unsafe void StencilAssociation_WriteBackWithGpuWrittenImageOverPlane()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        harness.Write(depthAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x05, 0x06, 0x07, 0x08]);

        // An ordinary image that starts at plane A and runs past its end, written by the GPU before the depth
        // writes: obtaining the plane's buffer copies it, which enters the image cache (and its lock) again.
        var partial = LinearRequest(aAddress, 8, Format.R8Uint, GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(8, 1, 1), 1, 1, 1);
        var partialId = harness.Acquire(ref partial);
        harness.Worker.Run(() =>
        {
            var source = harness.Image(partialId);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            source.Transition(AccessFlags.TransferWriteBit, null, command);
            var value = new ClearColorValue { Uint32_0 = 0x77 };
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _vulkan.Vk.CmdClearColorImage(command, source.Backing.Handle, ImageLayout.General, &value, 1, &range);
            harness.Images.MarkGpuWritten(partialId);
        });
        Assert.True(harness.Image(partialId).IsGpuModified);

        var depth = AsDepthTarget(LinearRequest(depthAddress, 16, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        depth.Description.Stencil = new GuestSpan(aAddress, 4);
        var depthId = harness.Acquire(ref depth);
        ClearStencilPlane(harness, depthId, 0x11);

        // Obtaining a buffer enters the image cache (copying images over its range): that takes the cache lock,
        // so the write-back must obtain its buffer before taking that lock.
        harness.Cache.CompleteGuestPublications = (publishAddress, publishSize, access) =>
        {
            if (access == GuestBufferCache.PublicationAccess.Write) harness.Images.QueryRegion(publishAddress, publishSize);
        };

        // Leaving plane A writes it back while the image over it is still GPU-owned.
        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.False(harness.Image(depthId).IsCpuDirty);
        depth.Description.Stencil = new GuestSpan(aAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(Filled(0x11), harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.StencilBit)[..4]);
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void StencilAssociation_DepthsAlternatingOnOnePlaneKeepItsValue()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var depthBytes = Bytes(0.75f, 0.5f, 0.25f, 1f);
        harness.Write(depthAddress, depthBytes);
        harness.Write(depthAddress + 0x100, depthBytes);
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        ImageRequest Depth(ulong depthSurface)
        {
            var request = AsDepthTarget(LinearRequest(depthSurface, 16, Format.D32SfloatS8Uint,
                GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 4, 1), Format.D32SfloatS8Uint);
            request.Description.Stencil = new GuestSpan(aAddress, 4);
            request.View = request.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
            return request;
        }

        var first = Depth(depthAddress);
        var second = Depth(depthAddress + 0x100);
        byte[] Aspect(ResourceSlotIdentifier id) => harness.ReadImageBytes(harness.Image(id), ImageAspectFlags.StencilBit)[..4];
        var firstId = harness.Acquire(ref first);
        ClearStencilPlane(harness, firstId, 0x33);

        var secondId = harness.Acquire(ref second);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(Filled(0x33), Aspect(secondId));
        ClearStencilPlane(harness, secondId, 0x44);

        Assert.Equal(firstId, harness.Acquire(ref first));
        Assert.Equal(Filled(0x44), Aspect(firstId));
        Assert.Equal(secondId, harness.Acquire(ref second));
        Assert.Equal(Filled(0x44), Aspect(secondId));
        Assert.Equal(firstId, harness.Acquire(ref first));
        Assert.Equal(Filled(0x44), Aspect(firstId));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    private static ImageRequest PlaneDepth(ulong depthAddress, ulong plane, Extent3D? extent = null, uint samples = 1)
    {
        var size = extent ?? new Extent3D(4, 1, 1);
        var depth = AsDepthTarget(LinearRequest(depthAddress, size.Width * size.Height * 4, Format.D32SfloatS8Uint,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, size, 1, 4, samples), Format.D32SfloatS8Uint);
        depth.Description.Stencil = new GuestSpan(plane, size.Width * size.Height);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        return depth;
    }

    // What a reader of the plane sees: an ordinary image, not the aspect of a depth.
    private static byte[] ReadPlane(CacheHarness harness, ulong plane, uint width = 4)
    {
        var reader = LinearRequest(plane, width, Format.R8Uint, GuestPixelFormat.Bits8UInt,
            GuestImageType.Color2D, new Extent3D(width, 1, 1), 1, 1, 1);
        var readerId = harness.Acquire(ref reader);
        Assert.False(harness.Image(readerId).DepthOwner.IsValid);
        return harness.ReadImageBytes(harness.Image(readerId));
    }

    private static byte[] Aspect(CacheHarness harness, ResourceSlotIdentifier depthId) =>
        harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.StencilBit)[..4];

    [Fact]
    public void StencilRelease_SavesAspectWritesMadeAfterAnEarlierPublish()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        harness.Write(depthAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x05, 0x06, 0x07, 0x08]);
        var depth = PlaneDepth(depthAddress, aAddress);
        var depthId = harness.Acquire(ref depth);
        ClearStencilPlane(harness, depthId, 0x11);

        // A readback of the plane publishes it and records the aspect it published.
        harness.Worker.Run(() => harness.Images.PublishImagesUnderRawWrite(aAddress, 4));
        harness.Finish();
        var proxy = harness.Image(harness.ProxyAt(aAddress, 4));
        Assert.False(proxy.IsGpuModified);
        Assert.Equal(harness.Image(depthId).GpuWriteSequence, proxy.StencilPublishedSequence);

        // A write that is not an acquisition (a copy into the depth, a resolve) changes the aspect again.
        ClearStencilPlane(harness, depthId, 0x55);
        Assert.True(proxy.StencilPublishedSequence < harness.Image(depthId).GpuWriteSequence);

        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.False(harness.ProxyAt(aAddress, 4).IsValid);
        Assert.Equal(Filled(0x55), ReadPlane(harness, aAddress));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void StencilRelease_DoesNotDownloadAnAspectThatWasPublished()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        harness.Write(depthAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x05, 0x06, 0x07, 0x08]);
        var depth = PlaneDepth(depthAddress, aAddress);
        var depthId = harness.Acquire(ref depth);
        ClearStencilPlane(harness, depthId, 0x11);
        harness.Worker.Run(() => harness.Images.PublishImagesUnderRawWrite(aAddress, 4));
        harness.Finish();
        var proxy = harness.Image(harness.ProxyAt(aAddress, 4));

        // The proxy's contents are published and the depth wrote nothing since.
        Assert.False(proxy.IsGpuModified);
        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.Equal(Filled(0x11), ReadPlane(harness, aAddress));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void StencilRelease_SavesStencilFillOfThePlane()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x100000, ReadWrite);
        var depthAddress = address + 0x40000;
        var aAddress = address + 0x80000;
        var bAddress = address + 0x90000;
        const ulong planeSize = 0x10000;
        var extent = new Extent3D(256, 256, 1);
        var depth = PlaneDepth(depthAddress, aAddress, extent);
        var depthId = harness.Acquire(ref depth);

        // A memory fill over exactly the plane clears the aspect of its depth.
        harness.Worker.Run(() => Assert.True(harness.Images.TryClearImageFromBuffer(aAddress, planeSize, 0x5a5a5a5au)));
        depth.Description.Stencil = new GuestSpan(bAddress, planeSize);
        Assert.Equal(depthId, harness.Acquire(ref depth));
        Assert.False(harness.ProxyAt(aAddress, planeSize).IsValid);
        Assert.Equal(Filled(0x5a), ReadPlane(harness, aAddress, 256)[..4]);
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StencilRelease_DetachesAnAspectThatCannotBeReadBack(bool compressed)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        harness.Write(depthAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x05, 0x06, 0x07, 0x08]);
        var depth = PlaneDepth(depthAddress, aAddress, samples: compressed ? 1u : 2u);
        if (compressed)
        {
            depth.Description.Metadata.Range = new GuestSpan(address + 0x10000, 0x1000);
            depth.Description.Metadata.Kind = MetadataKind.Htile;
            depth.Description.Metadata.StencilCompressed = true;
        }

        var depthId = harness.Acquire(ref depth);
        ClearStencilPlane(harness, depthId, 0x11);
        Assert.True(harness.ProxyAt(aAddress, 4).IsValid);

        depth.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(depthId, harness.Acquire(ref depth));

        // The aspect holds plane B now: plane A is not redirected to it, and reads what guest memory holds.
        Assert.False(harness.ProxyAt(aAddress, 4).IsValid);
        Assert.Equal(depthId, harness.Image(harness.ProxyAt(bAddress, 4)).DepthOwner);
        Assert.False(harness.Images.TryFindStencilOwner(new GuestSpan(aAddress, 4), out _));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, ReadPlane(harness, aAddress));
        harness.Shutdown();
    }

    [Fact]
    public void StencilRelease_DepthsOfDifferentExtentsKeepTheirOwnPlaneProxies()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var firstAddress = address + 0xa000;
        var secondAddress = address + 0xb000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        harness.Write(firstAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(secondAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x05, 0x06, 0x07, 0x08]);
        var first = PlaneDepth(firstAddress, aAddress);
        var second = PlaneDepth(secondAddress, aAddress, new Extent3D(2, 2, 1));
        var firstId = harness.Acquire(ref first);
        ClearStencilPlane(harness, firstId, 0x11);
        var secondId = harness.Acquire(ref second);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, harness.ImagesInRange(aAddress, 4).Count(id => harness.Image(id).DepthOwner.IsValid));

        // The first depth leaves the plane: only its own proxy is released, with its bytes saved.
        first.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(firstId, harness.Acquire(ref first));
        var remaining = harness.ImagesInRange(aAddress, 4).Select(id => harness.Image(id).DepthOwner).Where(owner => owner.IsValid).ToList();
        Assert.Equal([secondId], remaining);
        first.Description.Stencil = new GuestSpan(aAddress, 4);
        Assert.Equal(firstId, harness.Acquire(ref first));
        Assert.Equal(Filled(0x11), Aspect(harness, firstId));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StencilRelease_EvictingADepthKeepsItsGpuWrittenPlane(bool collect)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        harness.Write(depthAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        var depth = PlaneDepth(depthAddress, aAddress);
        var depthId = harness.Acquire(ref depth);
        ClearStencilPlane(harness, depthId, 0x66);

        harness.Worker.Run(() =>
        {
            if (collect)
            {
                harness.Images.SetCollectionThresholds(0, 0, ulong.MaxValue, 81);
                harness.Images.ResetRecency(new[] { depthId }, 81);
                harness.Images.RunGarbageCollector();
            }
            else
            {
                harness.Image(depthId).ClearGpuModified();
                harness.Images.DeleteImageForTest(depthId);
            }
        });
        Assert.False(harness.Images.Contains(depthId));
        Assert.False(harness.ProxyAt(aAddress, 4).IsValid);
        Assert.Equal(Filled(0x66), ReadPlane(harness, aAddress));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void StencilRelease_FindStencilOwnerFollowsTheProxy()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var firstAddress = address + 0xa000;
        var secondAddress = address + 0xb000;
        var aAddress = address + 0xd000;
        var bAddress = address + 0xe000;
        harness.Write(firstAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(secondAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        harness.Write(bAddress, [0x05, 0x06, 0x07, 0x08]);
        var first = PlaneDepth(firstAddress, aAddress);
        var second = PlaneDepth(secondAddress, aAddress);
        var firstId = harness.Acquire(ref first);
        Assert.True(harness.Images.TryFindStencilOwner(new GuestSpan(aAddress, 4), out var owner));
        Assert.Equal(firstId, owner);

        // The second depth takes the plane: its aspect holds it, the first depth's no longer does.
        var secondId = harness.Acquire(ref second);
        Assert.True(harness.Images.TryFindStencilOwner(new GuestSpan(aAddress, 4), out owner));
        Assert.Equal(secondId, owner);

        // It names another plane afterwards: nobody holds plane A, though both descriptions still name it.
        second.Description.Stencil = new GuestSpan(bAddress, 4);
        Assert.Equal(secondId, harness.Acquire(ref second));
        Assert.False(harness.Images.TryFindStencilOwner(new GuestSpan(aAddress, 4), out _));
        Assert.True(harness.Images.TryFindStencilOwner(new GuestSpan(bAddress, 4), out owner));
        Assert.Equal(secondId, owner);
        harness.Shutdown();
    }

    [Fact]
    public void StencilRelease_UnmappedDepthDetachesItsPlaneWithoutSavingIt()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var aAddress = address + 0xd000;
        harness.Write(depthAddress, Bytes(0.75f, 0.5f, 0.25f, 1f));
        harness.Write(aAddress, [0x01, 0x02, 0x03, 0x04]);
        var depth = PlaneDepth(depthAddress, aAddress);
        var depthId = harness.Acquire(ref depth);
        ClearStencilPlane(harness, depthId, 0x11);

        // The unmap deletes the depth under the lock, where no buffer can be obtained: its plane keeps what
        // guest memory holds, and nothing is left pointing at the deleted aspect.
        harness.Worker.Run(() => ((IGuestImageStore)harness.Images).Unregister(depthAddress, 16));
        Assert.False(harness.Images.Contains(depthId));
        Assert.False(harness.ProxyAt(aAddress, 4).IsValid);
        Assert.False(harness.Images.TryFindStencilOwner(new GuestSpan(aAddress, 4), out _));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, ReadPlane(harness, aAddress));
        harness.Shutdown();
    }
}

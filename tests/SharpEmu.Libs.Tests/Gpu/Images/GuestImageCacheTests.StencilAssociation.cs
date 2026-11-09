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
}

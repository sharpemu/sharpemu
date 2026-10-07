// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void NativeColorClear_VolumeFirstUsePreservesMixedSlicesAndLaterWrites(bool storage)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x80000, ReadWrite);
        var metadataAddress = address + 0x40000;
        var request = LinearRequest(address, 0x4000, Format.R16G16B16A16Sfloat,
            GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color3D, new Extent3D(1, 1, 64), 1, 8, 1);
        if (storage) request = AsStorage(request);
        request.Description.Metadata.Kind = MetadataKind.Dcc;
        request.Description.Metadata.Range = new GuestSpan(metadataAddress, 64 * 4096);
        request.Description.Metadata.NativeColorClear = true;
        Assert.False(harness.Images.TryAbsorbDccFill(metadataAddress, 64 * 4096, 0x40404040));
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(metadataAddress, 64 * 4096, isWritten: true);
            buffer.Fill(offset, 64 * 4096, 0x40404040);
            buffer.Fill(offset + 32 * 4096 + 4092, 4, uint.MaxValue);
            buffer.Fill(offset + 63 * 4096, 4096, 0x20202020);
            harness.Images.SynchronizeColorMetadata(request);
        });
        var identifier = harness.Find(ref request);
        harness.Worker.Run(() =>
        {
            harness.Images.ApplyNativeColorClear(identifier, request);
            harness.Images.AcquireTextureView(identifier, request);
        });
        var image = harness.Image(identifier);
        var pixels = harness.ReadImageBytes(image);
        for (var slice = 0; slice < 64; slice++)
        {
            Assert.Equal(slice is 32 or 63 ? (ushort)0 : (ushort)0x3c00, BitConverter.ToUInt16(pixels, slice * 8 + 6));
            var expected = slice == 32 ? 0x40404040u : slice == 63 ? 0x20202020u : uint.MaxValue;
            Assert.Equal(expected, harness.ReadUInt32(metadataAddress + (ulong)slice * 4096));
        }
        harness.Worker.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            var color = new ClearColorValue(1f, 0f, 0f, 0.5f);
            _vulkan.Vk.CmdClearColorImage(command, image.Backing.Handle, ImageLayout.TransferDstOptimal, &color, 1, &range);
            harness.Images.MarkGpuWritten(identifier);
        });
        var written = harness.ReadImageBytes(image);
        harness.Worker.Run(() => harness.Images.ApplyNativeColorClear(identifier, request));
        Assert.Equal(written, harness.ReadImageBytes(image));
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void NativeColorClear_VolumeTargetConsumesOnlyItsDepthWindow()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x80000, ReadWrite);
        var metadataAddress = address + 0x40000;
        harness.Write(metadataAddress, Enumerable.Repeat((byte)0x40, 64 * 4096).ToArray());
        var request = AsColorTarget(LinearRequest(address, 0x4000, Format.R16G16B16A16Sfloat,
            GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color3D, new Extent3D(1, 1, 64), 1, 8, 1));
        request.View = request.View with { Type = ImageViewType.Type2DArray, BaseLayer = 31, LayerCount = 2 };
        request.Description.Metadata.Kind = MetadataKind.Dcc;
        request.Description.Metadata.Range = new GuestSpan(metadataAddress, 64 * 4096);
        request.Description.Metadata.NativeColorClear = true;
        request.Description.Metadata.ColorMetadataBaseLayer = 31;
        harness.Worker.Run(() => harness.Images.SynchronizeColorMetadata(request));
        var identifier = harness.Find(ref request);
        harness.Worker.Run(() => harness.Images.ApplyNativeColorClear(identifier, request));
        var pixels = harness.ReadImageBytes(harness.Image(identifier));
        for (var slice = 0; slice < 64; slice++)
        {
            var cleared = slice is 31 or 32;
            Assert.Equal(cleared ? (ushort)0x3c00 : (ushort)0, BitConverter.ToUInt16(pixels, slice * 8 + 6));
            Assert.Equal(cleared ? uint.MaxValue : 0x40404040u, harness.ReadUInt32(metadataAddress + (ulong)slice * 4096));
        }
        harness.Shutdown();
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void NativeColorMetadataIsInvalidatedBeforeTargetViewAcquisition()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x210000, ReadWrite);
        var metadataAddress = address + 0x200000;
        harness.Write(metadataAddress, Enumerable.Repeat((byte)0x10, 4096).ToArray());
        var target = AsColorTarget(LinearRequest(address, 8, Format.R16G16B16A16Sfloat,
            GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 8, 1));
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        target.Description.Metadata.Range = new GuestSpan(metadataAddress, 4096);
        target.Description.Metadata.NativeColorClear = true;
        var identifier = harness.Find(ref target);
        harness.Worker.Run(() => harness.Images.ApplyNativeColorClear(identifier, target));
        Assert.True(harness.Images.IsMetadata(metadataAddress));
        Assert.False(harness.ImageStore.MarkCpuWrite(metadataAddress + 128, 4));
        Assert.False(harness.Images.IsMetadata(metadataAddress));
        Assert.True(harness.Image(identifier).Registered);
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeColorClear_UsesGpuBytesAndConsumesOnlyUniformSlices(bool gpuWrite, bool mixed)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        harness.Write(address, Bytes((ushort)0, (ushort)0, (ushort)0, (ushort)0));
        harness.Write(metadataAddress, Enumerable.Repeat((byte)0x40, 4096).ToArray());
        var target = AsColorTarget(LinearRequest(address, 8, Format.R16G16B16A16Sfloat,
            GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 8, 1));
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        target.Description.Metadata.Range = new GuestSpan(metadataAddress, 4096);
        target.Description.Metadata.NativeColorClear = true;
        if (gpuWrite)
        {
            harness.Worker.Run(() =>
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(metadataAddress, 4096, isWritten: true);
                buffer.Fill(offset, 4096, 0x40404040);
                if (mixed) buffer.Fill(offset + 4092, 4, 0xffffffff);
            });
        }
        harness.Worker.Run(() => harness.Images.SynchronizeColorMetadata(target));
        var identifier = harness.Find(ref target);
        harness.Worker.Run(() => harness.Images.ApplyNativeColorClear(identifier, target));
        harness.Worker.Run(() => harness.Images.AcquireColorTargetView(identifier, target));
        var registration = harness.Image(identifier).MetadataRegistration;
        var pixels = harness.ReadImageBytes(harness.Image(identifier));
        Assert.Equal(mixed ? (ushort)0 : (ushort)0x3c00, BitConverter.ToUInt16(pixels, 6));
        Assert.Equal(mixed ? 0x40404040u : uint.MaxValue, harness.ReadUInt32(metadataAddress));
        if (!mixed)
        {
            Assert.False(harness.Images.TryAbsorbDccFill(metadataAddress, 4096, 0x40404040));
            harness.Worker.Run(() => harness.Images.ApplyNativeColorClear(identifier, target));
            Assert.Same(registration, harness.Image(identifier).MetadataRegistration);
            Assert.Equal(pixels, harness.ReadImageBytes(harness.Image(identifier)));
        }
        harness.Shutdown();
    }

    [Theory]
    [InlineData(0x20, true)]
    [InlineData(0x10, false)]
    public void NativeColorClear_PreservesOtherLayersAndUnsupportedMetadata(int code, bool clears)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        var original = Bytes((ushort)0x3c00, (ushort)0, (ushort)0, (ushort)0,
            (ushort)0, (ushort)0x3c00, (ushort)0, (ushort)0);
        harness.Write(address, original[..8]);
        harness.Write(address + 256, original[8..]);
        harness.Write(metadataAddress, Enumerable.Repeat((byte)0x40, 4096).ToArray());
        harness.Write(metadataAddress + 4096, Enumerable.Repeat((byte)code, 4096).ToArray());
        var target = AsColorTarget(LinearRequest(address, 512, Format.R16G16B16A16Sfloat,
            GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 2, 8, 1));
        target.View = target.View with { BaseLayer = 1, LayerCount = 1, Type = ImageViewType.Type2D };
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        target.Description.Metadata.Range = new GuestSpan(metadataAddress, 8192);
        target.Description.Metadata.NativeColorClear = true;
        target.Description.Metadata.ColorMetadataBaseLayer = 1;
        target.Description.Metadata.PackedColorClearSupported = true;
        target.Description.Metadata.PackedColorClear = new ClearColorValue(0f, 0f, 1f, 1f);
        harness.Worker.Run(() => harness.Images.SynchronizeColorMetadata(target));
        var identifier = harness.Find(ref target);
        harness.Worker.Run(() => harness.Images.ApplyNativeColorClear(identifier, target));
        harness.Worker.Run(() => harness.Images.AcquireColorTargetView(identifier, target));
        var pixels = harness.ReadImageBytes(harness.Image(identifier));
        Assert.Equal(original[..8], pixels[..8]);
        Assert.Equal(clears ? Bytes((ushort)0, (ushort)0, (ushort)0x3c00, (ushort)0x3c00) : original[8..], pixels[8..]);
        Assert.Equal(0x40404040u, harness.ReadUInt32(metadataAddress));
        Assert.Equal(clears ? uint.MaxValue : 0x10101010u, harness.ReadUInt32(metadataAddress + 4096));
        harness.Shutdown();
    }
}

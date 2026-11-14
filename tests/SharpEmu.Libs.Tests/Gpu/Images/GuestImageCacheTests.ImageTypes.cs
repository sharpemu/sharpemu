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
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ImageTypeReplacementPreservesLayersOutsideTheRequestedRange(bool startWithTwoDimensions, bool gpuModified)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint width = 64;
        const uint deviceValue = 0x50607080;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var original = Enumerable.Range(0, 128).Select(index => 0x10203040u + (uint)index).ToArray();
        harness.Write(address, Bytes(original));
        var source = SingletonRequest(address, 2, startWithTwoDimensions);
        source.Description.Extent.Width = width;
        source.Description.Pitch = width;
        ImageRequestBuilders.PopulateTextureMipLayout(ref source.Description);
        var sourceIdentifier = harness.Acquire(ref source);
        if (gpuModified)
            Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address, 512, deviceValue)));

        var destination = SingletonRequest(address, 1, !startWithTwoDimensions);
        destination.Description.Extent.Width = width;
        destination.Description.Pitch = width;
        ImageRequestBuilders.PopulateTextureMipLayout(ref destination.Description);
        var replacementIdentifier = harness.Acquire(ref destination);
        var replacement = harness.Image(replacementIdentifier);
        Assert.NotEqual(sourceIdentifier, replacementIdentifier);
        Assert.Equal(source.Description.Data, replacement.Description.Data);
        Assert.Equal(source.Description.Resources, replacement.Description.Resources);
        Assert.Equal(source.Description.Pitch, replacement.Description.Pitch);
        Assert.True(replacement.SupportsViewType(destination.View));
        Assert.Equal(gpuModified, replacement.IsGpuModified);
        Assert.Equal(Bytes(gpuModified ? Enumerable.Repeat(deviceValue, 128).ToArray() : original), harness.ReadImageBytes(replacement));
        Assert.Equal(Bytes(original), harness.Read(address, 512));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false, false, 1u)]
    [InlineData(false, true, 1u)]
    [InlineData(true, false, 1u)]
    [InlineData(true, true, 1u)]
    [InlineData(false, false, 2u)]
    [InlineData(false, true, 2u)]
    [InlineData(true, false, 2u)]
    [InlineData(true, true, 2u)]
    public void SingletonImageTypeChangePreservesContentsAndRetiresOnlyAGpuWrittenBacking(
        bool startWithTwoDimensions, bool gpuModified, uint layers)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint guestValue = 0x10203040;
        const uint deviceValue = 0x50607080;
        const ulong sliceSize = 256;
        var address = harness.MapBacked(0x10000, ReadWrite);
        for (uint layer = 0; layer < layers; layer++)
            harness.Write(address + layer * sliceSize, Bytes(guestValue + layer));

        var source = SingletonRequest(address, layers, startWithTwoDimensions);
        var sourceIdentifier = harness.Acquire(ref source);
        var sourceImage = harness.Image(sourceIdentifier);
        sourceImage.Binding.IsBound = true;
        if (gpuModified)
        {
            Assert.True(harness.Worker.Run(() =>
                harness.Images.TryClearImageFromBuffer(address, layers * sliceSize, deviceValue)));
        }

        var destination = SingletonRequest(address, layers, !startWithTwoDimensions);
        var destinationIdentifier = harness.Acquire(ref destination);
        var destinationImage = harness.Image(destinationIdentifier);
        Assert.NotEqual(sourceIdentifier, destinationIdentifier);
        // Guest contents keep the image of the other type beside the new one; GPU-written contents move into it.
        Assert.Equal(!gpuModified, sourceImage.Registered);
        Assert.Equal(gpuModified, sourceImage.Binding.NeedsRebind);
        Assert.NotEqual(0ul, sourceImage.Backing.Handle.Handle);
        Assert.Equal(startWithTwoDimensions ? ImageType.Type1D : ImageType.Type2D, destinationImage.Backing.ImageType);
        Assert.True(destinationImage.SupportsViewType(destination.View));
        Assert.Equal(gpuModified, destinationImage.IsGpuModified);
        Assert.Equal(destinationIdentifier, harness.Find(ref destination));

        var expected = Enumerable.Range(0, (int)layers)
            .Select(layer => gpuModified ? deviceValue : guestValue + (uint)layer).ToArray();
        Assert.Equal(Bytes(expected), harness.ReadImageBytes(destinationImage));
        Assert.Equal(gpuModified, sourceImage.Backing.Handle.Handle == 0);
        for (uint layer = 0; layer < layers; layer++)
            Assert.Equal(Bytes(guestValue + layer), harness.Read(address + layer * sliceSize, sizeof(uint)));

        var restoredIdentifier = harness.Acquire(ref source);
        Assert.NotEqual(destinationIdentifier, restoredIdentifier);
        Assert.Equal(!gpuModified, restoredIdentifier == sourceIdentifier);
        Assert.Equal(Bytes(expected), harness.ReadImageBytes(harness.Image(restoredIdentifier)));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingletonImageTypeReplacementUsesTheCurrentBufferContents(bool startWithTwoDimensions)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x10203040u));
        var source = SingletonRequest(address, 1, startWithTwoDimensions);
        var sourceIdentifier = harness.Acquire(ref source);
        harness.Worker.Run(() =>
        {
            var (buffer, offset) = harness.Cache.ObtainBuffer(address, 256, isWritten: true, isTexelBuffer: true);
            buffer.Fill(offset, 256, 0x50607080u);
            harness.Images.InvalidateMemoryFromGpu(address, 256);
        });
        Assert.True(harness.Image(sourceIdentifier).IsBufferModified);

        var destination = SingletonRequest(address, 1, !startWithTwoDimensions);
        var destinationIdentifier = harness.Acquire(ref destination);
        Assert.NotEqual(sourceIdentifier, destinationIdentifier);
        Assert.True(harness.Image(destinationIdentifier).SupportsViewType(destination.View));
        Assert.Equal(Bytes(0x50607080u), harness.ReadImageBytes(harness.Image(destinationIdentifier)));
        harness.Shutdown();
    }

    // A texture the guest reads both as 1D and as 2D keeps one image of each type instead of replacing one with the other
    // on every lookup.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASingleRowTextureReadAsBothTypesKeepsOneImageOfEach(bool startWithTwoDimensions)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x10203040u));
        var first = SingletonRequest(address, 1, startWithTwoDimensions);
        var second = SingletonRequest(address, 1, !startWithTwoDimensions);
        var firstIdentifier = harness.Acquire(ref first);
        var secondIdentifier = harness.Acquire(ref second);
        Assert.NotEqual(firstIdentifier, secondIdentifier);
        for (var round = 0; round < 3; round++)
        {
            first = SingletonRequest(address, 1, startWithTwoDimensions);
            second = SingletonRequest(address, 1, !startWithTwoDimensions);
            Assert.Equal(firstIdentifier, harness.Acquire(ref first));
            Assert.Equal(secondIdentifier, harness.Acquire(ref second));
        }

        Assert.True(harness.Image(firstIdentifier).Registered);
        Assert.True(harness.Image(secondIdentifier).Registered);
        Assert.Equal(Bytes(0x10203040u), harness.ReadImageBytes(harness.Image(firstIdentifier)));
        Assert.Equal(Bytes(0x10203040u), harness.ReadImageBytes(harness.Image(secondIdentifier)));
        harness.Shutdown();
    }

    // Once one twin is GPU-written, the other holds stale contents: its next lookup retires it and grows the written image
    // into its type.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGpuWriteToOneSingleRowTwinRetiresTheOther(bool writeTwoDimensions)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x10203040u));
        var written = SingletonRequest(address, 1, writeTwoDimensions);
        var other = SingletonRequest(address, 1, !writeTwoDimensions);
        var otherIdentifier = harness.Acquire(ref other);
        var writtenIdentifier = harness.Acquire(ref written);
        var otherImage = harness.Image(otherIdentifier);
        var writtenImage = harness.Image(writtenIdentifier);
        Assert.Same(writtenImage, otherImage.DimensionTwin);
        writtenImage.MarkGpuModified();
        Assert.True(otherImage.DimensionTwinStale);

        other = SingletonRequest(address, 1, !writeTwoDimensions);
        var refreshedIdentifier = harness.Acquire(ref other);
        Assert.NotEqual(otherIdentifier, refreshedIdentifier);
        Assert.False(otherImage.Registered);
        Assert.True(harness.Image(refreshedIdentifier).IsGpuModified);
        Assert.True(harness.Image(refreshedIdentifier).SupportsViewType(other.View));
        harness.Shutdown();
    }

    [Fact]
    public void SingletonVolumeKeepsItsCompatibleTwoDimensionalView()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x10203040u));
        var volume = SingletonRequest(address, 1, true);
        volume.Description.Type = GuestImageType.Color3D;
        volume.View = volume.View with { Type = ImageViewType.Type3D };
        var volumeIdentifier = harness.Acquire(ref volume);

        var slice = SingletonRequest(address, 1, true);
        Assert.Equal(volumeIdentifier, harness.Acquire(ref slice));
        Assert.Equal(ImageType.Type3D, harness.Image(volumeIdentifier).Backing.ImageType);
        Assert.Equal(Bytes(0x10203040u), harness.ReadImageBytes(harness.Image(volumeIdentifier)));
        harness.Shutdown();
    }

    private static ImageRequest SingletonRequest(ulong address, uint layers, bool twoDimensions)
    {
        var request = LinearRequest(address, layers * 256ul, Format.R32Uint, GuestPixelFormat.Bits32UInt,
            twoDimensions ? GuestImageType.Color2D : GuestImageType.Color1D,
            new Extent3D(1, 1, 1), layers, sizeof(uint), 1);
        request.View = request.View with
        {
            Type = twoDimensions
                ? layers == 1 ? ImageViewType.Type2D : ImageViewType.Type2DArray
                : layers == 1 ? ImageViewType.Type1D : ImageViewType.Type1DArray,
        };
        return request;
    }
}

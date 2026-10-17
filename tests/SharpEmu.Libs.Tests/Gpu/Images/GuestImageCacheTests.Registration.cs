// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Registration, the page owner index and the overlap rules of the image cache.
[Collection(SchedulingStateCollection.Name)]
public sealed partial class GuestImageCacheTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public GuestImageCacheTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    private static ImageRequest Sampled(ulong address)
    {
        var request = LinearRequest(address, 4, Format.R8G8B8A8Srgb, GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        return request;
    }

    private static ImageDescription Ownership(ulong address, ulong size)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(address, size);
        description.Extent = new Extent3D(1, 1, 1);
        return description;
    }

    [Fact]
    public void FindImage_ReusesCompatibleBackingAndFiltersCandidates()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Write(address, Bytes(0x44332211u));

        var first = Sampled(address);
        var firstId = harness.Find(ref first);
        var repeated = Sampled(address);
        var repeatedId = harness.Find(ref repeated);
        var compatible = Sampled(address);
        compatible.Description.PixelFormat = Format.R8G8B8A8Uint;
        compatible.Description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UInt;
        compatible.View = compatible.View with { Format = Format.R8G8B8A8Uint };
        var compatibleId = harness.Find(ref compatible);
        Assert.True(firstId.IsValid);
        Assert.Equal(firstId, repeatedId);
        Assert.Equal(firstId, compatibleId);
        Assert.Equal(Format.R8G8B8A8Srgb, harness.Image(firstId).Description.PixelFormat);

        Assert.Empty(harness.ImagesInRange(address + 8, 1));
        Assert.Equal(new[] { firstId }, harness.ImagesInRange(address + 8, 1, pageOverlap: true));
        Assert.Empty(harness.ImagesInRange(address + 0x1000, 1, pageOverlap: true));

        var stale = new ResourceSlotIdentifier(firstId.Index, firstId.Generation + 1);
        harness.Images.AddPageOwner(address, stale);
        Assert.Equal(new[] { firstId }, harness.ImagesInRange(address, 4));
        Assert.True(harness.Images.RemovePageOwner(address, stale));

        harness.Images.QueryEpoch = uint.MaxValue;
        Assert.Equal(new[] { firstId }, harness.ImagesInRange(address, 4));
        Assert.Equal(1u, harness.Images.QueryEpoch);

        var exact = compatible;
        var exactId = harness.Find(ref exact, exactFormat: true);
        Assert.True(exactId.IsValid);
        Assert.NotEqual(firstId, exactId);
        Assert.True(harness.Images.Contains(firstId));
        Assert.Equal(Format.R8G8B8A8Srgb, harness.Image(firstId).Description.PixelFormat);
        Assert.Equal(Format.R8G8B8A8Uint, harness.Image(exactId).Description.PixelFormat);
        var compatibleAgain = compatible;
        Assert.Equal(exactId, harness.Find(ref compatibleAgain));

        var nullRequest = Sampled(address);
        nullRequest.Description.Data = GuestSpan.Empty;
        nullRequest.Description.Pitch = 0;
        var nullRepeat = nullRequest;
        var nullId = harness.Find(ref nullRequest);
        Assert.True(nullId.IsValid);
        Assert.Equal(nullId, harness.Find(ref nullRepeat));
        Assert.NotEqual(exactId, nullId);
        Assert.Equal(1, harness.Images.NullImageCount);
        harness.Shutdown();
    }

    [Fact]
    public void Index_TracksCoarsePagesAndOwners()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x2000000, ReadWrite);
        var spanningBase = (address + 0x1ff000) & ~0xfffUL;
        var spanning = Ownership(spanningBase, 0x2000);
        var spanningId = harness.Images.InsertImageForTest(spanning);
        Assert.Equal(new[] { spanningId }, harness.ImagesInRange(spanning.Data.Address, spanning.Data.Size));
        Assert.Equal(1, harness.Images.PageOwnerCount(spanning.Data.Address));
        Assert.Equal(1, harness.Images.PageOwnerCount(spanning.Data.End - 1));
        harness.Images.DeleteImageForTest(spanningId);
        Assert.Equal(0, harness.Images.PageOwnerCount(spanning.Data.Address));
        Assert.Equal(0, harness.Images.PageOwnerCount(spanning.Data.End - 1));
        Assert.Empty(harness.ImagesInRange(spanning.Data.Address, spanning.Data.Size));

        var shared = Ownership(address + 0x500100, 0x100);
        var sharedFirst = harness.Images.InsertImageForTest(shared);
        var sharedSecond = harness.Images.InsertImageForTest(shared);
        var sharedResults = harness.ImagesInRange(shared.Data.Address, shared.Data.Size);
        Assert.Equal(2, sharedResults.Count);
        Assert.Contains(sharedFirst, sharedResults);
        Assert.Contains(sharedSecond, sharedResults);
        harness.Images.DeleteImageForTest(sharedFirst);
        Assert.Equal(new[] { sharedSecond }, harness.ImagesInRange(shared.Data.Address, shared.Data.Size));
        Assert.Equal(1, harness.Images.PageOwnerCount(shared.Data.Address));
        harness.Images.DeleteImageForTest(sharedSecond);
        Assert.Equal(0, harness.Images.PageOwnerCount(shared.Data.Address));

        const ulong largeSize = 16UL * 1024 * 1024;
        var large = Ownership((address + 0x800000 + 0xfffff) & ~0xfffffUL, largeSize);
        var largeId = harness.Images.InsertImageForTest(large);
        Assert.Equal(16, harness.Images.OwnedPageCount(large.Data.Address, large.Data.Size, largeId));
        harness.Images.DeleteImageForTest(largeId);
        Assert.Equal(0, harness.Images.OwnedPageCount(large.Data.Address, large.Data.Size, largeId));
        harness.Shutdown();
    }

    [Fact]
    public void CpuWriteBetweenDiscoveryAndAcquisition_RefreshesTheImage()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x44332211u));
        var request = Sampled(address);
        var imageIdentifier = harness.Find(ref request);
        var image = harness.Image(imageIdentifier);

        Assert.True(harness.WriteFault(address));
        harness.Write(address, Bytes(0x88776655u));
        Assert.True(image.IsCpuDirty);

        var local = request;
        var (firstView, repeatedView, mappedView) = harness.Worker.Run(() =>
        {
            var first = harness.Images.AcquireTextureView(imageIdentifier, local);
            var repeated = harness.Images.AcquireTextureView(imageIdentifier, local);
            var mapped = image.GetOrCreateView(local.View with { Mapping = new ComponentMapping(ComponentSwizzle.B, ComponentSwizzle.Identity, ComponentSwizzle.R, ComponentSwizzle.Identity) });
            return (first, repeated, mapped);
        });
        Assert.NotEqual(0UL, firstView.Handle);
        Assert.Equal(firstView, repeatedView);
        Assert.NotEqual(firstView, mappedView);
        Assert.Equal(2, image.Views.Count);
        Assert.False(image.Uses.Texture);
        Assert.False(image.IsGpuModified);
        Assert.False(image.IsCpuDirty);
        Assert.False(harness.Images.IsReadbackScheduled(imageIdentifier));
        Assert.Equal(Bytes(0x88776655u), harness.ReadImageBytes(image));
        harness.Shutdown();
    }

    [Fact]
    public void ExactBackingWithFewerResources_IsDiscardedAndRecreated()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(0x01020304u, 0x11121314u, 0x21222324u));
        var underspecified = LinearRequest(address, 12, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D, new Extent3D(2, 1, 1), 1, 4, 1);
        underspecified.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 8, Pitch = 2, Height = 1 };
        underspecified.Description.MipLayout[1] = new MipLevelLayout { Offset = 8, Size = 4, Pitch = 1, Height = 1 };
        var underspecifiedId = harness.Acquire(ref underspecified);
        harness.MarkGpuWritten(underspecifiedId);

        var complete = underspecified;
        complete.Description.Resources = new SubresourceCount(2, 1);
        complete.View = complete.View with { LevelCount = 2 };
        var completeId = harness.Find(ref complete);
        var owner = harness.Images.Owner(completeId);
        Assert.True(completeId.IsValid);
        Assert.NotEqual(underspecifiedId, completeId);
        Assert.False(harness.Images.Contains(underspecifiedId));
        Assert.NotNull(owner);
        Assert.True(owner!.Registered);
        Assert.Equal(complete.Description.Resources, owner.Description.Resources);
        Assert.False(owner.IsGpuModified);
        Assert.True(owner.IsDefinitelyCpuDirty);
        harness.Shutdown();
    }

    [Fact]
    public void RenderTargetGrowth_ReplacesTheEqualAllocation()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var narrow = LinearRequest(address, 0x10000, Format.R16G16B16A16Sfloat, GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 8, 1);
        narrow = AsColorTarget(narrow);
        narrow.Description.Pitch = 128;
        narrow.Description.TileMode = GuestTileMode.RenderTarget;
        narrow.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 0x10000, Pitch = 128, Height = 1 };
        var narrowId = harness.Acquire(ref narrow);

        var wide = narrow;
        wide.Description.Extent = new Extent3D(9, 1, 1);
        var wideId = harness.Acquire(ref wide);
        var wideOwner = harness.Images.Owner(wideId);
        Assert.NotEqual(narrowId, wideId);
        Assert.False(harness.Images.Contains(narrowId));
        Assert.NotNull(wideOwner);
        Assert.Equal(9u, wideOwner!.Description.Extent.Width);
        Assert.Equal(9u, wideOwner.Backing.Extent.Width);

        var narrowAgain = narrow;
        var narrowAgainId = harness.Find(ref narrowAgain);
        Assert.Equal(wideId, narrowAgainId);
        Assert.Equal(9u, harness.Image(narrowAgainId).Backing.Extent.Width);
        harness.Shutdown();
    }

    [Fact]
    public void ArrayToVolumeOverlap_ExpandsAndKeepsGpuOwnership()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        // A linear 1x1 layer occupies one padded 256-byte slice; the GPU clear then differs from guest memory.
        const ulong slice = 256;
        harness.Write(address + 0x1000, Bytes(0x10203040u));
        harness.Write(address + 0x1000 + slice, Bytes(0x50607080u));
        var array = LinearRequest(address + 0x1000, 2 * slice, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D, new Extent3D(1, 1, 1), 2, 4, 1);
        var arrayId = harness.Acquire(ref array);
        Assert.Equal(Bytes(0x10203040u, 0x50607080u), harness.ReadImageBytes(harness.Image(arrayId)));
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address + 0x1000, 2 * slice, 0x0a0b0c0du)));
        Assert.True(harness.Image(arrayId).IsGpuModified);

        var volume = array;
        volume.Description.Type = GuestImageType.Color3D;
        volume.Description.Extent = new Extent3D(1, 1, 2);
        volume.Description.Resources = SubresourceCount.Single;
        volume.View = volume.View with { Type = ImageViewType.Type3D, LayerCount = 1 };
        var volumeId = harness.Find(ref volume);
        Assert.True(volumeId.IsValid);
        Assert.NotEqual(arrayId, volumeId);
        Assert.Equal(ImageType.Type3D, harness.Image(volumeId).Backing.ImageType);
        Assert.True(harness.Image(volumeId).IsGpuModified);
        Assert.Equal(Bytes(0x0a0b0c0du, 0x0a0b0c0du), harness.ReadImageBytes(harness.Image(volumeId)));
        Assert.Equal(Bytes(0x10203040u), harness.Read(address + 0x1000, 4));
        Assert.Equal(Bytes(0x50607080u), harness.Read(address + 0x1000 + slice, 4));
        harness.Shutdown();
    }

    [Fact]
    public void EqualSizeTileModeAlias_KeepsSeparateBackings()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint extent = 256;
        const ulong size = extent * extent * 4;
        var address = harness.MapBacked(size, ReadWrite);
        var renderTarget = LinearRequest(address, size, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color2D, new Extent3D(extent, extent, 1), 1, 4, 1);
        renderTarget = AsStorage(renderTarget);
        renderTarget.Description.TileMode = GuestTileMode.RenderTarget;
        var renderTargetId = harness.Find(ref renderTarget);

        var standard = renderTarget;
        standard.Role = ImageRole.Texture;
        standard.Description.TileMode = GuestTileMode.Standard4KB;
        standard.View = standard.View with { Usage = ImageUsageFlags.SampledBit };
        var standardId = harness.Find(ref standard);
        var repeated = standard;
        var repeatedId = harness.Find(ref repeated);
        Assert.True(renderTargetId.IsValid && standardId.IsValid);
        Assert.NotEqual(renderTargetId, standardId);
        Assert.Equal(standardId, repeatedId);
        Assert.Equal(GuestTileMode.RenderTarget, harness.Image(renderTargetId).Description.TileMode);
        Assert.Equal(GuestTileMode.Standard4KB, harness.Image(standardId).Description.TileMode);
        harness.Shutdown();
    }

    [Fact]
    public void CompressedAlias_PreservesTheNativeContents()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var data = Bytes(0x01234567u, 0x89abcdefu, 0xfedcba98u, 0x76543210u);
        harness.Write(address, data);
        var uncompressed = LinearRequest(address, 16, Format.R32G32B32A32Uint, GuestPixelFormat.Bits32_32_32_32UInt, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 16, 1);
        var uncompressedId = harness.Acquire(ref uncompressed);
        harness.MarkGpuWritten(uncompressedId);

        var compressed = LinearRequest(address, 16, Format.BC3UnormBlock, GuestPixelFormat.Bc3UNorm, GuestImageType.Color2D, new Extent3D(4, 4, 1), 1, 16, 1);
        var compressedId = harness.Find(ref compressed);
        var downloaded = harness.Worker.Run(() => harness.Images.TryDownloadForTest(compressedId));
        harness.Finish();
        Assert.True(compressedId.IsValid);
        Assert.NotEqual(uncompressedId, compressedId);
        Assert.Equal(Format.BC3UnormBlock, harness.Image(compressedId).Backing.Format);
        Assert.True(downloaded);
        Assert.Equal(data, harness.Read(address, 16));
        harness.Shutdown();
    }
}

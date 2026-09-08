// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class CachedImageTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public CachedImageTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    internal static ImageDescription Color2D(uint width, uint height, uint levels = 1, uint layers = 1, ulong address = ArrayBackedSpace.Base, Format format = Format.R8G8B8A8Unorm)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(address, ImageTestHarness.WholeImageBytes(description with
        {
            Extent = new Extent3D(width, height, 1),
            Resources = new SubresourceCount(levels, layers),
            BytesPerBlock = 4,
        }));
        description.PixelFormat = format;
        description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UNorm;
        description.Extent = new Extent3D(width, height, 1);
        description.Resources = new SubresourceCount(levels, layers);
        description.Pitch = width;
        description.BytesPerBlock = 4;
        return description;
    }

    internal static ImageDescription Volume(uint width, uint height, uint depth)
    {
        var description = Color2D(width, height);
        description.Type = GuestImageType.Color3D;
        description.Extent = new Extent3D(width, height, depth);
        description.Data = new GuestSpan(ArrayBackedSpace.Base, (ulong)width * height * depth * 4);
        return description;
    }

    [Fact]
    public void CalculateRowsPerCopy_LimitsRowsToTheStagingCapacity()
    {
        Assert.Equal(0u, CachedImage.CalculateRowsPerCopy(0, 4, 100));
        Assert.Equal(0u, CachedImage.CalculateRowsPerCopy(16, 0, 100));
        Assert.Equal(0u, CachedImage.CalculateRowsPerCopy(200, 4, 100));
        Assert.Equal(4u, CachedImage.CalculateRowsPerCopy(16, 4, 100));
        Assert.Equal(6u, CachedImage.CalculateRowsPerCopy(16, 10, 100));
    }

    [Fact]
    public void Constructor_SeedsDirtyStateFromTheDescription()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var backed = harness.CreateImage(Color2D(8, 8));
        Assert.True(backed.IsDefinitelyCpuDirty);
        Assert.True(backed.IsCpuDirty);
        Assert.True(backed.Backing.Exists);
        Assert.Equal(ImageAccessState.Initial, backed.Backing.State);
        Assert.Equal(256UL, backed.Description.Data.Size);
        Assert.Equal(1024UL, backed.AccountedSize);

        var unbacked = Color2D(8, 8);
        unbacked.Data = GuestSpan.Empty;
        unbacked.Pitch = 0;
        var fresh = harness.CreateImage(unbacked);
        Assert.False(fresh.IsCpuDirty);
        Assert.Equal(0UL, fresh.AccountedSize);

        var association = ImageDescription.Create();
        association.Data = new GuestSpan(ArrayBackedSpace.Base, 0x100);
        var proxy = harness.CreateImage(association);
        Assert.False(proxy.Backing.Exists);
        Assert.Equal(0UL, proxy.AccountedSize);
        Assert.True(proxy.IsCpuDirty);
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void DirtyFlags_FollowTheReferenceTransitions()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(_vulkan);
        var imageBase = ArrayBackedSpace.Base + 0x800;
        var image = harness.CreateImage(Color2D(64, 64, address: imageBase));
        image.RefreshComplete();
        Assert.False(image.IsCpuDirty);

        image.InvalidateCpuWrite(ArrayBackedSpace.Base + 0x5000, 0x10);
        Assert.False(image.IsCpuDirty);
        image.InvalidateCpuWrite(ArrayBackedSpace.Base + 0x100, 0x10);
        Assert.True(image.IsMaybeCpuDirty);
        Assert.False(image.IsDefinitelyCpuDirty);
        Assert.True(image.NeedsMaybeCpuHash);
        image.SetMaybeCpuHash(7);
        Assert.False(image.NeedsMaybeCpuHash);
        Assert.False(image.ResolveMaybeCpuHash(7));
        Assert.False(image.IsCpuDirty);

        image.MarkMaybeCpuDirty();
        image.SetMaybeCpuHash(7);
        Assert.True(image.ResolveMaybeCpuHash(8));
        Assert.True(image.IsDefinitelyCpuDirty);
        image.MarkMaybeCpuDirty();
        Assert.False(image.IsMaybeCpuDirty);
        Assert.Throws<SchedulerFatalException>(() => image.SetMaybeCpuHash(1));
        image.RefreshComplete();
        Assert.Throws<SchedulerFatalException>(image.RefreshComplete);

        image.InvalidateCpuWrite(imageBase + 0x100, 4);
        Assert.True(image.IsDefinitelyCpuDirty);
        Assert.Equal(SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled ? imageBase + 0x100 : 0UL, image.LastCpuWriteAddress);
        Assert.Equal(SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled ? 4UL : 0UL, image.LastCpuWriteSize);
        image.RefreshComplete();
        Assert.Equal(0UL, image.LastCpuWriteAddress);
        Assert.Equal(0UL, image.LastCpuWriteSize);
        image.MarkGpuModified();
        Assert.True(image.SafeToDownload);
        Assert.True(image.GpuOverlaps(imageBase, 1));
        Assert.False(image.GpuOverlaps(imageBase + 0x4000, 1));
        Assert.True(image.Overlaps(imageBase + 0x4000, 1, pages: true));
        image.MarkBufferModified();
        Assert.False(image.SafeToDownload);
        image.ClearBufferModified();
        image.ClearGpuModified();
        Assert.False(image.SafeToDownload);
        Assert.False(image.IsWatched);
        image.WatchBegin = 1;
        image.WatchEnd = 2;
        Assert.True(image.IsWatched);
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void HashGuestEdges_ReadsThePartialPagesThroughTheBacking()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(_vulkan);
        var description = Color2D(64, 64, address: ArrayBackedSpace.Base + 0x800);
        var image = harness.CreateImage(description);
        var first = image.HashGuestEdges();
        Assert.Equal(first, image.HashGuestEdges());
        harness.Guest.Bytes[0x800] ^= 0xff;
        var headChanged = image.HashGuestEdges();
        Assert.NotEqual(first, headChanged);
        harness.Guest.Bytes[0x800 + 0x4000 - 1] ^= 0xff;
        Assert.NotEqual(headChanged, image.HashGuestEdges());
        harness.Guest.Bytes[0x2000] ^= 0xff;
        var middleChanged = image.HashGuestEdges();
        harness.Guest.Bytes[0x2000] ^= 0xff;
        Assert.Equal(middleChanged, image.HashGuestEdges());

        var unbacked = harness.CreateImage(Color2D(8, 8, address: 0x1000));
        Assert.Throws<SchedulerFatalException>(() => unbacked.HashGuestEdges());
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Barriers_MaterializePerSubresourceAndCollapseAfterAWholePass()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var image = harness.CreateImage(Color2D(64, 64, levels: 3, layers: 2));

        var (initial, initialStages) = image.GetBarriers(ImageLayout.General, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit, null);
        var only = Assert.Single(initial);
        Assert.Equal(ImageLayout.Undefined, only.OldLayout);
        Assert.Equal(Vk.RemainingMipLevels, only.SubresourceRange.LevelCount);
        Assert.Equal(PipelineStageFlags.AllCommandsBit, initialStages);
        Assert.Null(image.Backing.SubresourceStates);

        var (unchanged, _) = image.GetBarriers(ImageLayout.General, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit, null);
        Assert.Empty(unchanged);

        var partial = new SubresourceRange(1, 1, 1, 1);
        var (partialBarriers, partialStages) = image.GetBarriers(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit, partial);
        var one = Assert.Single(partialBarriers);
        Assert.Equal((1u, 1u, 1u, 1u), (one.SubresourceRange.BaseMipLevel, one.SubresourceRange.LevelCount, one.SubresourceRange.BaseArrayLayer, one.SubresourceRange.LayerCount));
        Assert.Equal(PipelineStageFlags.ComputeShaderBit, partialStages);
        Assert.NotNull(image.Backing.SubresourceStates);
        Assert.Equal(6, image.Backing.SubresourceStates!.Count);

        var (repeated, _) = image.GetBarriers(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit, partial);
        Assert.Single(repeated);

        // Only the subresource the transfer changed needs a barrier; the others are already in the target state.
        var (whole, wholeStages) = image.GetBarriers(ImageLayout.General, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit, null);
        var collapsed = Assert.Single(whole);
        Assert.Equal((1u, 1u), (collapsed.SubresourceRange.BaseMipLevel, collapsed.SubresourceRange.BaseArrayLayer));
        Assert.Equal(PipelineStageFlags.TransferBit, wholeStages);
        Assert.Null(image.Backing.SubresourceStates);
        Assert.Equal(new ImageAccessState(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit, ImageLayout.General), image.Backing.State);
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Barriers_IgnoreLayerWindowsOnVolumes()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var image = harness.CreateImage(Volume(16, 16, 8));
        var (barriers, _) = image.GetBarriers(ImageLayout.General, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit, new SubresourceRange(0, 1, 3, 2));
        Assert.Single(barriers);
        Assert.Null(image.Backing.SubresourceStates);
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void GetOrCreateView_NormalizesAndCachesViews()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(_vulkan);
        var image = harness.CreateImage(Color2D(64, 64, levels: 2, layers: 2));
        var request = ImageViewDescription.Default with { Format = Format.R8G8B8A8Unorm, Type = ImageViewType.Type2DArray, LayerCount = 2 };
        var view = image.GetOrCreateView(request);
        Assert.NotEqual(0UL, view.Handle);
        Assert.Equal(view, image.GetOrCreateView(request));
        Assert.Equal(view, image.GetOrCreateView(request with { Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit }));
        Assert.Single(image.Views);
        Assert.NotEqual(view, image.GetOrCreateView(request with { Usage = ImageUsageFlags.StorageBit }));
        Assert.NotEqual(view, image.GetOrCreateView(request with { Format = Format.B8G8R8A8Srgb }));
        Assert.Equal(3, image.Views.Count);

        Assert.Throws<SchedulerFatalException>(() => image.GetOrCreateView(request with { Format = Format.R16G16B16A16Sfloat }));
        Assert.Throws<SchedulerFatalException>(() => image.GetOrCreateView(request with { LevelCount = 3 }));
        Assert.Throws<SchedulerFatalException>(() => image.GetOrCreateView(request with { BaseLayer = 2 }));
        Assert.Throws<SchedulerFatalException>(() => image.GetOrCreateView(request with { Aspect = ImageAspectFlags.DepthBit }));
        Assert.Throws<SchedulerFatalException>(() => image.GetOrCreateView(request with { Type = ImageViewType.Type3D }));
        Assert.All(fatal.Messages, message => Assert.Contains("image view is invalid", message));

        var depth = harness.CreateImage(Color2D(16, 16, format: Format.D32Sfloat));
        var depthView = depth.GetOrCreateView(ImageViewDescription.Default with { Format = Format.R32Sfloat, Aspect = ImageAspectFlags.ColorBit });
        var cached = Assert.Single(depth.Views);
        Assert.Equal((Format.D32Sfloat, ImageAspectFlags.DepthBit), (cached.Description.Format, cached.Description.Aspect));
        Assert.Equal(depthView, cached.View);

        var volume = harness.CreateImage(Volume(16, 16, 8));
        var slice = volume.GetOrCreateView(ImageViewDescription.Default with { Format = Format.R8G8B8A8Unorm, Type = ImageViewType.Type2DArray, BaseLayer = 2, LayerCount = 4 });
        Assert.NotEqual(0UL, slice.Handle);
        Assert.Throws<SchedulerFatalException>(() => volume.GetOrCreateView(ImageViewDescription.Default with { Format = Format.R8G8B8A8Unorm, Type = ImageViewType.Type2DArray, BaseLayer = 2, LayerCount = 8 }));
        harness.AssertNoValidationMessages();
    }

    [Theory]
    [InlineData(32u, 16u, 1u, 1u, false)]
    [InlineData(32u, 32u, 3u, 1u, false)]
    [InlineData(16u, 16u, 1u, 4u, false)]
    [InlineData(16u, 16u, 2u, 3u, false)]
    [InlineData(16u, 16u, 1u, 6u, true)]
    public void UploadAndDownload_RoundTripEverySubresource(uint width, uint height, uint levels, uint depth, bool volume)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var description = volume ? Volume(width, height, depth) : Color2D(width, height, levels, depth);
        var image = harness.CreateImage(description);
        var bytes = ImageTestHarness.WholeImageBytes(description);
        var copies = ImageTestHarness.WholeImageCopies(description, 0);
        var pattern = ImageTestHarness.Pattern((int)bytes, 11);
        harness.UploadImage(image, pattern, copies);
        Assert.Equal(new ImageAccessState(PipelineStageFlags.TransferBit | PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit, ImageLayout.General), image.Backing.State);
        Assert.Equal(pattern, harness.ReadImage(image, copies, bytes));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Copies_CrossImageTypesAndFormats()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var arrayDescription = Color2D(16, 16, levels: 2, layers: 4);
        var arrayBytes = ImageTestHarness.WholeImageBytes(arrayDescription);
        var arrayCopies = ImageTestHarness.WholeImageCopies(arrayDescription, 0);
        var pattern = ImageTestHarness.Pattern((int)arrayBytes, 3);
        var source = harness.CreateImage(arrayDescription);
        harness.UploadImage(source, pattern, arrayCopies);

        var arrayCopy = harness.CreateImage(arrayDescription);
        harness.Run(() => arrayCopy.CopyFrom(source));
        Assert.Equal(pattern, harness.ReadImage(arrayCopy, arrayCopies, arrayBytes));

        var volumeDescription = Volume(16, 16, 4);
        var volume = harness.CreateImage(volumeDescription);
        harness.Run(() => volume.CopyFrom(source));
        var volumeCopies = ImageTestHarness.WholeImageCopies(volumeDescription, 0);
        Assert.Equal(pattern[..(16 * 16 * 4 * 4)], harness.ReadImage(volume, volumeCopies, ImageTestHarness.WholeImageBytes(volumeDescription)));

        var back = harness.CreateImage(Color2D(16, 16, layers: 4));
        harness.Run(() => back.CopyFrom(volume));
        var backCopies = ImageTestHarness.WholeImageCopies(back.Description, 0);
        Assert.Equal(pattern[..(16 * 16 * 4 * 4)], harness.ReadImage(back, backCopies, 16 * 16 * 4 * 4));

        var uintDescription = Color2D(16, 16, levels: 2, layers: 4, format: Format.R32Uint);
        var reinterpreted = harness.CreateImage(uintDescription);
        harness.Run(() => reinterpreted.CopyThroughBuffer(source, harness.CreateDeviceLocalBuffer(16 * 4 * 3)));
        Assert.Equal(pattern, harness.ReadImage(reinterpreted, arrayCopies, arrayBytes));

        var mipSource = harness.CreateImage(Color2D(8, 8, layers: 4));
        var mipPattern = ImageTestHarness.Pattern(8 * 8 * 4 * 4, 5);
        harness.UploadImage(mipSource, mipPattern, ImageTestHarness.WholeImageCopies(mipSource.Description, 0));
        harness.Run(() => arrayCopy.CopyMipFrom(mipSource, 1, 0));
        var afterMip = harness.ReadImage(arrayCopy, arrayCopies, arrayBytes);
        Assert.Equal(pattern[..(16 * 16 * 4 * 4)], afterMip[..(16 * 16 * 4 * 4)]);
        Assert.Equal(mipPattern, afterMip[(16 * 16 * 4 * 4)..]);
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void ResolveFrom_CopiesSingleSampleAndResolvesMultisample()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new ImageTestHarness(_vulkan);
        var description = Color2D(8, 8);
        var source = harness.CreateImage(description);
        var pattern = ImageTestHarness.Pattern(8 * 8 * 4, 9);
        var copies = ImageTestHarness.WholeImageCopies(description, 0);
        harness.UploadImage(source, pattern, copies);
        var destination = harness.CreateImage(description with { PixelFormat = Format.B8G8R8A8Unorm });
        harness.Run(() => destination.ResolveFrom(source, SubresourceRange.First, SubresourceRange.First));
        Assert.Equal(pattern, harness.ReadImage(destination, copies, 8 * 8 * 4));

        var multisampled = description;
        multisampled.Samples = 4;
        var samples = harness.CreateImage(multisampled);
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            samples.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
            var clear = new ClearColorValue { Float32_0 = 1.0f, Float32_1 = 0.25f, Float32_2 = 0.0f, Float32_3 = 1.0f };
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            harness.Vk.CmdClearColorImage(command, samples.Backing.Handle, ImageLayout.TransferDstOptimal, &clear, 1, &range);
        });
        var resolved = harness.CreateImage(description);
        harness.Run(() => resolved.ResolveFrom(samples, SubresourceRange.First, SubresourceRange.First));
        var resolvedBytes = harness.ReadImage(resolved, copies, 8 * 8 * 4);
        Assert.All(Enumerable.Range(0, 64), texel => Assert.Equal(new byte[] { 255, 64, 0, 255 }, resolvedBytes[(texel * 4)..(texel * 4 + 4)]));

        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => samples.ResolveFrom(source, SubresourceRange.First, SubresourceRange.First)));
        harness.Run(() => Assert.Throws<SchedulerFatalException>(() => resolved.ResolveFrom(source, new SubresourceRange(1, 1, 0, 1), SubresourceRange.First)));
        Assert.Contains(fatal.Messages, message => message.Contains("resolve subresources are invalid"));
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void Disposal_ReturnsTheAllocationCounterToItsBaseline()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var baseline = harness.Device.LiveAllocations;
        var image = harness.CreateImage(Color2D(64, 64, levels: 2, layers: 2));
        Assert.Equal(baseline + 1, harness.Device.LiveAllocations);
        Assert.True(harness.Device.PeakAllocations >= baseline + 1);
        Assert.True(image.Backing.AllocationSize >= 64 * 64 * 4 * 2);
        image.Dispose();
        Assert.Equal(baseline, harness.Device.LiveAllocations);
        Assert.False(image.Backing.Exists);
        harness.AssertNoValidationMessages();
    }
}

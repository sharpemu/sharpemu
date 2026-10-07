// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed unsafe class PresentationColorTests : IClassFixture<HeadlessVulkanFixture>
{
    private readonly HeadlessVulkan? _vulkan;

    public PresentationColorTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Theory]
    [InlineData(Format.B8G8R8A8Srgb, Format.B8G8R8A8Unorm, false)]
    [InlineData(Format.R8G8B8A8Srgb, Format.R8G8B8A8Unorm, false)]
    [InlineData(Format.R8G8B8A8Srgb, Format.B8G8R8A8Unorm, false)]
    [InlineData(Format.B8G8R8A8Srgb, Format.R8G8B8A8Unorm, false)]
    [InlineData(Format.B8G8R8A8Unorm, Format.B8G8R8A8Unorm, false)]
    [InlineData(Format.R8G8B8A8Unorm, Format.B8G8R8A8Unorm, false)]
    [InlineData(Format.B8G8R8A8Srgb, Format.B8G8R8A8Unorm, true)]
    [InlineData(Format.B8G8R8A8Unorm, Format.B8G8R8A8Unorm, true)]
    public void ScaledPresentationPreservesColorsAndBlackBorders(
        Format sourceFormat, Format targetFormat, bool useCheckerboard)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var source = harness.CreateImage(CachedImageTests.Color2D(2, 2, format: sourceFormat));
        var target = harness.CreateImage(CachedImageTests.Color2D(1, 3, format: targetFormat));
        var snapshot = harness.CreateImage(CachedImageTests.Color2D(2, 2,
            format: VulkanVideoPresenter.GetPresentationSnapshotFormat(sourceFormat)));
        var sourceIsBlueFirst = sourceFormat is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm;
        byte[] pixel = sourceIsBlueFirst ? [35, 48, 196, 255] : [196, 48, 35, 255];
        byte[] payload = useCheckerboard
            ? [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255]
            : Enumerable.Range(0, 4).SelectMany(_ => pixel).ToArray();
        var colorLayer = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
        harness.UploadImage(source, payload, [new BufferImageCopy
        {
            ImageSubresource = colorLayer,
            ImageExtent = new Extent3D(2, 2, 1),
        }]);
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            source.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, null, command);
            snapshot.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
            var copy = new ImageCopy
            {
                SrcSubresource = colorLayer,
                DstSubresource = colorLayer,
                Extent = new Extent3D(2, 2, 1),
            };
            harness.Vk.CmdCopyImage(command, source.Backing.Handle, ImageLayout.TransferSrcOptimal,
                snapshot.Backing.Handle, ImageLayout.TransferDstOptimal, 1, &copy);
            snapshot.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, null, command);
            target.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
            var black = new ClearColorValue(0f, 0f, 0f, 1f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            harness.Vk.CmdClearColorImage(command, target.Backing.Handle,
                ImageLayout.TransferDstOptimal, &black, 1, &range);
            var clearComplete = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstAccessMask = AccessFlags2.TransferWriteBit,
            };
            VulkanSynchronization.PipelineBarrier(harness.Vk, command, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
                0, 1, &clearComplete, 0, null, 0, null);
            var blit = new ImageBlit
            {
                SrcSubresource = colorLayer,
                DstSubresource = colorLayer,
                SrcOffsets = new ImageBlit.SrcOffsetsBuffer { Element1 = new Offset3D(2, 2, 1) },
                DstOffsets = new ImageBlit.DstOffsetsBuffer
                {
                    Element0 = new Offset3D(0, 1, 0),
                    Element1 = new Offset3D(1, 2, 1),
                },
            };
            harness.Vk.CmdBlitImage(command, snapshot.Backing.Handle, ImageLayout.TransferSrcOptimal,
                target.Backing.Handle, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
        });
        var result = harness.ReadImage(target, [new BufferImageCopy
        {
            ImageSubresource = colorLayer,
            ImageExtent = new Extent3D(1, 3, 1),
        }], 12);
        byte[] expected = targetFormat == Format.B8G8R8A8Unorm ? [35, 48, 196, 255] : [196, 48, 35, 255];
        if (useCheckerboard) expected = [128, 128, 128, 255];
        for (var channel = 0; channel < 4; channel++)
        {
            Assert.InRange(Math.Abs(result[4 + channel] - expected[channel]), 0, 1);
        }

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, result[..4]);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, result[8..]);
        harness.AssertNoValidationMessages();
    }
}

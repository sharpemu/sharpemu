// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Images;

// Layout transitions and the buffer/image copies the image cache records.
public sealed unsafe partial class CachedImage
{
    private const AccessFlags WriteAccess = AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit | AccessFlags.MemoryWriteBit;
    private const AccessFlags TransferAccess = AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit;
    private const AccessFlags MemoryAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
    private const ImageLayout ReadyLayout = ImageLayout.General;
    private const AccessFlags ReadyAccess = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit;

    private ImageMemoryBarrier MakeBarrier(in ImageAccessState from, ImageLayout layout, AccessFlags access, uint baseLevel, uint levelCount, uint baseLayer, uint layerCount) => new()
    {
        SType = StructureType.ImageMemoryBarrier,
        SrcAccessMask = from.Access,
        DstAccessMask = access,
        OldLayout = from.Layout,
        NewLayout = layout,
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Image = Backing.Handle,
        SubresourceRange = new ImageSubresourceRange(ViewFormatRules.FullAspects(Backing.Format), baseLevel, levelCount, baseLayer, layerCount),
    };

    // Barriers for the requested state; a repeated write always gets a barrier.
    public (List<ImageMemoryBarrier> Barriers, PipelineStageFlags SourceStages) GetBarriers(ImageLayout layout, AccessFlags access, PipelineStageFlags stage, SubresourceRange? range)
    {
        var barriers = new List<ImageMemoryBarrier>();
        PipelineStageFlags sourceStages = 0;
        if (range is { } && Description.IsVolume)
        {
            range = range.Value with { BaseLayer = 0, LayerCount = 1 };
        }

        var levels = Description.Resources.Levels;
        var layers = Description.Resources.Layers;
        var partial = range is { } window &&
                      (window.BaseLevel != 0 || window.LevelCount != levels || window.BaseLayer != 0 || window.LayerCount != layers);
        var states = Backing.SubresourceStates;
        if (partial || states != null)
        {
            if (states == null)
            {
                states = new List<ImageAccessState>((int)(levels * layers));
                for (var index = 0; index < levels * layers; index++)
                {
                    states.Add(Backing.State);
                }

                Backing.SubresourceStates = states;
            }

            var baseLevel = partial ? range!.Value.BaseLevel : 0;
            var levelCount = partial ? range!.Value.LevelCount : levels;
            var baseLayer = partial ? range!.Value.BaseLayer : 0;
            var layerCount = partial ? range!.Value.LayerCount : layers;
            for (var level = baseLevel; level < baseLevel + levelCount; level++)
            {
                for (var layer = baseLayer; layer < baseLayer + layerCount; layer++)
                {
                    var index = (int)(level * layers + layer);
                    if (index >= states.Count)
                    {
                        throw SubmissionScheduler.Fatal($"The subresource index is outside the image: level={level} layer={layer} levels={levels} layers={layers}.");
                    }

                    var state = states[index];
                    var repeatedWrite = (state.Access & WriteAccess) != 0;
                    if (state.Layout != layout || state.Access != access || repeatedWrite)
                    {
                        barriers.Add(MakeBarrier(state, layout, access, level, 1, layer, 1));
                        sourceStages |= state.Stage;
                        states[index] = new ImageAccessState(stage, access, layout);
                    }
                }
            }

            if (!partial)
            {
                Backing.SubresourceStates = null;
            }
        }
        else
        {
            var state = Backing.State;
            var repeatedWrite = (state.Access & WriteAccess) != 0;
            if (state.Layout == layout && state.Access == access && !repeatedWrite)
            {
                return (barriers, sourceStages);
            }

            barriers.Add(MakeBarrier(state, layout, access, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers));
            sourceStages |= state.Stage;
        }

        Backing.State = new ImageAccessState(stage, access, layout);
        return (barriers, sourceStages);
    }

    public void Transition(ImageLayout layout, AccessFlags access, SubresourceRange? range, CommandBuffer command)
    {
        PipelineStageFlags stage = 0;
        if ((access & TransferAccess) != 0)
        {
            stage |= PipelineStageFlags.TransferBit;
        }

        if (access == 0 || (access & ~TransferAccess) != 0)
        {
            stage |= PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit;
        }

        var (barriers, sourceStages) = GetBarriers(layout, access, stage, range);
        if (barriers.Count == 0)
        {
            return;
        }

        _scheduler.EndRendering();
        RecordBarriers(command, sourceStages, stage, null, barriers);
    }

    private void RecordBarriers(CommandBuffer command, PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, BufferMemoryBarrier* bufferBarrier, List<ImageMemoryBarrier> imageBarriers)
    {
        var images = imageBarriers.ToArray();
        fixed (ImageMemoryBarrier* imagePointer = images)
        {
            _device.Vk.CmdPipelineBarrier(
                command, sourceStages == 0 ? PipelineStageFlags.TopOfPipeBit : sourceStages, destinationStages, DependencyFlags.ByRegionBit,
                0, null, bufferBarrier == null ? 0u : 1u, bufferBarrier, (uint)images.Length, imagePointer);
        }
    }

    private static BufferMemoryBarrier BufferBarrier(VkBuffer buffer, ulong offset, ulong size, AccessFlags source, AccessFlags destination) => new()
    {
        SType = StructureType.BufferMemoryBarrier,
        SrcAccessMask = source,
        DstAccessMask = destination,
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Buffer = buffer,
        Offset = offset,
        Size = size,
    };

    private CommandBuffer BeginTransfer(ReadOnlySpan<BufferImageCopy> copies, VkBuffer buffer, ulong size)
    {
        if (copies.IsEmpty || buffer.Handle == 0 || size == 0)
        {
            throw SubmissionScheduler.Fatal($"The image transfer needs copy regions, a buffer and a size: regions={copies.Length} size={size}.");
        }

        _scheduler.EndRendering();
        return new CommandBuffer(_scheduler.Current.Handle);
    }

    public void UploadFromBuffer(ReadOnlySpan<BufferImageCopy> copies, VkBuffer buffer, ulong offset, ulong size)
    {
        var command = BeginTransfer(copies, buffer, size);
        var bufferBarrier = BufferBarrier(buffer, offset, size, AccessFlags.MemoryWriteBit, AccessFlags.TransferReadBit);
        var (imageBarriers, sourceStages) = GetBarriers(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit, null);
        RecordBarriers(command, sourceStages | PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, &bufferBarrier, imageBarriers);
        fixed (BufferImageCopy* regions = copies)
        {
            _device.Vk.CmdCopyBufferToImage(command, buffer, Backing.Handle, ImageLayout.TransferDstOptimal, (uint)copies.Length, regions);
        }

        bufferBarrier = BufferBarrier(buffer, offset, size, AccessFlags.TransferReadBit, MemoryAccess);
        _device.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit, 0, null, 1, &bufferBarrier, 0, null);
        Transition(ReadyLayout, ReadyAccess, null, command);
    }

    public void DownloadToBuffer(ReadOnlySpan<BufferImageCopy> copies, VkBuffer buffer, ulong offset, ulong size)
    {
        var command = BeginTransfer(copies, buffer, size);
        var bufferBarrier = BufferBarrier(buffer, offset, size, MemoryAccess, AccessFlags.TransferWriteBit);
        var (imageBarriers, sourceStages) = GetBarriers(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, PipelineStageFlags.TransferBit, null);
        RecordBarriers(command, sourceStages | PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, &bufferBarrier, imageBarriers);
        fixed (BufferImageCopy* regions = copies)
        {
            _device.Vk.CmdCopyImageToBuffer(command, Backing.Handle, ImageLayout.TransferSrcOptimal, buffer, (uint)copies.Length, regions);
        }

        bufferBarrier = BufferBarrier(buffer, offset, size, AccessFlags.TransferWriteBit, MemoryAccess);
        _device.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.AllCommandsBit, DependencyFlags.ByRegionBit, 0, null, 1, &bufferBarrier, 0, null);
    }

    private static (uint SourceLayers, uint DestinationLayers) SanitizeCopyLayers(CachedImage source, CachedImage destination, uint depth)
    {
        var sourceType = source.Backing.ImageType;
        var destinationType = destination.Backing.ImageType;
        var sourceLayers = sourceType == ImageType.Type3D ? 1 : source.Backing.Layers;
        var destinationLayers = destinationType == ImageType.Type3D ? 1 : destination.Backing.Layers;
        if (sourceType == destinationType)
        {
            sourceLayers = destinationLayers = Math.Min(sourceLayers, destinationLayers);
        }
        else if (sourceType == ImageType.Type2D && destinationType == ImageType.Type3D)
        {
            sourceLayers = depth;
        }
        else if (sourceType == ImageType.Type3D && destinationType == ImageType.Type2D)
        {
            destinationLayers = depth;
        }

        return (sourceLayers, destinationLayers);
    }

    // Copies every shared mip level of the source; stencil is never copied here.
    public void CopyFrom(CachedImage source)
    {
        if (source.Backing.Samples != Backing.Samples)
        {
            throw SubmissionScheduler.Fatal($"An image copy needs equal sample counts: source={source.Backing.Samples} destination={Backing.Samples}.");
        }

        _scheduler.EndRendering();
        var levels = Math.Min(source.Backing.MipLevels, Backing.MipLevels);
        var baseDepth = Backing.ImageType == ImageType.Type3D ? Backing.Extent.Depth : source.Backing.Extent.Depth;
        var sourceAspect = ViewFormatRules.FullAspects(source.Backing.Format) & ~ImageAspectFlags.StencilBit;
        var destinationAspect = ViewFormatRules.FullAspects(Backing.Format) & ~ImageAspectFlags.StencilBit;
        var copies = new ImageCopy[levels];
        for (uint level = 0; level < levels; level++)
        {
            var width = Math.Max(source.Backing.Extent.Width >> (int)level, 1);
            var height = Math.Max(source.Backing.Extent.Height >> (int)level, 1);
            var depth = Math.Max(baseDepth >> (int)level, 1);
            var (sourceLayers, destinationLayers) = SanitizeCopyLayers(source, this, depth);
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(sourceAspect, level, 0, 1),
                DstSubresource = new ImageSubresourceLayers(destinationAspect, level, 0, 1),
            };
            if (source.Backing.ImageType == Backing.ImageType)
            {
                if (source.Backing.ImageType == ImageType.Type3D)
                {
                    copy.Extent = new Extent3D(width, height, depth);
                }
                else
                {
                    var layerCount = Math.Min(sourceLayers, destinationLayers);
                    copy.SrcSubresource.LayerCount = layerCount;
                    copy.DstSubresource.LayerCount = layerCount;
                    copy.Extent = new Extent3D(width, height, 1);
                }
            }
            else if (source.Backing.ImageType == ImageType.Type2D)
            {
                copy.SrcSubresource.LayerCount = sourceLayers;
                copy.Extent = new Extent3D(width, height, sourceLayers);
            }
            else
            {
                copy.DstSubresource.LayerCount = destinationLayers;
                copy.Extent = new Extent3D(width, height, destinationLayers);
            }

            copies[level] = copy;
        }

        if (copies.Length == 0)
        {
            return;
        }

        var command = new CommandBuffer(_scheduler.Current.Handle);
        source.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, null, command);
        Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
        fixed (ImageCopy* regions = copies)
        {
            _device.Vk.CmdCopyImage(command, source.Backing.Handle, ImageLayout.TransferSrcOptimal, Backing.Handle, ImageLayout.TransferDstOptimal, (uint)copies.Length, regions);
        }

        Transition(ReadyLayout, ReadyAccess, null, command);
    }

    // Resolves or copies one mip of the source into one mip of this single-sample image.
    public void ResolveFrom(CachedImage source, in SubresourceRange sourceRange, in SubresourceRange destinationRange)
    {
        if (Backing.Samples != 1 || source.Backing.ImageType != ImageType.Type2D || Backing.ImageType != ImageType.Type2D ||
            sourceRange.LevelCount != 1 || destinationRange.LevelCount != 1 ||
            sourceRange.BaseLevel >= source.Backing.MipLevels || destinationRange.BaseLevel >= Backing.MipLevels ||
            sourceRange.BaseLayer >= source.Backing.Layers || destinationRange.BaseLayer >= Backing.Layers)
        {
            throw SubmissionScheduler.Fatal(
                $"The resolve subresources are invalid: sourceMip={sourceRange.BaseLevel}+{sourceRange.LevelCount} sourceLayer={sourceRange.BaseLayer} destinationMip={destinationRange.BaseLevel}+{destinationRange.LevelCount} destinationLayer={destinationRange.BaseLayer} destinationSamples={Backing.Samples}.");
        }

        var layers = Math.Min(Math.Min(sourceRange.LayerCount, destinationRange.LayerCount), Math.Min(source.Backing.Layers - sourceRange.BaseLayer, Backing.Layers - destinationRange.BaseLayer));
        var sourceWidth = Math.Max(source.Backing.Extent.Width >> (int)sourceRange.BaseLevel, 1);
        var sourceHeight = Math.Max(source.Backing.Extent.Height >> (int)sourceRange.BaseLevel, 1);
        var destinationWidth = Math.Max(Backing.Extent.Width >> (int)destinationRange.BaseLevel, 1);
        var destinationHeight = Math.Max(Backing.Extent.Height >> (int)destinationRange.BaseLevel, 1);
        var copy = source.Backing.Samples == 1;
        var formatsAgree = copy ? ViewFormatRules.AreCompatible(source.Backing.Format, Backing.Format) : source.Backing.Format == Backing.Format;
        if (layers == 0 || Description.Extent.Width > sourceWidth || Description.Extent.Height > sourceHeight ||
            Description.Extent.Width > destinationWidth || Description.Extent.Height > destinationHeight || !formatsAgree)
        {
            throw SubmissionScheduler.Fatal(
                $"The resolve extent or formats do not agree: extent={Description.Extent.Width}x{Description.Extent.Height} source={sourceWidth}x{sourceHeight} destination={destinationWidth}x{destinationHeight} layers={layers} sourceFormat={(int)source.Backing.Format} destinationFormat={(int)Backing.Format}.");
        }

        var resolvedSource = sourceRange with { LayerCount = layers };
        var resolvedDestination = destinationRange with { LayerCount = layers };
        var extent = new Extent3D(Description.Extent.Width, Description.Extent.Height, 1);
        _scheduler.EndRendering();
        var command = new CommandBuffer(_scheduler.Current.Handle);
        source.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, resolvedSource, command);
        Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, resolvedDestination, command);
        var sourceLayers = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, resolvedSource.BaseLevel, resolvedSource.BaseLayer, layers);
        var destinationLayers = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, resolvedDestination.BaseLevel, resolvedDestination.BaseLayer, layers);
        if (copy)
        {
            var region = new ImageCopy { SrcSubresource = sourceLayers, DstSubresource = destinationLayers, Extent = extent };
            _device.Vk.CmdCopyImage(command, source.Backing.Handle, ImageLayout.TransferSrcOptimal, Backing.Handle, ImageLayout.TransferDstOptimal, 1, &region);
        }
        else
        {
            var region = new ImageResolve { SrcSubresource = sourceLayers, DstSubresource = destinationLayers, Extent = extent };
            _device.Vk.CmdResolveImage(command, source.Backing.Handle, ImageLayout.TransferSrcOptimal, Backing.Handle, ImageLayout.TransferDstOptimal, 1, &region);
        }
    }

    public static uint CalculateRowsPerCopy(ulong rowSize, uint rows, ulong capacity)
    {
        if (rowSize == 0 || rows == 0 || rowSize > capacity)
        {
            return 0;
        }

        return (uint)Math.Min(rows, capacity / rowSize);
    }

    // A storage image holds the stencil bytes while the shader runs; the attachment stays the owner.
    public CachedImage CreateStencilStorageImage()
    {
        if ((ViewFormatRules.FullAspects(Backing.Format) & ImageAspectFlags.StencilBit) == 0 ||
            Backing.ImageType != ImageType.Type2D || Backing.Samples != 1 || Backing.MipLevels != 1)
        {
            throw SubmissionScheduler.Fatal("Stencil storage needs a single-sample, single-level 2D stencil image.");
        }

        var description = ImageDescription.Create();
        description.PixelFormat = Format.R8Uint;
        description.GuestFormat = GuestPixelFormat.Bits8UInt;
        description.Extent = Backing.Extent;
        description.Resources = new SubresourceCount(1, Backing.Layers);
        description.Pitch = Backing.Extent.Width;
        description.BytesPerBlock = 1;
        return new CachedImage(_device, _scheduler, _guestBacking, description);
    }

    public void CopyStencilStorage(CachedImage storage, GpuBuffer buffer, bool writeBack)
    {
        if ((ViewFormatRules.FullAspects(Backing.Format) & ImageAspectFlags.StencilBit) == 0 ||
            Backing.ImageType != ImageType.Type2D || Backing.Samples != 1 || Backing.MipLevels != 1 ||
            storage.Backing.Format != Format.R8Uint || storage.Backing.ImageType != ImageType.Type2D ||
            storage.Backing.Samples != 1 || storage.Backing.MipLevels != 1 || storage.Backing.Layers != Backing.Layers ||
            storage.Backing.Extent.Width != Backing.Extent.Width || storage.Backing.Extent.Height != Backing.Extent.Height)
        {
            throw SubmissionScheduler.Fatal("The stencil storage image does not match its attachment.");
        }

        if (writeBack)
            CopyThroughBuffer(storage, buffer, ImageAspectFlags.ColorBit, ImageAspectFlags.StencilBit);
        else
            storage.CopyThroughBuffer(this, buffer, ImageAspectFlags.StencilBit, ImageAspectFlags.ColorBit);
    }

    // Reinterprets the source through a staging buffer, one row band at a time.
    public void CopyThroughBuffer(CachedImage source, GpuBuffer buffer) =>
        CopyThroughBuffer(source, buffer, ViewFormatRules.FullAspects(source.Backing.Format) & ~ImageAspectFlags.StencilBit,
            ViewFormatRules.FullAspects(Backing.Format) & ~ImageAspectFlags.StencilBit);

    private void CopyThroughBuffer(CachedImage source, GpuBuffer buffer, ImageAspectFlags sourceAspect, ImageAspectFlags destinationAspect)
    {
        if (buffer.Handle.Handle == 0 || source.Backing.Samples != 1 || Backing.Samples != 1)
        {
            throw SubmissionScheduler.Fatal($"A copy through a buffer needs single-sample images and a buffer: sourceSamples={source.Backing.Samples} destinationSamples={Backing.Samples}.");
        }

        _scheduler.EndRendering();
        var levels = Math.Min(source.Backing.MipLevels, Backing.MipLevels);
        var sourceBytes = sourceAspect == ImageAspectFlags.StencilBit ? 1u :
            DepthFormatRule.AspectTransferBytes(source.Backing.Format) is var sourceTransfer && sourceTransfer != 0 ? sourceTransfer : source.Description.BytesPerBlock;
        var destinationBytes = destinationAspect == ImageAspectFlags.StencilBit ? 1u :
            DepthFormatRule.AspectTransferBytes(Backing.Format) is var destinationTransfer && destinationTransfer != 0 ? destinationTransfer : Description.BytesPerBlock;
        var sourceBlock = source.Description.IsBlock ? 4u : 1u;
        var destinationBlock = Description.IsBlock ? 4u : 1u;
        if (levels == 0 || sourceBytes == 0 || sourceBytes != destinationBytes || sourceBlock != destinationBlock)
        {
            throw SubmissionScheduler.Fatal($"The images cannot be copied through a buffer: levels={levels} sourceBytes={sourceBytes} destinationBytes={destinationBytes} sourceBlock={sourceBlock} destinationBlock={destinationBlock}.");
        }

        var command = new CommandBuffer(_scheduler.Current.Handle);
        source.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, null, command);
        Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
        for (uint level = 0; level < levels; level++)
        {
            var width = Math.Max(source.Backing.Extent.Width >> (int)level, 1);
            var height = Math.Max(source.Backing.Extent.Height >> (int)level, 1);
            var sourceDepth = source.Backing.ImageType == ImageType.Type3D ? Math.Max(source.Backing.Extent.Depth >> (int)level, 1) : source.Backing.Layers;
            var destinationDepth = Backing.ImageType == ImageType.Type3D ? Math.Max(Backing.Extent.Depth >> (int)level, 1) : Backing.Layers;
            var slices = Math.Min(sourceDepth, destinationDepth);
            var blockRows = (height + sourceBlock - 1) / sourceBlock;
            var rowSize = (ulong)((width + sourceBlock - 1) / sourceBlock) * sourceBytes;
            var rowsPerCopy = CalculateRowsPerCopy(rowSize, blockRows, buffer.Size);
            if (slices == 0 || rowsPerCopy == 0)
            {
                throw SubmissionScheduler.Fatal($"The staging buffer cannot hold one row of the image: level={level} rowSize={rowSize} capacity={buffer.Size}.");
            }

            for (uint slice = 0; slice < slices; slice++)
            {
                for (uint blockRow = 0; blockRow < blockRows; blockRow += rowsPerCopy)
                {
                    var copyRows = Math.Min(rowsPerCopy, blockRows - blockRow);
                    var y = blockRow * sourceBlock;
                    var copyHeight = Math.Min(copyRows * sourceBlock, height - y);
                    var copySize = rowSize * copyRows;
                    var sourceCopy = new BufferImageCopy
                    {
                        ImageSubresource = new ImageSubresourceLayers(sourceAspect, level, source.Backing.ImageType == ImageType.Type3D ? 0 : slice, 1),
                        ImageOffset = new Offset3D(0, (int)y, source.Backing.ImageType == ImageType.Type3D ? (int)slice : 0),
                        ImageExtent = new Extent3D(width, copyHeight, 1),
                    };
                    var destinationCopy = sourceCopy;
                    destinationCopy.ImageSubresource = new ImageSubresourceLayers(destinationAspect, level, Backing.ImageType == ImageType.Type3D ? 0 : slice, 1);
                    destinationCopy.ImageOffset.Z = Backing.ImageType == ImageType.Type3D ? (int)slice : 0;
                    var barrier = BufferBarrier(buffer.Handle, 0, copySize, AccessFlags.TransferReadBit, AccessFlags.TransferWriteBit);
                    _device.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit, 0, null, 1, &barrier, 0, null);
                    _device.Vk.CmdCopyImageToBuffer(command, source.Backing.Handle, ImageLayout.TransferSrcOptimal, buffer.Handle, 1, &sourceCopy);
                    barrier = BufferBarrier(buffer.Handle, 0, copySize, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
                    _device.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, DependencyFlags.ByRegionBit, 0, null, 1, &barrier, 0, null);
                    _device.Vk.CmdCopyBufferToImage(command, buffer.Handle, Backing.Handle, ImageLayout.TransferDstOptimal, 1, &destinationCopy);
                }
            }
        }

        Transition(ReadyLayout, ReadyAccess, null, command);
    }

    // Copies a whole standalone image into one mip and layer of this image.
    public void CopyMipFrom(CachedImage source, uint mip, uint layer)
    {
        if (source.Backing.Samples != Backing.Samples || mip >= Backing.MipLevels || layer >= Backing.Layers)
        {
            throw SubmissionScheduler.Fatal($"The mip copy target is invalid: mip={mip} layer={layer} levels={Backing.MipLevels} layers={Backing.Layers} sourceSamples={source.Backing.Samples} destinationSamples={Backing.Samples}.");
        }

        _scheduler.EndRendering();
        var width = Math.Max(Backing.Extent.Width >> (int)mip, 1);
        var height = Math.Max(Backing.Extent.Height >> (int)mip, 1);
        var depth = Math.Max(Backing.Extent.Depth >> (int)mip, 1);
        if (width != source.Backing.Extent.Width || height != source.Backing.Extent.Height)
        {
            throw SubmissionScheduler.Fatal($"The mip copy source extent does not match the target mip: source={source.Backing.Extent.Width}x{source.Backing.Extent.Height} mip={width}x{height}.");
        }

        var (sourceLayers, destinationLayers) = SanitizeCopyLayers(source, this, depth);
        var aspects = ViewFormatRules.FullAspects(source.Backing.Format);
        if (aspects != ViewFormatRules.FullAspects(Backing.Format))
        {
            throw SubmissionScheduler.Fatal($"The mip copy aspects differ: source=0x{(uint)aspects:x} destination=0x{(uint)ViewFormatRules.FullAspects(Backing.Format):x}.");
        }

        var copies = stackalloc ImageCopy[2];
        uint copyCount = 0;
        foreach (var aspect in new[] { ImageAspectFlags.ColorBit, ImageAspectFlags.DepthBit, ImageAspectFlags.StencilBit })
        {
            if ((aspects & aspect) == 0)
            {
                continue;
            }

            copies[copyCount++] = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(aspect, 0, 0, sourceLayers),
                DstSubresource = new ImageSubresourceLayers(aspect, mip, layer, destinationLayers),
                Extent = new Extent3D(width, height, depth),
            };
        }

        var command = new CommandBuffer(_scheduler.Current.Handle);
        Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
        source.Transition(ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, null, command);
        _device.Vk.CmdCopyImage(command, source.Backing.Handle, ImageLayout.TransferSrcOptimal, Backing.Handle, ImageLayout.TransferDstOptimal, copyCount, copies);
        Transition(ReadyLayout, ReadyAccess, null, command);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Images;

// How a request relates to the images that already cover its range, and the copies that follow.
public sealed partial class GuestImageCache
{
    private readonly record struct OverlapResolution(ResourceSlotIdentifier Image, int Mip = -1, int Layer = -1);

    private static bool SameExtent(in Extent3D left, in Extent3D right) => left.Width == right.Width && left.Height == right.Height && left.Depth == right.Depth;

    private static bool HasSameBacking(in ImageDescription cached, in ImageDescription requested, bool exactFormat)
    {
        if (cached.Data.Address != requested.Data.Address || cached.Data.Size != requested.Data.Size ||
            !SameExtent(cached.Extent, requested.Extent) || cached.Samples != requested.Samples ||
            cached.BytesPerBlock != requested.BytesPerBlock || cached.TileMode != requested.TileMode ||
            !ViewFormatRules.AreCompatible(cached.PixelFormat, requested.PixelFormat))
        {
            return false;
        }

        if (cached.Type != requested.Type && !SameExtent(requested.Extent, new Extent3D(1, 1, 1)))
        {
            return false;
        }

        return !exactFormat || cached.PixelFormat == requested.PixelFormat;
    }

    private static ImageRole UploadRole(CachedImage image)
    {
        if (image.Description.IsDepth)
        {
            return ImageRole.DepthTarget;
        }

        if (image.Uses.RenderTarget)
        {
            return ImageRole.ColorTarget;
        }

        if (image.Uses.VideoOut)
        {
            return ImageRole.DisplaySurface;
        }

        return image.Uses.Storage ? ImageRole.StorageImage : ImageRole.Texture;
    }

    private bool CanReadBack(CachedImage image)
    {
        if (!image.SafeToDownload)
        {
            return false;
        }

        var range = image.Description.Data;
        return !_bufferCache.HasGpuDirtyBytes(range.Address, range.Size);
    }

    private static void PrepareCopyTarget(CachedImage image)
    {
        if (image.IsCpuDirty)
        {
            image.RefreshComplete();
        }
    }

    private void RefreshCopySource(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        RefreshFromGuest(imageIdentifier, RefreshRequest(image));
        if (image.IsDefinitelyCpuDirty)
        {
            throw SubmissionScheduler.Fatal($"An image copy source stayed CPU-dirty after its refresh: address=0x{image.Description.Data.Address:X16} size=0x{image.Description.Data.Size:X}.");
        }
    }

    // Converts between 16-bit color and the widened depth format through two temporary buffers.
    private bool CopyDepth16(CachedImage destination, CachedImage source)
    {
        var sourceDepth = source.Description.IsDepth;
        var destinationDepth = destination.Description.IsDepth;
        if (sourceDepth == destinationDepth)
        {
            return false;
        }

        var depth = sourceDepth ? source : destination;
        var color = sourceDepth ? destination : source;
        var transferBytes = DepthFormatRule.AspectTransferBytes(depth.Backing.Format);
        if (depth.Description.BytesPerBlock != sizeof(ushort) || color.Description.BytesPerBlock != sizeof(ushort) || transferBytes != sizeof(uint))
        {
            return false;
        }

        if (source.Backing.Samples != 1 || destination.Backing.Samples != 1 ||
            source.Description.Resources.Levels != 1 || destination.Description.Resources.Levels != 1 ||
            !SameExtent(source.Description.Extent, destination.Description.Extent) ||
            source.Description.Resources.Layers != destination.Description.Resources.Layers)
        {
            throw SubmissionScheduler.Fatal(
                $"A 16-bit depth conversion copy needs single-sample, single-level images of one extent and layer count: sourceSamples={source.Backing.Samples} destinationSamples={destination.Backing.Samples} " +
                $"sourceLevels={source.Description.Resources.Levels} destinationLevels={destination.Description.Resources.Levels} sourceLayers={source.Description.Resources.Layers} destinationLayers={destination.Description.Resources.Layers}.");
        }

        var layers = depth.Description.Resources.Layers;
        var depthSlice = (ulong)depth.Description.Pitch * depth.Description.Extent.Height * transferBytes;
        var colorSlice = (ulong)color.Description.Pitch * color.Description.Extent.Height * sizeof(ushort);
        if (layers == 0 || depthSlice > ulong.MaxValue / layers || colorSlice > ulong.MaxValue / layers)
        {
            throw SubmissionScheduler.Fatal($"The 16-bit depth conversion size overflows: layers={layers} depthSlice={depthSlice} colorSlice={colorSlice}.");
        }

        var depthSize = depthSlice * layers;
        var colorSize = colorSlice * layers;
        var depthCopies = new BufferImageCopy[layers];
        var colorCopies = new BufferImageCopy[layers];
        for (uint layer = 0; layer < layers; layer++)
        {
            depthCopies[layer] = new BufferImageCopy
            {
                BufferOffset = depthSlice * layer,
                BufferRowLength = depth.Description.Pitch,
                BufferImageHeight = depth.Description.Extent.Height,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, layer, 1),
                ImageExtent = depth.Description.Extent,
            };
            colorCopies[layer] = new BufferImageCopy
            {
                BufferOffset = colorSlice * layer,
                BufferRowLength = color.Description.Pitch,
                BufferImageHeight = color.Description.Extent.Height,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, layer, 1),
                ImageExtent = color.Description.Extent,
            };
        }

        var depthBuffer = _tiler.GetScratchBuffer(depthSize);
        var colorBuffer = _tiler.GetScratchBuffer(colorSize);
        var widenLayout = new DepthConversionLayout
        {
            Width = depth.Description.Extent.Width,
            Height = depth.Description.Extent.Height,
            Layers = layers,
            SourceRowStride = (ulong)color.Description.Pitch * sizeof(ushort),
            TargetRowStride = (ulong)depth.Description.Pitch * transferBytes,
            SourceSliceStride = colorSlice,
            TargetSliceStride = depthSlice,
        };
        var useFloat32Depth = DepthFormatRule.AspectTransferFormat(depth.Backing.Format) == Format.D32Sfloat;
        if (sourceDepth)
        {
            source.DownloadToBuffer(depthCopies, depthBuffer.Buffer, depthBuffer.Offset, depthBuffer.Size);
            _tiler.ConvertDepth16(depthBuffer, colorBuffer, DepthConversionDirection.Narrow, useFloat32Depth, new DepthConversionLayout
            {
                Width = widenLayout.Width,
                Height = widenLayout.Height,
                Layers = widenLayout.Layers,
                SourceRowStride = widenLayout.TargetRowStride,
                TargetRowStride = widenLayout.SourceRowStride,
                SourceSliceStride = widenLayout.TargetSliceStride,
                TargetSliceStride = widenLayout.SourceSliceStride,
            });
            destination.UploadFromBuffer(colorCopies, colorBuffer.Buffer, colorBuffer.Offset, colorBuffer.Size);
        }
        else
        {
            source.DownloadToBuffer(colorCopies, colorBuffer.Buffer, colorBuffer.Offset, colorBuffer.Size);
            _tiler.ConvertDepth16(colorBuffer, depthBuffer, DepthConversionDirection.Widen, useFloat32Depth, widenLayout);
            destination.UploadFromBuffer(depthCopies, depthBuffer.Buffer, depthBuffer.Offset, depthBuffer.Size);
        }

        return true;
    }

    private void CopyWholeImage(ResourceSlotIdentifier destinationImageIdentifier, ResourceSlotIdentifier sourceImageIdentifier)
    {
        RefreshCopySource(sourceImageIdentifier);
        var destination = _slots[destinationImageIdentifier];
        var source = _slots[sourceImageIdentifier];
        WatchImage(destinationImageIdentifier);
        if (source.Backing.Samples != destination.Backing.Samples)
        {
            throw SubmissionScheduler.Fatal($"An image copy needs equal sample counts: source={source.Backing.Samples} destination={destination.Backing.Samples}.");
        }

        PrepareCopyTarget(destination);
        if (source.IsBufferModified)
        {
            if (source.Description.Data == destination.Description.Data)
            {
                destination.MarkBufferModified();
            }

            return;
        }

        var sourceDepth = source.Description.IsDepth;
        var destinationDepth = destination.Description.IsDepth;
        var directCopy = source.Backing.Format == destination.Backing.Format ||
                         (!sourceDepth && !destinationDepth && ViewFormatRules.BlockBytes(source.Backing.Format) == ViewFormatRules.BlockBytes(destination.Backing.Format));
        if (directCopy)
        {
            destination.CopyFrom(source);
        }
        else if (!CopyDepth16(destination, source))
        {
            if (source.Backing.Samples != 1 || destination.Backing.Samples != 1)
            {
                throw SubmissionScheduler.Fatal($"A cross-format multisample image copy is not supported: sourceFormat={(int)source.Backing.Format} destinationFormat={(int)destination.Backing.Format} samples={source.Backing.Samples}.");
            }

            destination.CopyThroughBuffer(source, _bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal));
        }

        if (source.IsGpuModified)
        {
            destination.MarkGpuModified();
        }

        destination.ClearBufferModified();
    }

    private void CopyIntoMip(ResourceSlotIdentifier destinationImageIdentifier, ResourceSlotIdentifier sourceImageIdentifier, uint mip, uint layer)
    {
        RefreshCopySource(sourceImageIdentifier);
        var destination = _slots[destinationImageIdentifier];
        var source = _slots[sourceImageIdentifier];
        WatchImage(destinationImageIdentifier);
        if (source.IsBufferModified || source.Backing.Samples != destination.Backing.Samples)
        {
            throw SubmissionScheduler.Fatal($"The mip copy source ownership or sample count is invalid: bufferModified={source.IsBufferModified} sourceSamples={source.Backing.Samples} destinationSamples={destination.Backing.Samples}.");
        }

        destination.CopyMipFrom(source, mip, layer);
        if (source.IsGpuModified)
        {
            destination.MarkGpuModified();
        }
    }

    private ResourceSlotIdentifier ResolveDepthOverlap(in ImageDescription requested, ImageRole role, ResourceSlotIdentifier cachedImageIdentifier)
    {
        var cached = _slots[cachedImageIdentifier];
        ref var cachedInfo = ref cached.Description;
        if (!cachedInfo.IsDepth && !requested.IsDepth)
        {
            return ResourceSlotIdentifier.Invalid;
        }

        var stencilMatch = requested.HasStencil == cachedInfo.HasStencil;
        var bppMatch = requested.BytesPerBlock == cachedInfo.BytesPerBlock;
        var rawD16Texture =
            role == ImageRole.Texture && cachedInfo.IsDepth &&
            cachedInfo.GuestFormat == GuestPixelFormat.Bits16UNorm && requested.GuestFormat == GuestPixelFormat.Bits16UInt &&
            requested.PixelFormat == Format.R16Uint && cached.Backing.Samples == 1 && requested.Samples == 1 &&
            requested.Data == cachedInfo.Data && SameExtent(requested.Extent, cachedInfo.Extent) && requested.Resources == cachedInfo.Resources &&
            requested.Type == cachedInfo.Type && requested.Pitch == cachedInfo.Pitch && requested.TileMode == cachedInfo.TileMode &&
            !requested.HasStencil && !cachedInfo.HasStencil && !requested.HasMetadata && !cachedInfo.HasMetadata;
        var retainCachedLayout =
            requested.Samples == 1 && cachedInfo.Samples == 1 && cached.Backing.Samples == 1 &&
            requested.BytesPerBlock == cachedInfo.BytesPerBlock && requested.Data.Address == cachedInfo.Data.Address &&
            requested.Data.Size < cachedInfo.Data.Size && SameExtent(requested.Extent, cachedInfo.Extent) &&
            requested.Resources.Levels == 1 && cachedInfo.Resources.Levels == 1 &&
            requested.Resources.Layers != 0 && cachedInfo.Resources.Layers != 0 &&
            requested.Resources.Layers < cachedInfo.Resources.Layers &&
            requested.Type == cachedInfo.Type && requested.Pitch == cachedInfo.Pitch && requested.TileMode == cachedInfo.TileMode &&
            requested.MipLayout[0].Offset == 0 && cachedInfo.MipLayout[0].Offset == 0 &&
            requested.MipLayout[0].Size == requested.Data.Size && cachedInfo.MipLayout[0].Size == cachedInfo.Data.Size &&
            requested.Data.Size % requested.Resources.Layers == 0 && cachedInfo.Data.Size % cachedInfo.Resources.Layers == 0 &&
            requested.Data.Size / requested.Resources.Layers == cachedInfo.Data.Size / cachedInfo.Resources.Layers &&
            !requested.HasStencil && !cachedInfo.HasStencil && !requested.HasMetadata && !cachedInfo.HasMetadata;
        var recreate = cachedInfo.Resources < requested.Resources;
        switch (role)
        {
            case ImageRole.Texture:
                recreate |= requested.IsDepth && !cachedInfo.IsDepth;
                recreate |= rawD16Texture;
                break;
            case ImageRole.StorageImage:
            case ImageRole.ColorTarget:
            case ImageRole.DisplaySurface:
                recreate |= cachedInfo.IsDepth;
                break;
            case ImageRole.DepthTarget:
                recreate |= !cachedInfo.IsDepth;
                recreate |= cachedInfo.IsDepth && !(stencilMatch && bppMatch);
                break;
        }

        if (!recreate)
        {
            return cachedImageIdentifier;
        }

        RefreshFromGuest(cachedImageIdentifier, RefreshRequest(cached));
        var replacementInfo = requested;
        if (retainCachedLayout)
        {
            replacementInfo.Data = cachedInfo.Data;
            replacementInfo.Resources = cachedInfo.Resources;
            replacementInfo.MipLayout = cachedInfo.MipLayout;
        }
        else
        {
            replacementInfo.Resources = SubresourceCount.Max(requested.Resources, cachedInfo.Resources);
        }

        replacementInfo.HtileClearMask = 0;
        var replacementImageIdentifier = InsertImage(replacementInfo);
        var replacement = _slots[replacementImageIdentifier];
        replacement.Uses = cached.Uses;
        if (cached.Binding.IsBound || cached.Binding.IsTarget)
        {
            cached.Binding.NeedsRebind = true;
        }

        if (cached.Backing.Samples == replacement.Backing.Samples)
        {
            var copySupported = cached.Backing.Samples == 1 || cached.Backing.Format == replacement.Backing.Format ||
                                (!cachedInfo.IsDepth && !replacement.Description.IsDepth && ViewFormatRules.AreCompatible(cached.Backing.Format, replacement.Backing.Format));
            if (copySupported)
            {
                CopyWholeImage(replacementImageIdentifier, cachedImageIdentifier);
            }
            else
            {
                Console.Error.WriteLine($"[GPU][WARN] A cross-format multisample depth copy is not supported: cachedFormat={(int)cached.Backing.Format} replacementFormat={(int)replacement.Backing.Format} samples={cached.Backing.Samples}.");
            }
        }
        else if (cached.Backing.Samples == 1 && replacement.Backing.Samples > 1 && replacement.Description.IsDepth)
        {
            RefreshCopySource(cachedImageIdentifier);
            if (cached.IsBufferModified || cached.IsDefinitelyCpuDirty)
            {
                throw SubmissionScheduler.Fatal($"The multisample depth conversion source is not native-current: address=0x{cachedInfo.Data.Address:X16} bufferModified={cached.IsBufferModified} cpuDirty={cached.IsDefinitelyCpuDirty}.");
            }

            PrepareCopyTarget(replacement);
            _blit.Reinterpret(cached, replacement);
            TakeGpuOwnership(replacement);
        }
        else
        {
            Console.Error.WriteLine($"[GPU][WARN] An unequal-sample depth overlap copy is not supported: cachedSamples={cached.Backing.Samples} replacementSamples={replacement.Backing.Samples}.");
        }

        ReleaseImage(cachedImageIdentifier);
        return replacementImageIdentifier;
    }

    private OverlapResolution ResolveOverlap(in ImageDescription requested, ImageRole role, ResourceSlotIdentifier cachedImageIdentifier, ResourceSlotIdentifier mergedImageIdentifier)
    {
        var cached = _slots.TryGet(cachedImageIdentifier);
        if (cached == null)
        {
            return new OverlapResolution(mergedImageIdentifier);
        }

        ref var cachedInfo = ref cached.Description;
        var currentTick = _scheduler.CurrentTick;
        var safeToDelete = currentTick - Math.Min(currentTick, cached.LastAccessTick) > TicksBeforeRemoval;

        if (requested.Data.Address == cachedInfo.Data.Address)
        {
            var requestedBlock = requested.BytesPerBlock * requested.Samples;
            var cachedBlock = cachedInfo.BytesPerBlock * cachedInfo.Samples;
            var requestedBlockExtent = requested.BlockExtent;
            var cachedBlockExtent = cachedInfo.BlockExtent;
            if (requestedBlockExtent.Width != cachedBlockExtent.Width || requestedBlockExtent.Height != cachedBlockExtent.Height || requestedBlock != cachedBlock)
            {
                if (safeToDelete)
                {
                    ReleaseImage(cachedImageIdentifier);
                }

                return new OverlapResolution(mergedImageIdentifier);
            }

            var depthImageIdentifier = ResolveDepthOverlap(requested, role, cachedImageIdentifier);
            if (depthImageIdentifier.IsValid)
            {
                return new OverlapResolution(depthImageIdentifier);
            }

            if (requested.IsBlock && !cachedInfo.IsBlock)
            {
                return new OverlapResolution(GrowImage(requested, cachedImageIdentifier));
            }

            if (requested.Data.Size == cachedInfo.Data.Size && (requested.IsVolume || cachedInfo.IsVolume))
            {
                return new OverlapResolution(GrowImage(requested, cachedImageIdentifier));
            }

            if (requested.TileMode != cachedInfo.TileMode)
            {
                if (safeToDelete)
                {
                    ReleaseImage(cachedImageIdentifier);
                }

                return new OverlapResolution(mergedImageIdentifier);
            }

            if (requested.Data.Size == cachedInfo.Data.Size && requested.Resources == cachedInfo.Resources && requested.Type == cachedInfo.Type &&
                requested.Extent.Width > cachedInfo.Extent.Width && requested.Extent.Height >= cachedInfo.Extent.Height &&
                requested.Extent.Depth >= cachedInfo.Extent.Depth && ViewFormatRules.AreCompatible(cachedInfo.PixelFormat, requested.PixelFormat))
            {
                return new OverlapResolution(GrowImage(requested, cachedImageIdentifier));
            }

            if (requested.PixelFormat != cachedInfo.PixelFormat || requested.Data.Size <= cachedInfo.Data.Size)
            {
                var resultImageIdentifier = mergedImageIdentifier.IsValid ? mergedImageIdentifier : cachedImageIdentifier;
                var result = _slots.TryGet(resultImageIdentifier);
                return new OverlapResolution(result != null && ViewFormatRules.AreCompatible(result.Description.PixelFormat, requested.PixelFormat) ? resultImageIdentifier : ResourceSlotIdentifier.Invalid);
            }

            if (requested.Type == cachedInfo.Type && requested.Resources > cachedInfo.Resources)
            {
                return new OverlapResolution(GrowImage(requested, cachedImageIdentifier));
            }

            throw SubmissionScheduler.Fatal(
                $"An equal-address image overlap cannot be resolved: address=0x{requested.Data.Address:X16} requested={requested.Resources.Levels}x{requested.Resources.Layers} " +
                $"cached={cachedInfo.Resources.Levels}x{cachedInfo.Resources.Layers} requestedSize=0x{requested.Data.Size:X16} cachedSize=0x{cachedInfo.Data.Size:X16} " +
                $"type={(uint)requested.Type}/{(uint)cachedInfo.Type} tile={(uint)requested.TileMode}/{(uint)cachedInfo.TileMode}.");
        }

        if (requested.Data.Address > cachedInfo.Data.Address)
        {
            var mip = requested.FindMatchingMipLevel(cachedInfo);
            if (mip >= 0)
            {
                var layer = requested.FindMatchingArraySlice(cachedInfo, mip);
                if (layer >= 0)
                {
                    return new OverlapResolution(cachedImageIdentifier, mip, layer);
                }
            }

            if (safeToDelete)
            {
                ReleaseImage(cachedImageIdentifier);
            }

            return new OverlapResolution(ResourceSlotIdentifier.Invalid);
        }

        var containedMip = cachedInfo.FindMatchingMipLevel(requested);
        if (containedMip >= 0)
        {
            var containedLayer = cachedInfo.FindMatchingArraySlice(requested, containedMip);
            if (containedLayer >= 0)
            {
                if (cached.Binding.IsTarget)
                {
                    cached.Binding.NeedsRebind = true;
                    if (mergedImageIdentifier.IsValid)
                    {
                        _slots[mergedImageIdentifier].Binding.IsTarget = true;
                    }

                    ReleaseImage(cachedImageIdentifier);
                    return new OverlapResolution(mergedImageIdentifier);
                }

                if (mergedImageIdentifier.IsValid)
                {
                    CopyIntoMip(mergedImageIdentifier, cachedImageIdentifier, (uint)containedMip, (uint)containedLayer);
                    ReleaseImage(cachedImageIdentifier);
                }
            }
        }

        return new OverlapResolution(mergedImageIdentifier);
    }

    // Replaces the source with a larger image that starts with the source contents.
    private ResourceSlotIdentifier GrowImage(in ImageDescription description, ResourceSlotIdentifier sourceImageIdentifier)
    {
        RefreshCopySource(sourceImageIdentifier);
        var expandedImageIdentifier = InsertImage(description);
        var expanded = _slots[expandedImageIdentifier];
        var source = _slots[sourceImageIdentifier];
        expanded.Uses = source.Uses;
        if (source.Binding.IsBound || source.Binding.IsTarget)
        {
            source.Binding.NeedsRebind = true;
        }

        PopulateFromGuest(expandedImageIdentifier, ImageRequest.Refresh(description, UploadRole(source)));
        CopyWholeImage(expandedImageIdentifier, sourceImageIdentifier);
        ReleaseImage(sourceImageIdentifier);
        return expandedImageIdentifier;
    }

    private void AssociateStencilRange(ResourceSlotIdentifier depthImageIdentifier, GuestSpan stencil)
    {
        if (!ImageDescription.IsValidRange(stencil))
        {
            throw SubmissionScheduler.Fatal($"The stencil association range is invalid: address=0x{stencil.Address:X16} size=0x{stencil.Size:X16}.");
        }

        var depth = _slots[depthImageIdentifier];
        if (!depth.Description.IsDepth || !depth.Description.HasStencil)
        {
            throw SubmissionScheduler.Fatal($"A stencil association needs a depth/stencil image: address=0x{depth.Description.Data.Address:X16} format={(int)depth.Description.PixelFormat}.");
        }

        var association = ResourceSlotIdentifier.Invalid;
        foreach (var imageIdentifier in FindImagesInRange(stencil.Address, stencil.Size, pageOverlap: false))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner != null && owner.Description.Data.Address == stencil.Address)
            {
                association = imageIdentifier;
            }
        }

        if (!association.IsValid)
        {
            var description = ImageDescription.Create();
            description.Data = stencil;
            description.Extent = depth.Description.Extent;
            association = InsertImage(description);
        }

        var record = _slots[association];
        TouchImage(record);
        record.AssociateDepth(depthImageIdentifier);
    }
}

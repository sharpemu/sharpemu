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
        var sourceType = source.Backing.ImageType;
        var destinationType = destination.Backing.ImageType;
        var compatibleTypes = sourceType == destinationType ||
                              (sourceType == ImageType.Type2D && destinationType == ImageType.Type3D) ||
                              (sourceType == ImageType.Type3D && destinationType == ImageType.Type2D);
        var directCopy = compatibleTypes &&
                         (source.Backing.Format == destination.Backing.Format ||
                          (!sourceDepth && !destinationDepth && ViewFormatRules.BlockBytes(source.Backing.Format) == ViewFormatRules.BlockBytes(destination.Backing.Format)));
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

    // Two formats of one surface with the same layout are separate images over the same guest
    // bytes, which the GPU writes through either of them. Before one is used, it copies the
    // newest GPU contents among them, as unified guest memory would show them (the alias
    // synchronization of other unified-memory emulators). Depth keeps its reinterpretation path.
    private void SynchronizeAliases(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (image.Description.IsDepth || ImageDescription.IsEmptyRange(image.Description.Data))
        {
            return;
        }

        var newest = ResourceSlotIdentifier.Invalid;
        var newestContents = image.ContentSequence;
        var newestStencil = ResourceSlotIdentifier.Invalid;
        var newestStencilContents = newestContents;
        foreach (var aliasIdentifier in FindImagesInRange(image.Description.Data.Address, image.Description.Data.Size, pageOverlap: false))
        {
            // A depth image's stencil plane of exactly this span, which the GPU wrote after this
            // image last changed, is newer than the image (the guest memory under both is stale).
            if (aliasIdentifier != imageIdentifier && _slots.TryGet(aliasIdentifier) is { DepthOwner.IsValid: true, IsGpuModified: true } plane &&
                plane.Description.Data == image.Description.Data && plane.ContentSequence > newestStencilContents)
            {
                newestStencil = aliasIdentifier;
                newestStencilContents = plane.ContentSequence;
            }

            if (aliasIdentifier == imageIdentifier || _slots.TryGet(aliasIdentifier) is not { } alias || !alias.Registered ||
                alias.DepthOwner.IsValid || !alias.SafeToDownload || alias.ContentSequence <= newestContents ||
                !IsSameLayoutAlias(image.Description, alias.Description))
            {
                continue;
            }

            newest = aliasIdentifier;
            newestContents = alias.ContentSequence;
        }

        if (newestStencil.IsValid && newestStencilContents > newestContents)
        {
            // Only an 8-bit single-level image can show the plane; its values come from the owner.
            var owner = _slots[_slots[newestStencil].DepthOwner];
            if (image.Backing.Exists && image.Backing.Format == Format.R8Uint && CanWriteBackStencilPlane(owner) &&
                image.Backing.MipLevels == 1 && image.Backing.Layers == owner.Backing.Layers &&
                image.Backing.Extent.Width <= owner.Backing.Extent.Width && image.Backing.Extent.Height <= owner.Backing.Extent.Height)
            {
                owner.CopyStencilStorage(image, _bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal), writeBack: false);
                // The copy is newer than guest memory, which a refresh must not paint over it.
                TakeGpuOwnership(image);
                image.ContentSequence = newestStencilContents;
                return;
            }
        }

        if (!newest.IsValid)
        {
            return;
        }

        CopyWholeImage(imageIdentifier, newest);
        image.ContentSequence = newestContents;
    }

    private static bool IsSameLayoutAlias(in ImageDescription image, in ImageDescription alias) =>
        !alias.IsDepth && alias.Data == image.Data && SameExtent(alias.Extent, image.Extent) && alias.Type == image.Type &&
        alias.Samples == image.Samples && alias.BytesPerBlock == image.BytesPerBlock && alias.TileMode == image.TileMode &&
        alias.Resources == image.Resources && alias.FirstLevel == image.FirstLevel && SameMipLayout(alias, image);

    // Two sizes of one surface, as a dynamic-resolution title alternates between them.
    private static bool IsResizeSibling(in ImageDescription image, in ImageDescription candidate) =>
        !candidate.IsDepth && candidate.Data.Address == image.Data.Address && candidate.Data.Size != image.Data.Size &&
        !SameExtent(candidate.Extent, image.Extent) && candidate.Type == image.Type &&
        candidate.Samples == image.Samples && candidate.BytesPerBlock == image.BytesPerBlock &&
        candidate.TileMode == image.TileMode && candidate.Resources == image.Resources &&
        candidate.FirstLevel == 0 && image.FirstLevel == 0 && candidate.PixelFormat == image.PixelFormat;

    // A freshly created size of a surface copies the shared rectangle of the newest
    // GPU-written sibling, so switching the render resolution keeps the previous
    // frame's content instead of starting from stale guest bytes.
    private void SyncResizedSibling(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (image.Description.IsDepth || ImageDescription.IsEmptyRange(image.Description.Data) ||
            image.Description.Resources.Layers != 1 || image.Backing.Samples != 1)
        {
            return;
        }

        var newest = ResourceSlotIdentifier.Invalid;
        var newestWrite = image.GpuWriteSequence;
        foreach (var candidateIdentifier in FindImagesInRange(image.Description.Data.Address, image.Description.Data.Size, pageOverlap: false))
        {
            if (candidateIdentifier == imageIdentifier || _slots.TryGet(candidateIdentifier) is not { } candidate || !candidate.Registered ||
                candidate.DepthOwner.IsValid || candidate.GpuWriteSequence <= newestWrite || !candidate.IsGpuModified ||
                !IsResizeSibling(image.Description, candidate.Description))
            {
                continue;
            }

            newest = candidateIdentifier;
            newestWrite = candidate.GpuWriteSequence;
        }

        if (!newest.IsValid)
        {
            return;
        }

        var source = _slots[newest];
        // Newer guest bytes reach the image through its own refresh; copying the sibling
        // would paint stale GPU contents over them.
        if (source.Backing.Format != image.Backing.Format || source.IsBufferModified || source.IsCpuDirty || image.IsBufferModified)
        {
            return;
        }

        var width = Math.Min(source.Description.Extent.Width, image.Description.Extent.Width);
        var height = Math.Min(source.Description.Extent.Height, image.Description.Extent.Height);
        if (width == 0 || height == 0)
        {
            return;
        }

        WatchImage(imageIdentifier);
        image.CopyRegionFrom(source, 0, 0, 0, width, height);
        image.MarkGpuModified();
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

    private static bool GrowImageToLayers(ref ImageDescription info, uint layers)
    {
        var current = info.Resources.Layers;
        if (info.Resources.Levels != 1 || current == 0 || layers <= current ||
            info.MipLayout[0].Offset != 0 || info.LevelBytes(0) != info.Data.Size)
        {
            return false;
        }

        static bool TryStretch(ref GuestSpan range, uint currentLayers, uint newLayers)
        {
            if (range.Size == 0)
            {
                return true;
            }

            if (range.Size % currentLayers != 0)
            {
                return false;
            }

            range = new GuestSpan(range.Address, range.Size / currentLayers * newLayers);
            return true;
        }

        var grown = info;
        if (!TryStretch(ref grown.Data, current, layers) ||
            !TryStretch(ref grown.Stencil, current, layers) ||
            !TryStretch(ref grown.Metadata.Range, current, layers))
        {
            return false;
        }

        if (grown.SliceStride == 0)
        {
            grown.MipLayout[0].Size = grown.Data.Size;
        }
        grown.Resources = grown.Resources with { Layers = layers };
        info = grown;
        return true;
    }

    private static bool SameMipLayout(in ImageDescription left, in ImageDescription right)
    {
        for (var level = 0; level < ImageDescription.MaxLevels; level++)
        {
            var a = left.MipLayout[level];
            var b = right.MipLayout[level];
            if (left.SliceStride != right.SliceStride || a.Offset != b.Offset || a.Size != b.Size || a.Pitch != b.Pitch || a.Height != b.Height)
            {
                return false;
            }
        }

        return true;
    }

    private ResourceSlotIdentifier ResolveDepthOverlap(in ImageDescription requested, ImageRole role, ResourceSlotIdentifier cachedImageIdentifier)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageOverlap);
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
        // HTile belongs to the attachment view; it does not change the shared depth slice stride.
        var separateDepthMetadata = role == ImageRole.DepthTarget && requested.Metadata.Kind == MetadataKind.Htile &&
            requested.Metadata.Compression == DisplayCompression.Uncompressed && !requested.Metadata.StencilCompressed;
        var retainCachedLayout =
            requested.Samples == 1 && cachedInfo.Samples == 1 && cached.Backing.Samples == 1 &&
            requested.BytesPerBlock == cachedInfo.BytesPerBlock && requested.Data.Address == cachedInfo.Data.Address &&
            requested.Data.Size < cachedInfo.Data.Size && SameExtent(requested.Extent, cachedInfo.Extent) &&
            requested.Resources.Levels == 1 && cachedInfo.Resources.Levels == 1 &&
            requested.Resources.Layers != 0 && cachedInfo.Resources.Layers != 0 &&
            requested.Resources.Layers < cachedInfo.Resources.Layers &&
            requested.Type == cachedInfo.Type && requested.Pitch == cachedInfo.Pitch && requested.TileMode == cachedInfo.TileMode &&
            requested.MipLayout[0].Offset == 0 && cachedInfo.MipLayout[0].Offset == 0 &&
            requested.LevelBytes(0) == requested.Data.Size && cachedInfo.LevelBytes(0) == cachedInfo.Data.Size &&
            requested.Data.Size % requested.Resources.Layers == 0 && cachedInfo.Data.Size % cachedInfo.Resources.Layers == 0 &&
            requested.Data.Size / requested.Resources.Layers == cachedInfo.Data.Size / cachedInfo.Resources.Layers &&
            !requested.HasStencil && !cachedInfo.HasStencil && (!requested.HasMetadata || separateDepthMetadata) && !cachedInfo.HasMetadata;
        var recreate = cachedInfo.Resources < requested.Resources;
        switch (role)
        {
            case ImageRole.Texture:
                recreate |= requested.IsDepth && !cachedInfo.IsDepth;
                recreate |= rawD16Texture;
                // A color texture that reads neither the depth nor the stencil plane
                // reinterprets the memory as color, so its bytes move to a color image.
                recreate |= cachedInfo.IsDepth && !requested.IsDepth &&
                            !ViewFormatRules.IsDepthCompatible(requested.PixelFormat) &&
                            !ViewFormatRules.IsStencilViewFormat(requested.PixelFormat);
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
            replacementInfo.SliceStride = cachedInfo.SliceStride;
        }
        else
        {
            var merged = SubresourceCount.Max(requested.Resources, cachedInfo.Resources);
            if (merged.Layers > requested.Resources.Layers && !GrowImageToLayers(ref replacementInfo, merged.Layers))
            {
                merged = merged with { Layers = requested.Resources.Layers };
            }

            replacementInfo.Resources = merged;
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

    // Memory keeps its bytes when another image or a buffer write reuses part of it. An image
    // whose GPU contents never reached guest memory moves them into the buffer over its range
    // before it is dropped, so a later image over those bytes starts from them, not from stale
    // guest memory. False when the contents cannot be copied; they are lost as before.
    private bool PublishBeforeDrop(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (!image.IsGpuModified || image.DepthOwner.IsValid || !image.SafeToDownload)
        {
            return false;
        }

        var range = image.Description.Data;
        if (_bufferCache.ObtainBufferForImageWriteBack(range.Address, range.Size) is not { } buffer ||
            !TryDownloadImageToBuffer(image, buffer))
        {
            return false;
        }

        image.ClearGpuModified();
        return true;
    }

    private OverlapResolution ResolveOverlap(in ImageDescription requested, ImageRole role, ResourceSlotIdentifier cachedImageIdentifier, ResourceSlotIdentifier mergedImageIdentifier)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageOverlap);
        var cached = _slots.TryGet(cachedImageIdentifier);
        if (cached == null)
        {
            return new OverlapResolution(mergedImageIdentifier);
        }

        // Resolving can allocate a replacement, and an allocation under memory pressure collects
        // images not used this tick. The image being resolved is the copy source of that
        // replacement; touching it keeps it out of the collection.
        TouchImage(cached);

        ref var cachedInfo = ref cached.Description;
        var currentTick = _scheduler.CurrentTick;
        var safeToDelete = currentTick - Math.Min(currentTick, cached.LastAccessTick) > TicksBeforeRemoval;
        if (requested.FirstLevel != 0 || cachedInfo.FirstLevel != 0)
        {
            // An image of a streamed chain's resident levels shares only an identical chain (the
            // same-backing match). It is a copy of guest memory that a lookup recreates cheaply, so
            // another resource over its range replaces it; a request for resident levels over
            // another image is a separate texture (the lookup took the whole chain when that image
            // holds GPU contents).
            if (cachedInfo.FirstLevel != 0 && !cached.IsGpuModified)
            {
                cached.Binding.NeedsRebind |= cached.Binding.IsBound || cached.Binding.IsTarget;
                ReleaseImage(cachedImageIdentifier);
            }

            return new OverlapResolution(mergedImageIdentifier);
        }
        var requestedBlock = requested.BytesPerBlock * requested.Samples;
        var cachedBlock = cachedInfo.BytesPerBlock * cachedInfo.Samples;
        var requestedBlockExtent = requested.BlockExtent;
        var cachedBlockExtent = cachedInfo.BlockExtent;

        if (requested.Data.Address == cachedInfo.Data.Address &&
            requestedBlockExtent.Width == cachedBlockExtent.Width && requestedBlockExtent.Height == cachedBlockExtent.Height &&
            requestedBlock == cachedBlock)
        {
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

            if (requested.TileMode != cachedInfo.TileMode ||
                (requested.Resources == cachedInfo.Resources && !SameMipLayout(requested, cachedInfo)))
            {
                if (safeToDelete)
                {
                    PublishBeforeDrop(cachedImageIdentifier);
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

            // Dynamic resolution: the same surface at another size. Both sizes stay alive,
            // and the lookup syncs the requested one from the most recently written sibling,
            // so a size switch neither drops the other image nor reuses it with the wrong
            // extent (which would paint the target at a mismatched resolution). An equal
            // allocation re-described with another extent is the growth case above, not a
            // resolution switch.
            if (requested.Data.Size != cachedInfo.Data.Size && requested.PixelFormat == cachedInfo.PixelFormat &&
                requested.Type == cachedInfo.Type && requested.Samples == cachedInfo.Samples &&
                requested.TileMode == cachedInfo.TileMode && requested.Resources == cachedInfo.Resources &&
                !SameExtent(requested.Extent, cachedInfo.Extent))
            {
                return new OverlapResolution(ResourceSlotIdentifier.Invalid);
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

            if (requested.IsVolume != cachedInfo.IsVolume)
            {
                if (safeToDelete)
                {
                    ReleaseImage(cachedImageIdentifier);
                }

                return new OverlapResolution(mergedImageIdentifier);
            }

            throw SubmissionScheduler.Fatal(
                $"An equal-address image overlap cannot be resolved: address=0x{requested.Data.Address:X16} requested={requested.Resources.Levels}x{requested.Resources.Layers} " +
                $"cached={cachedInfo.Resources.Levels}x{cachedInfo.Resources.Layers} requestedSize=0x{requested.Data.Size:X16} cachedSize=0x{cachedInfo.Data.Size:X16} " +
                $"type={(uint)requested.Type}/{(uint)cachedInfo.Type} tile={(uint)requested.TileMode}/{(uint)cachedInfo.TileMode}.");
        }

        var requestedMip = requested.FindMatchingMipLevel(cachedInfo);
        if (requestedMip >= 0)
        {
            var layer = requested.FindMatchingArraySlice(cachedInfo, requestedMip);
            return new OverlapResolution(cachedImageIdentifier, requestedMip, layer);
        }

        var containedMip = cachedInfo.FindMatchingMipLevel(requested);
        if (containedMip >= 0)
        {
            var containedLayer = cachedInfo.FindMatchingArraySlice(requested, containedMip);
            if (!mergedImageIdentifier.IsValid)
            {
                return new OverlapResolution(GrowImage(requested, cachedImageIdentifier));
            }

            cached.Binding.NeedsRebind |= cached.Binding.IsBound || cached.Binding.IsTarget;
            _slots[mergedImageIdentifier].Binding.IsTarget |= cached.Binding.IsTarget;
            CopyIntoMip(mergedImageIdentifier, cachedImageIdentifier, (uint)containedMip, (uint)containedLayer);
            ReleaseImage(cachedImageIdentifier);
            return new OverlapResolution(mergedImageIdentifier);
        }

        if (requested.Data.Address >= cachedInfo.Data.Address && safeToDelete)
        {
            PublishBeforeDrop(cachedImageIdentifier);
            ReleaseImage(cachedImageIdentifier);
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
        var firstLayer = !source.IsBufferModified && !source.IsCpuDirty && source.Backing.Exists &&
            CanUploadArrayGrowthSuffix(source.Description, description)
            ? source.Description.Resources.Layers : 0;
        try
        {
            PopulateFromGuest(expandedImageIdentifier, ImageRequest.Refresh(description, UploadRole(source)),
                firstLayer == 0 ? "expand" : "expand-partial", firstLayer);
            var mip = source.Description.FindMatchingMipLevel(description);
            var layer = source.Description.FindMatchingArraySlice(description, mip);
            if (layer >= 0)
            {
                CopyIntoMip(expandedImageIdentifier, sourceImageIdentifier, (uint)mip, (uint)layer);
            }
            else
            {
                CopyWholeImage(expandedImageIdentifier, sourceImageIdentifier);
            }
        }
        catch
        {
            ReleaseImage(expandedImageIdentifier);
            throw;
        }

        if (source.Binding.IsBound || source.Binding.IsTarget)
        {
            source.Binding.NeedsRebind = true;
        }

        ReleaseImage(sourceImageIdentifier);
        return expandedImageIdentifier;
    }

    internal static bool CanUploadArrayGrowthSuffix(in ImageDescription source, in ImageDescription destination) =>
        source.Type == GuestImageType.Color2D && destination.Type == GuestImageType.Color2D &&
        !source.IsDepth && !destination.IsDepth && !source.IsBlock && !destination.IsBlock &&
        !source.HasMetadata && !destination.HasMetadata && !source.HasStencil && !destination.HasStencil &&
        !source.Bgra16 && !destination.Bgra16 &&
        source.Metadata.Compression == DisplayCompression.Uncompressed && destination.Metadata.Compression == DisplayCompression.Uncompressed &&
        source.Samples == 1 && destination.Samples == 1 && source.Resources.Levels == 1 && destination.Resources.Levels == 1 &&
        source.Resources.Layers > 0 && source.Resources.Layers < destination.Resources.Layers &&
        source.Data.Address == destination.Data.Address && source.GuestFormat == destination.GuestFormat &&
        source.Pitch == destination.Pitch && SameExtent(source.Extent, destination.Extent) &&
        source.FindMatchingArraySlice(destination, 0) == 0;

    // A compute shader writes the blocks of a compressed image through an
    // uncompressed view, but the driver could not give that image storage usage.
    // The blocks move to an uncompressed image of the same memory; the next
    // compressed request copies them back (the IsBlock branch of ResolveOverlap).
    private ResourceSlotIdentifier ReplaceCompressedForStorage(in ImageDescription requested, ResourceSlotIdentifier cachedImageIdentifier)
    {
        var replacementImageIdentifier = InsertImage(requested);
        var replacement = _slots[replacementImageIdentifier];
        var cached = _slots[cachedImageIdentifier];
        replacement.Uses = cached.Uses;
        if (cached.Binding.IsBound || cached.Binding.IsTarget)
        {
            cached.Binding.NeedsRebind = true;
        }

        CopyWholeImage(replacementImageIdentifier, cachedImageIdentifier);
        ReleaseImage(cachedImageIdentifier);
        return replacementImageIdentifier;
    }

    private ResourceSlotIdentifier AssociateStencilRange(ResourceSlotIdentifier depthImageIdentifier, GuestSpan stencil, bool restoreNativeStencil = false)
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
            // Only an image of exactly this span can stand for the plane: the stencil upload detiles the
            // whole depth extent from it. A proxy of another span or extent belongs to another surface
            // and stays; a standalone image of the span (a dynamic-resolution view of the plane may be
            // smaller than the depth image) becomes the proxy, and an image of another size (a texture
            // reading part of the plane) is a separate resource.
            var owner = _slots.TryGet(imageIdentifier);
            if (owner != null && owner.Description.Data == stencil &&
                (!owner.DepthOwner.IsValid || owner.Description.Extent.Equals(depth.Description.Extent)))
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

        // A proxy another depth image owns holds that image's writes, not this one's (Ghost of Yotei
        // copies its depth buffer into a second depth image that shares the stencil plane). What the GPU
        // wrote reaches guest memory before the plane changes owner, and the new owner loads it from there.
        if (record.DepthOwner.IsValid && record.DepthOwner != depthImageIdentifier)
        {
            if (record.IsGpuModified)
            {
                if (_slots.TryGet(record.DepthOwner) is { } previousOwner && CanWriteBackStencilPlane(previousOwner))
                {
                    WriteBackStencilPlane(previousOwner, record.Description.Data);
                }

                record.ClearGpuModified();
            }

            record.InvalidateCpuWrite(record.Description.Data.Address, record.Description.Data.Size);
        }

        // An image a shader wrote through a color view before this depth target took the plane
        // (Ghost of Yotei builds the stencil its lighting reads that way) is the association now,
        // and holds the plane's newest values: they move into the depth image's stencil aspect.
        var takeContents = record.Backing.Exists && !record.DepthOwner.IsValid && record.IsGpuModified &&
                           TakeStandaloneStencilPlane(depth, association);
        TouchImage(record);
        // A current ordinary image becomes an S8 proxy below. Preserve its native
        // bytes before changing the owner; clean flags alone cannot refresh S8.
        // Definite CPU/buffer updates instead use the normal stencil upload. A
        // maybe-dirty hash may resolve unchanged, so it still needs preservation.
        if (restoreNativeStencil && !record.DepthOwner.IsValid && record.Backing.Exists &&
            !record.IsDefinitelyCpuDirty && !record.IsBufferModified &&
            !depth.Description.Metadata.StencilCompressed && depth.Backing.Samples == 1 &&
            record.Backing.Format is Format.R8Uint or Format.R8Unorm &&
            record.Backing.ImageType == ImageType.Type2D && record.Backing.Samples == 1 &&
            record.Backing.MipLevels == 1 && depth.Backing.MipLevels == 1 &&
            record.Backing.Layers == depth.Backing.Layers && SameExtent(record.Backing.Extent, depth.Backing.Extent))
        {
            depth.CopyStencilStorage(record, _bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal), writeBack: true);
        }

        // Release the former image watch on conversion. Keep an active stencil watch on reuse.
        if (!record.DepthOwner.IsValid)
        {
            UnwatchImage(association);
        }
        record.AssociateDepth(depthImageIdentifier);
        if (takeContents)
        {
            // The depth image now holds values newer than guest memory.
            TakeGpuOwnership(record);
        }

        return association;
    }

    // Copies a GPU-written single-level 8-bit image of the stencil plane into the depth image's
    // stencil aspect; true when the depth image holds its values.
    private bool TakeStandaloneStencilPlane(CachedImage depth, ResourceSlotIdentifier standaloneIdentifier)
    {
        var standalone = _slots[standaloneIdentifier];
        if (standalone.Binding.IsBound || standalone.Binding.IsTarget)
        {
            standalone.Binding.NeedsRebind = true;
        }

        if (standalone.Backing.Format == Format.R8Uint && CanWriteBackStencilPlane(depth) &&
            standalone.Backing.MipLevels == 1 && standalone.Backing.Layers == depth.Backing.Layers &&
            standalone.Backing.Extent.Width <= depth.Backing.Extent.Width && standalone.Backing.Extent.Height <= depth.Backing.Extent.Height)
        {
            depth.CopyStencilStorage(standalone, _bufferCache.GetUtilityBuffer(GpuBufferUsage.DeviceLocal), writeBack: true);
            return true;
        }

        // Nothing can carry the values over; the association reads guest memory as before.
        Console.Error.WriteLine($"[GPU][WARN] A GPU-written image of a stencil plane cannot reach its depth image: address=0x{standalone.Description.Data.Address:X16} format={(int)standalone.Backing.Format}.");
        return false;
    }
}

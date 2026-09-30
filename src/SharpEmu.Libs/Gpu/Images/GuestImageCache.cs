// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Images;

// The host image store: images registered by guest range, page-watched, uploaded on use,
// downloaded back when scheduled, with metadata tracking and overlap resolution.
public sealed unsafe partial class GuestImageCache : IGuestImageCache, IGuestImageStore, IDisposable
{
    private const ulong TicksBeforeRemoval = 32;
    private const ulong MiB = 1024 * 1024;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly RegionLock _lock = new(RegionLock.Category.ImageCache);
    private readonly PageGuard _pages;
    private readonly ColorToMultisampleDepthBlit _blit;
    private readonly GpuTiler _tiler;
    private readonly GuestBufferCache _bufferCache;
    private readonly IGuestBackedSpace _backing;
    private readonly SlotTable<CachedImage> _slots = new();
    private readonly ImageBackingPool? _backingPool;
    private readonly ImagePageOwnerTable _pageOwners = new();
    private readonly Dictionary<Format, ResourceSlotIdentifier> _nullImages = new();
    private RecencyQueue<ResourceSlotIdentifier> _recencyQueue = new();
    private readonly HashSet<ResourceSlotIdentifier> _scheduledReadbacks = new();
    private readonly SortedDictionary<ulong, SurfaceMetadata> _surfaceMetadata = new();
    private ulong _totalUsedMemory;
    private ulong _collectionStartBytes = 1536 * MiB;
    private ulong _memoryPressureBytes = 1536 * MiB;
    private ulong _criticalMemoryBytes = 3072 * MiB;
    private ulong _collectionTick;
    private uint _queryEpoch;
    private bool _readbackLinearImages;
    private bool _disposed;
    private readonly Action _reportAllocationFailure;
    private ulong _createdImageCount;

    public GuestImageCache(GpuDeviceInfo device, SubmissionScheduler scheduler, PageGuard pages, GuestBufferCache bufferCache, IGuestBackedSpace backing, bool readbackLinearImages)
    {
        _device = device;
        _reportAllocationFailure = () => ReportImageMemory("allocation-failure", true);
        _scheduler = scheduler;
        _pages = pages;
        _bufferCache = bufferCache;
        _backing = backing;
        _readbackLinearImages = readbackLinearImages;
        _blit = new ColorToMultisampleDepthBlit(device, scheduler);
        _tiler = new GpuTiler(device, scheduler, bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream));
        _backingPool = ImageBackingPool.Enabled ? new ImageBackingPool(device) : null;
    }

    public ulong TotalUsedMemory => _totalUsedMemory;

    public int ImageCount => _slots.Count;

    // Finishes GPU work, removes every image from the index and its watches, then frees the images.
    public void Shutdown()
    {
        if (_scheduler.Active)
        {
            _scheduler.Finish();
        }

        var registered = new List<ResourceSlotIdentifier>();
        _slots.ForEach((imageIdentifier, image) =>
        {
            if (image.Registered)
            {
                registered.Add(imageIdentifier);
            }
        });
        foreach (var imageIdentifier in registered)
        {
            RemoveFromIndex(imageIdentifier);
        }

        if (_scheduler.Active)
        {
            _scheduler.Finish();
        }

        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (ImageClearTrace.Enabled) _volumeTraceStates.WriteTo(Console.Error, "VolumeImageHistory");
        PrintRangeResourceHistory();
        _slots.ForEach((_, image) => image.Dispose());
        _backingPool?.Dispose();
        _tiler.Dispose();
        _blit.Dispose();
    }

    private static bool IsValidRange(ulong address, ulong size) => ImageDescription.IsValidRange(new GuestSpan(address, size));

    private static ImageRequest RefreshRequest(CachedImage image) => ImageRequest.Refresh(image.Description, UploadRole(image));

    // The image for the request; creates, grows or replaces cached images as the overlap rules decide.
    public ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat = false)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageLookup);
        var command = _scheduler.Current;
        if (command.IsInvalid)
        {
            throw SubmissionScheduler.Fatal("An image lookup needs a command buffer that is recording.");
        }

        ValidateRequest(request);
        if (ImageDescription.IsEmptyRange(request.Description.Data))
        {
            using var nullHeld = _lock.Hold();
            return GetNullImage(request);
        }

        var found = LookUpImage(ref request, exactFormat);
        if (request.Role is ImageRole.Texture or ImageRole.StorageImage)
        {
            if (request.Description.Metadata.Kind == MetadataKind.Dcc)
            {
                using var held = _lock.Hold();
                var image = _slots[found];
                if (image.Description.Metadata.Kind == MetadataKind.None)
                {
                    image.Description.Metadata.Kind = MetadataKind.Dcc;
                    image.Description.Metadata.Range = request.Description.Metadata.Range;
                }
            }

            ref readonly var description = ref _slots[found].Description;
            if (description.DccSliceSize is var sliceSize and not 0)
            {
                SynchronizeGuestDccMetadata(description.Metadata.Range.Address, sliceSize, request.View.BaseLayer, request.View.LayerCount);
            }
        }

        return found;
    }

    private ResourceSlotIdentifier LookUpImage(ref ImageRequest request, bool exactFormat)
    {
        using var held = _lock.Hold();
        if (TryReuseLookup(ref request, exactFormat, out var reused))
        {
            if (ImageClearTrace.Enabled) ImageTraceRange.NoteImageLookup(request.Description.Data.Address, request.Description.Data.Size);
            PrintResourceHistory(request);
            TraceVolumeState("lookup", _slots[reused], request);
            return reused;
        }

        var original = request;
        var generation = _lookupGeneration;
        var result = ResourceSlotIdentifier.Invalid;
        if (ImageClearTrace.Enabled) ImageTraceRange.NoteImageLookup(request.Description.Data.Address, request.Description.Data.Size);
        PrintResourceHistory(request);
        var candidates = FindImagesInRange(request.Description.Data.Address, request.Description.Data.Size, pageOverlap: false);
        if (ImageClearTrace.Enabled && request.TraceTextureMetadataAddress != 0)
        {
            ImageClearTrace.Write($"texture-owner-search image=0x{request.Description.Data.Address:X16} candidates={candidates.Count}");
            foreach (var candidate in candidates)
            {
                var cached = _slots[candidate];
                ImageClearTrace.Write($"texture-owner-candidate request=0x{request.Description.Data.Address:X16} " +
                    $"cached=0x{cached.Description.Data.Address:X16} size=0x{cached.Description.Data.Size:X} " +
                    $"format={cached.Description.PixelFormat} host=0x{cached.Backing.Handle.Handle:X} " +
                    $"cpuDirty={cached.IsCpuDirty} bufferDirty={cached.IsBufferModified} gpuDirty={cached.IsGpuModified} target={cached.Uses.RenderTarget}");
            }
        }
        foreach (var imageIdentifier in candidates)
        {
            if (HasSameBacking(_slots[imageIdentifier].Description, request.Description, exactFormat))
            {
                result = imageIdentifier;
            }
        }

        var sameBacking = result.IsValid;
        var viewMip = -1;
        var viewLayer = -1;
        var exactMatch = result.IsValid;
        if (!result.IsValid)
        {
            foreach (var candidate in candidates)
            {
                viewMip = -1;
                viewLayer = -1;
                var mergedDescription = result.IsValid ? _slots[result].Description : request.Description;
                var overlap = ResolveOverlap(mergedDescription, request.Role, candidate, result);
                if (overlap.Image.IsValid)
                {
                    result = overlap.Image;
                    viewMip = overlap.Mip;
                    viewLayer = overlap.Layer;
                }
            }
        }

        if (result.IsValid)
        {
            var resolved = _slots[result];
            if (exactFormat && resolved.Description.PixelFormat != request.Description.PixelFormat)
            {
                result = ResourceSlotIdentifier.Invalid;
            }
            else if (resolved.Description.Resources < request.Description.Resources)
            {
                ReleaseImage(result);
                result = ResourceSlotIdentifier.Invalid;
            }
            else if (!resolved.SupportsViewType(request.View) &&
                     resolved.Description.Extent.Height == 1 && resolved.Description.Extent.Depth == 1 &&
                     ((resolved.Backing.ImageType == ImageType.Type1D &&
                       request.View.Type is ImageViewType.Type2D or ImageViewType.Type2DArray) ||
                      (resolved.Backing.ImageType == ImageType.Type2D &&
                       request.View.Type is ImageViewType.Type1D or ImageViewType.Type1DArray)))
            {
                // Keep all cached subresources when an overlap needs a different dimensional type.
                var replacement = resolved.Description;
                replacement.Type = request.View.Type is ImageViewType.Type1D or ImageViewType.Type1DArray
                    ? GuestImageType.Color1D : GuestImageType.Color2D;
                result = GrowImage(replacement, result);
            }
            else if (request.Role == ImageRole.StorageImage && viewMip < 0 && viewLayer < 0 &&
                     resolved.Description.IsBlock && !request.Description.IsBlock &&
                     (resolved.Backing.Usage & ImageUsageFlags.StorageBit) == 0)
            {
                result = ReplaceCompressedForStorage(request.Description, result);
            }
        }

        if (RenderPhaseProfile.Enabled && result.IsValid)
        {
            if (exactMatch) _exactImageMatches++;
            else _overlapImageMatches++;
        }
        if (!result.IsValid)
        {
            if (RenderPhaseProfile.Enabled)
            {
                if (candidates.Count == 0) _emptyImageSearches++;
                else _unmatchedImageOverlaps++;
                if (request.Description.Data.Size >= 64UL * 1024 * 1024 && _largeImageLookupReports < 64)
                {
                    _largeImageLookupReports++;
                    Console.Error.WriteLine($"[GPU][INFO] ImageMemoryLookupLarge address=0x{request.Description.Data.Address:X} " +
                        $"role={request.Role} candidates={candidates.Count} exact_format={exactFormat} " +
                        $"view_type={request.View.Type} view_mip={request.View.BaseLevel} view_layer={request.View.BaseLayer}");
                    var reported = 0;
                    foreach (var candidate in candidates)
                    {
                        if (reported++ == 8) break;
                        var previous = _slots.TryGet(candidate);
                        if (previous == null) continue;
                        Console.Error.WriteLine($"[GPU][INFO] ImageMemoryOverlapLarge address=0x{previous.Description.Data.Address:X} " +
                            $"guest_bytes={previous.Description.Data.Size} format={previous.Description.PixelFormat} " +
                            $"extent={previous.Description.Extent.Width}x{previous.Description.Extent.Height}x{previous.Description.Extent.Depth} " +
                            $"layers={previous.Description.Resources.Layers} levels={previous.Description.Resources.Levels} " +
                            $"registered={previous.Registered} gpu_modified={previous.IsGpuModified}");
                    }
                }
            }
            result = InsertImage(request.Description);
            var inserted = _slots[result];
            if (_bufferCache.HasGpuDirtyBytes(inserted.Description.Data.Address, inserted.Description.Data.Size))
            {
                inserted.MarkBufferModified();
            }
        }

        var image = _slots[result];
        if (request.Role == ImageRole.DisplaySurface && request.Description.Metadata.Compression != DisplayCompression.Uncompressed)
        {
            var guestDirty = image.IsBufferModified || image.IsCpuDirty;
            var nativeCurrent = (image.Uses.RenderTarget || image.IsGpuModified) && !guestDirty;
            if (!nativeCurrent)
            {
                throw SubmissionScheduler.Fatal(
                    $"A compressed display surface can only be read from clean native GPU contents: address=0x{image.Description.Data.Address:X16} bufferModified={image.IsBufferModified} cpuDirty={image.IsCpuDirty} gpuModified={image.IsGpuModified}.");
            }
        }

        if (viewMip >= 0)
        {
            request.View = request.View with { BaseLevel = (uint)viewMip };
        }

        if (viewLayer >= 0)
        {
            request.View = request.View with { BaseLayer = (uint)viewLayer };
        }

        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        if (sameBacking && generation == _lookupGeneration)
        {
            RememberLookup(original, exactFormat, request.View, result);
        }

        TraceVolumeState("lookup", image, request);
        return result;
    }

    // Uploads guest changes of an image that was found earlier.
    public void RefreshImage(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        TouchImage(image);
        RefreshFromGuest(imageIdentifier, RefreshRequest(image));
    }

    // The image behind a slot without touching its recency; false once the slot was erased.
    public bool TryGetImage(ResourceSlotIdentifier imageIdentifier, out CachedImage image)
    {
        using var held = _lock.Hold();
        var found = _slots.TryGet(imageIdentifier);
        image = found!;
        return found != null;
    }

    public CachedImage GetImage(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        TouchImage(image);
        return image;
    }

    public ImageView AcquireTextureView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        TouchImage(image);
        var hasData = !ImageDescription.IsEmptyRange(image.Description.Data);
        if (hasData && (!image.Registered || image.DepthOwner.IsValid || image.Binding.NeedsRebind))
        {
            throw SubmissionScheduler.Fatal($"A texture must be found again before its view is acquired: address=0x{image.Description.Data.Address:X16} registered={image.Registered} proxy={image.DepthOwner.IsValid} rebind={image.Binding.NeedsRebind}.");
        }

        if (request.Role == ImageRole.StorageImage)
        {
            image.MarkGpuModified();
        }

        if (hasData)
        {
            RefreshFromGuest(imageIdentifier, request);
            MergeMipTailBlock(image);
        }

        switch (request.Role)
        {
            case ImageRole.Texture:
                break;
            case ImageRole.StorageImage:
                if (hasData)
                {
                    if (!image.Registered || image.DepthOwner.IsValid)
                    {
                        throw SubmissionScheduler.Fatal($"A storage image that is not available cannot be acquired: address=0x{image.Description.Data.Address:X16}.");
                    }

                    TakeGpuOwnership(image);
                }

                ScheduleReadback(imageIdentifier, image);
                break;
            default:
                throw SubmissionScheduler.Fatal($"The texture role is invalid: role={request.Role}.");
        }

        TraceVolumeState("acquire", image, request);
        return image.GetOrCreateView(request.View);
    }

    public ImageView AcquireColorTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        if (MeshDrawTrace.Active) MeshDrawTrace.Range("color-acquire", request.Description.Data.Address, request.Description.Data.Size, $"view={request.View}", remember: true);
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        if (request.Role != ImageRole.ColorTarget)
        {
            throw SubmissionScheduler.Fatal($"The color-target role is invalid: role={request.Role}.");
        }

        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        if (!image.Registered || image.DepthOwner.IsValid || image.Binding.NeedsRebind)
        {
            throw SubmissionScheduler.Fatal($"A color target must be found again before its view is acquired: address=0x{image.Description.Data.Address:X16} registered={image.Registered} proxy={image.DepthOwner.IsValid} rebind={image.Binding.NeedsRebind}.");
        }

        TouchImage(image);
        image.MarkGpuModified();
        image.Uses.RenderTarget = true;
        TraceVolumeState("color-acquire", image, request);
        RefreshFromGuest(imageIdentifier, request);
        TraceMetadataBinding(request);
        // DCC lives in its own allocation; it is registered at bind time, keeping a pending fill.
        if (request.Description.Metadata.Kind == MetadataKind.Dcc)
        {
            image.Description.Metadata = request.Description.Metadata;
            var address = request.Description.Metadata.Range.Address;
            IncludeMetadataWriteRange(address, Math.Max(request.Description.Metadata.Range.Size,
                request.Description.DccSliceSize * request.Description.TransferLayers));
            if (!_surfaceMetadata.TryGetValue(address, out var metadata) || metadata.Invalidated)
            {
                metadata = new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc };
                _surfaceMetadata[address] = metadata;
            }
            else if (metadata.Kind == SurfaceMetadataKind.PendingDcc)
            {
                metadata.Kind = SurfaceMetadataKind.Dcc;
            }
            else if (metadata.Kind != SurfaceMetadataKind.Dcc)
            {
                throw SubmissionScheduler.Fatal($"A color target reuses metadata that is not DCC: address=0x{address:X16} kind={metadata.Kind}.");
            }

            metadata.Size = Math.Max(metadata.Size, request.Description.DccSliceSize * request.Description.TransferLayers);
            metadata.RangeSize = Math.Max(metadata.RangeSize, request.Description.Metadata.Range.Size);
            metadata.NativeColorClear = request.Description.Metadata.NativeColorClear;
            image.MetadataRegistration = metadata;
        }

        TakeGpuOwnership(image);
        ScheduleReadback(imageIdentifier, image);
        return image.GetOrCreateView(request.View);
    }

    public ImageView AcquireDepthTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        if (MeshDrawTrace.Active) MeshDrawTrace.Range("depth-acquire", request.Description.Data.Address, request.Description.Data.Size, $"view={request.View}", remember: true);
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        if (request.Role != ImageRole.DepthTarget)
        {
            throw SubmissionScheduler.Fatal($"The depth-target role is invalid: role={request.Role}.");
        }

        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        if (!image.Registered || image.DepthOwner.IsValid || image.Binding.NeedsRebind)
        {
            throw SubmissionScheduler.Fatal($"A depth target must be found again before its view is acquired: address=0x{image.Description.Data.Address:X16} registered={image.Registered} proxy={image.DepthOwner.IsValid} rebind={image.Binding.NeedsRebind}.");
        }

        TouchImage(image);
        image.MarkGpuModified();
        image.Uses.DepthTarget = true;
        TraceVolumeState("depth-acquire", image, request);
        RefreshFromGuest(imageIdentifier, request);
        TraceMetadataBinding(request);
        if (request.Description.HasMetadata)
        {
            image.Description.Metadata = request.Description.Metadata;
            var address = request.Description.Metadata.Range.Address;
            IncludeMetadataWriteRange(address, request.Description.Metadata.Range.Size);
            if (!_surfaceMetadata.TryGetValue(address, out var metadata))
            {
                metadata = new SurfaceMetadata { Kind = SurfaceMetadataKind.HTile, ClearMask = image.Description.HtileClearMask };
                _surfaceMetadata.Add(address, metadata);
            }
            else if (metadata.Invalidated)
            {
                metadata = new SurfaceMetadata { Kind = SurfaceMetadataKind.HTile };
                _surfaceMetadata[address] = metadata;
            }
            else if (metadata.Kind == SurfaceMetadataKind.PendingDcc)
            {
                // A pending DCC fill uses the DCC encoding; it must not become HTile state.
                metadata.Kind = SurfaceMetadataKind.HTile;
                metadata.ClearMask = image.Description.HtileClearMask;
                metadata.FillValue = 0xffffffff;
                metadata.FillSize = 0;
            }
            else if (metadata.Kind != SurfaceMetadataKind.HTile)
            {
                throw SubmissionScheduler.Fatal($"A depth target reuses metadata that is not HTile: address=0x{address:X16} kind={metadata.Kind}.");
            }
            metadata.RangeSize = Math.Max(metadata.RangeSize, request.Description.Metadata.Range.Size);
            image.MetadataRegistration = metadata;
        }

        if (request.Description.HasStencil)
        {
            image.Description.Stencil = request.Description.Stencil;
            RefreshStencilPlane(imageIdentifier, image, request.Description.Metadata.StencilCompressed);
        }

        TakeGpuOwnership(image);
        return image.GetOrCreateView(request.View);
    }

    // GFX10 packs the mips smaller than half a swizzle block into one block at the start of the chain.
    // A title can write that block through a single-level view the size of the block (Unity's
    // screen-space reflection blur writes mips 3 and up that way), which the cache keeps as its own
    // image; copy each tail mip out of it before the chain is read.
    private void MergeMipTailBlock(CachedImage chain)
    {
        ref var description = ref chain.Description;
        if (description.Resources.Levels <= 1 || description.IsBlock || description.IsDepth || description.Samples > 1 ||
            description.Resources.Layers != 1 || description.IsVolume ||
            !Agc.GnmTiling.TryGetBlockElementDimensions((uint)description.TileMode, (int)description.BytesPerBlock, out var blockWidth, out var blockHeight))
        {
            return;
        }

        foreach (var candidateIdentifier in FindImagesInRange(description.Data.Address, 1, pageOverlap: false))
        {
            var block = _slots[candidateIdentifier];
            ref var blockDescription = ref block.Description;
            if (ReferenceEquals(block, chain) || blockDescription.Data.Address != description.Data.Address ||
                blockDescription.Resources.Levels != 1 || blockDescription.Resources.Layers != 1 ||
                blockDescription.Extent.Width != (uint)blockWidth || blockDescription.Extent.Height != (uint)blockHeight ||
                blockDescription.BytesPerBlock != description.BytesPerBlock || blockDescription.TileMode != description.TileMode ||
                !block.IsGpuModified || !block.Backing.Exists || block.GpuWriteSequence <= chain.MergedTailSequence)
            {
                continue;
            }

            if (!Agc.GnmTiling.TryGetMipChainPlacement(
                    (uint)description.TileMode,
                    (int)description.Extent.Width,
                    (int)description.Extent.Height,
                    (int)description.BytesPerBlock,
                    description.Resources.Levels,
                    out var placements,
                    out _))
            {
                return;
            }

            for (var mip = 0; mip < placements.Length && mip < chain.Backing.MipLevels; mip++)
            {
                var placement = placements[mip];
                if (!placement.InMipTail ||
                    placement.TailElementX + placement.ElementsWide > blockWidth ||
                    placement.TailElementY + placement.ElementsHigh > blockHeight)
                {
                    continue;
                }

                chain.CopyRegionFrom(
                    block,
                    (uint)placement.TailElementX,
                    (uint)placement.TailElementY,
                    (uint)mip,
                    (uint)placement.ElementsWide,
                    (uint)placement.ElementsHigh);
            }

            chain.MergedTailSequence = block.GpuWriteSequence;
            return;
        }
    }

    private void RefreshStencilPlane(ResourceSlotIdentifier depthIdentifier, CachedImage depth, bool stencilCompressed)
    {
        var association = AssociateStencilRange(depthIdentifier, depth.Description.Stencil);
        if (stencilCompressed || depth.Description.Samples != 1)
        {
            return;
        }

        RefreshFromGuest(association, RefreshRequest(_slots[association]));
    }

    public void MarkGpuWritten(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        RecordResourceHistory("gpu-image-written", image.Description.Data.Address, image.Description.Data.Size,
            image.Description.PixelFormat, image.Description.Extent);
        if (!image.Registered || image.DepthOwner.IsValid)
        {
            throw SubmissionScheduler.Fatal($"An image that is not available cannot be marked GPU-written: address=0x{image.Description.Data.Address:X16} registered={image.Registered} proxy={image.DepthOwner.IsValid}.");
        }

        WatchImage(imageIdentifier);
        TakeGpuOwnership(image);
        if (image.Description.HasStencil)
        {
            TakeStencilOwnership(imageIdentifier, image);
        }
    }

    private void TakeStencilOwnership(ResourceSlotIdentifier depthIdentifier, CachedImage depth)
    {
        var association = AssociateStencilRange(depthIdentifier, depth.Description.Stencil);
        WatchImage(association);
        TakeGpuOwnership(_slots[association]);
    }

    private static void TakeGpuOwnership(CachedImage image)
    {
        if (MeshDrawTrace.Enabled) MeshDrawTrace.Range("image-take-ownership", image.Description.Data.Address, image.Description.Data.Size, $"cpuDirty={image.IsCpuDirty} bufferDirty={image.IsBufferModified} gpuDirty={image.IsGpuModified}", remember: true);
        if (!image.DepthOwner.IsValid && !image.Backing.Exists)
        {
            throw SubmissionScheduler.Fatal($"GPU ownership needs a native image or a stencil association: address=0x{image.Description.Data.Address:X16}.");
        }

        image.ClearBufferModified();
        if (image.IsCpuDirty)
        {
            image.RefreshComplete();
        }

        image.MarkGpuModified();
    }

    private void ValidateRequest(in ImageRequest request)
    {
        request.Description.Validate();
        ref readonly var description = ref request.Description;
        var view = request.View;
        if (view.Format == Format.Undefined || view.LevelCount == 0 || view.LayerCount == 0 ||
            view.BaseLevel >= description.Resources.Levels || view.LevelCount > description.Resources.Levels - view.BaseLevel ||
            (!description.IsVolume && (view.BaseLayer >= description.Resources.Layers || view.LayerCount > description.Resources.Layers - view.BaseLayer)))
        {
            throw SubmissionScheduler.Fatal(
                $"The image view description is invalid: format={(int)view.Format} mip={view.BaseLevel}+{view.LevelCount} layer={view.BaseLayer}+{view.LayerCount} levels={description.Resources.Levels} layers={description.Resources.Layers}.");
        }

        if (request.Role == ImageRole.DepthTarget && !description.IsSupportedDepthTarget)
        {
            throw SubmissionScheduler.Fatal($"The depth image description is not supported: format={(int)description.PixelFormat} guestFormat={(uint)description.GuestFormat} bytesPerBlock={description.BytesPerBlock}.");
        }

        if (request.Role == ImageRole.DisplaySurface && !description.IsSupportedDisplayFormat)
        {
            throw SubmissionScheduler.Fatal($"The display surface description is not supported: format={(int)description.PixelFormat} guestFormat={(uint)description.GuestFormat} bytesPerBlock={description.BytesPerBlock} bgra16={description.Bgra16}.");
        }

        if (request.Role == ImageRole.DisplaySurface && description.Metadata.Compression == DisplayCompression.Unsupported)
        {
            throw SubmissionScheduler.Fatal($"The compressed display surface description is not supported: address=0x{description.Data.Address:X16} metadata=0x{description.Metadata.Range.Address:X16} control=0x{description.Metadata.Control:x}.");
        }
    }

    private ResourceSlotIdentifier GetNullImage(in ImageRequest request)
    {
        var format = request.Description.PixelFormat;
        if (_nullImages.TryGetValue(format, out var found))
        {
            return found;
        }

        var description = ImageDescription.Create();
        description.PixelFormat = request.Description.PixelFormat;
        description.GuestFormat = request.Description.GuestFormat;
        description.Type = GuestImageType.Color2D;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = SubresourceCount.Single;
        description.Pitch = 1;
        description.BytesPerBlock = Math.Max(request.Description.BytesPerBlock, 1);
        description.Samples = 1;
        description.TileMode = GuestTileMode.Linear;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = description.BytesPerBlock, Pitch = 1, Height = 1 };
        var imageIdentifier = InsertImage(description);
        _nullImages.Add(format, imageIdentifier);
        return imageIdentifier;
    }

    private ResourceSlotIdentifier InsertImage(in ImageDescription description,
        [System.Runtime.CompilerServices.CallerMemberName] string creationPath = "")
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageCreate);
        var imageIdentifier = _slots.Insert(new CachedImage(_device, _scheduler, _backing, description, _backingPool, _reportAllocationFailure));
        _createdImageCount++;
        ReportLargeImageCreation(_slots[imageIdentifier], creationPath);
        ReportImageLifetime(_slots[imageIdentifier], "recreate");
        if (RenderPhaseProfile.Enabled)
        {
            _imageCreationTotals.TryGetValue(creationPath, out var totals);
            _imageCreationTotals[creationPath] = (totals.Count + 1, totals.Bytes + _slots[imageIdentifier].Backing.AllocationSize);
        }
        TraceVolumeState("create", _slots[imageIdentifier], ImageRequest.Refresh(description, ImageRole.Texture));
        if (!ImageDescription.IsEmptyRange(description.Data))
        {
            AddToIndex(imageIdentifier);
        }

        return imageIdentifier;
    }
}

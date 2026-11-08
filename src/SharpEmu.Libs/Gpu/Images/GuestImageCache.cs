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
    private const ulong ParkedFramesBeforeRemoval = 8;
    private const int ParkedImageLimit = 64;
    private readonly List<ResourceSlotIdentifier> _parked = new();
    private const ulong MiB = 1024 * 1024;

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly SpinReadWriteLock _lock = new();
    private readonly PageGuard _pages;
    private readonly ColorToMultisampleDepthBlit _blit;
    private readonly GpuTiler _tiler;
    private readonly GuestBufferCache _bufferCache;
    private readonly IGuestBackedSpace _backing;
    private readonly SlotTable<CachedImage> _slots = new();
    private readonly ImageBackingPool? _backingPool;
    private readonly ImagePageOwnerTable _pageOwners = new();
    private readonly Dictionary<(Format Format, GuestImageType Type, uint Samples), ResourceSlotIdentifier> _nullImages = new();
    private RecencyQueue<ResourceSlotIdentifier> _recencyQueue = new();
    private readonly HashSet<ResourceSlotIdentifier> _scheduledReadbacks = new();
    private readonly ReadbackLandings _readbackLandings = new();
    private readonly SortedList<ulong, SurfaceMetadata> _surfaceMetadata = new();
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
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("image lookup");
        }

        _bufferCache.CompleteGuestPublications?.Invoke(request.Description.Data.Address, request.Description.Data.Size,
            request.Role != ImageRole.Texture ? Buffers.GuestBufferCache.PublicationAccess.Write : Buffers.GuestBufferCache.PublicationAccess.Read);
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

        var metadataBaseLayer = request.View.BaseLayer;
        var result = LookUpImage(ref request, exactFormat);
        if (_insertedOverGpuImages == result)
        {
            _insertedOverGpuImages = ResourceSlotIdentifier.Invalid;
            HandOverImagesToNewImage(result);
        }

        CompleteLookup(result, in request, metadataBaseLayer);
        return result;
    }

    public void CompleteLookup(ResourceSlotIdentifier result, in ImageRequest request, uint metadataBaseLayer)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("lookup completion");
        }

        if (request.Role is ImageRole.Texture or ImageRole.StorageImage)
        {
            ulong metadataAddress;
            ulong sliceSize;
            using (var held = _lock.Hold())
            {
                var image = _slots[result];
                if (request.Description.Metadata.Kind == MetadataKind.Dcc && request.Description.Metadata.Range.Size == 0 &&
                    image.Description.Metadata.Kind == MetadataKind.None)
                {
                    image.Description.Metadata = request.Description.Metadata;
                }

                ref readonly var description = ref image.Description;
                metadataAddress = description.Metadata.Range.Address;
                sliceSize = description.DccSliceSize;
            }

            if (sliceSize != 0)
            {
                SynchronizeGuestDccMetadata(metadataAddress, sliceSize, request.View.BaseLayer, request.View.LayerCount);
            }
        }

        MaterializeDccClear(result, request, metadataBaseLayer);
        if (request.Role == ImageRole.DisplaySurface && request.Description.Metadata.Compression != DisplayCompression.Uncompressed)
        {
            using var held = _lock.Hold();
            var image = _slots[result];
            var guestDirty = image.IsBufferModified || image.IsCpuDirty;
            var nativeCurrent = (image.Uses.RenderTarget || image.IsGpuModified) && !guestDirty;
            if (!nativeCurrent)
            {
                throw SubmissionScheduler.Fatal(
                    $"A compressed display surface can only be read from clean native GPU contents: address=0x{image.Description.Data.Address:X16} bufferModified={image.IsBufferModified} cpuDirty={image.IsCpuDirty} gpuModified={image.IsGpuModified}.");
            }
        }
    }

    private ResourceSlotIdentifier _insertedOverGpuImages = ResourceSlotIdentifier.Invalid;

    // A new image over memory that GPU-written images of another shape hold starts from their contents:
    // they move into the buffer first, and the new image is filled from it.
    private void HandOverImagesToNewImage(ResourceSlotIdentifier imageIdentifier)
    {
        GuestSpan range;
        using (var held = _lock.Hold())
        {
            range = _slots[imageIdentifier].Description.Data;
        }

        if (!PublishPartlyWrittenImages(range.Address, range.Size, wholeImages: true))
        {
            return;
        }

        using (var held = _lock.Hold())
        {
            _slots.TryGet(imageIdentifier)?.MarkBufferModified();
        }
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
            else if (request.View.Type == ImageViewType.Type3D && resolved.Backing.ImageType != ImageType.Type3D)
            {
                result = ResourceSlotIdentifier.Invalid;
                viewMip = -1;
                viewLayer = -1;
            }
            else if (viewMip < 0 && viewLayer < 0 && resolved.Description.IsBlock && !request.Description.IsBlock &&
                     ((request.Role == ImageRole.StorageImage && (resolved.Backing.Usage & ImageUsageFlags.StorageBit) == 0) ||
                      request.View.LevelCount > 1 || request.View.LayerCount > 1))
            {
                result = ReplaceCompressedForStorage(request.Description, result);
            }
        }

        if (RenderPhaseProfile.Enabled && result.IsValid)
        {
            if (exactMatch) _exactImageMatches++;
            else _overlapImageMatches++;
        }
        if (!result.IsValid && !TryUnparkImage(request.Description, out result))
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

            foreach (var candidate in candidates)
            {
                if (_slots.TryGet(candidate) is { IsGpuModified: true } overlapped &&
                    overlapped.Description.Data.Address == inserted.Description.Data.Address &&
                    (overlapped.Description.Extent.Width != inserted.Description.Extent.Width ||
                     overlapped.Description.Extent.Height != inserted.Description.Extent.Height ||
                     overlapped.Description.Pitch != inserted.Description.Pitch))
                {
                    _insertedOverGpuImages = result;
                    break;
                }
            }
        }

        var image = _slots[result];
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
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("image refresh");
        }

        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        TouchImage(image);
        RefreshFromGuest(imageIdentifier, RefreshRequest(image));
    }

    // The image behind a slot without touching its recency; false once the slot was erased.
    public bool TryGetImage(ResourceSlotIdentifier imageIdentifier, out CachedImage image)
    {
        using var held = _lock.Read();
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
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("texture view");
        }

        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        TouchImage(image);
        var hasData = !ImageDescription.IsEmptyRange(image.Description.Data);
        if (hasData && (!image.Registered || image.DepthOwner.IsValid || image.Binding.NeedsRebind))
        {
            throw SubmissionScheduler.Fatal($"A texture must be found again before its view is acquired: address=0x{image.Description.Data.Address:X16} registered={image.Registered} proxy={image.DepthOwner.IsValid} rebind={image.Binding.NeedsRebind}.");
        }

        AcquireTextureCore(imageIdentifier, image, in request);
        return image.GetOrCreateView(request.View);
    }

    private void AcquireTextureCore(ResourceSlotIdentifier imageIdentifier, CachedImage image, in ImageRequest request)
    {
        var hasData = !ImageDescription.IsEmptyRange(image.Description.Data);
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
    }

    public ImageView AcquireColorTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("color target view");
        }

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
        AcquireColorTargetCore(imageIdentifier, image, in request);
        return image.GetOrCreateView(request.View);
    }

    private void AcquireColorTargetCore(ResourceSlotIdentifier imageIdentifier, CachedImage image, in ImageRequest request)
    {
        image.MarkGpuModified();
        image.Uses.RenderTarget = true;
        TraceVolumeState("color-acquire", image, request);
        RefreshFromGuest(imageIdentifier, request);
        TraceMetadataBinding(request);
        if (request.Description.Metadata.Kind == MetadataKind.Dcc && request.Description.Metadata.Range.Size == 0)
        {
            image.Description.Metadata = request.Description.Metadata;
            var address = request.Description.Metadata.Range.Address;
            IncludeMetadataWriteRange(address, Math.Max(request.Description.Metadata.Range.Size,
                request.Description.DccSliceSize * request.Description.TransferLayers));
            if (!_surfaceMetadata.TryGetValue(address, out var metadata) || metadata.Invalidated)
            {
                metadata = new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc };
                _surfaceMetadata[address] = metadata;
                NoteMetadataChange(address);
            }
            else if (metadata.Kind == SurfaceMetadataKind.PendingDcc)
            {
                metadata.Kind = SurfaceMetadataKind.Dcc;
                NoteMetadataChange(address);
            }
            else if (metadata.Kind != SurfaceMetadataKind.Dcc)
            {
                metadata = ReplaceSurfaceMetadata(address, metadata, new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc });
            }

            metadata.Size = Math.Max(metadata.Size, request.Description.DccSliceSize * request.Description.TransferLayers);
            metadata.RangeSize = Math.Max(metadata.RangeSize, request.Description.Metadata.Range.Size);
            metadata.NativeColorClear = request.Description.Metadata.NativeColorClear;
            metadata.DccSliceCount = request.Description.TransferLayers;
            image.MetadataRegistration = metadata;
        }

        TakeGpuOwnership(image);
        ScheduleReadback(imageIdentifier, image);
    }

    public ImageView AcquireDepthTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("depth target view");
        }

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
        AcquireDepthTargetCore(imageIdentifier, image, in request);
        return image.GetOrCreateView(request.View);
    }

    private void AcquireDepthTargetCore(ResourceSlotIdentifier imageIdentifier, CachedImage image, in ImageRequest request)
    {
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
                NoteMetadataChange(address);
            }
            else if (metadata.Invalidated && metadata.Kind == SurfaceMetadataKind.HTile)
            {
                metadata = new SurfaceMetadata { Kind = SurfaceMetadataKind.HTile };
                _surfaceMetadata[address] = metadata;
                NoteMetadataChange(address);
            }
            else if (metadata.Kind == SurfaceMetadataKind.PendingDcc)
            {
                // A pending DCC fill uses the DCC encoding; known or not, it must not become HTile state.
                metadata.Invalidated = false;
                metadata.Kind = SurfaceMetadataKind.HTile;
                metadata.ClearMask = image.Description.HtileClearMask;
                metadata.FillValue = 0xffffffff;
                metadata.FillSize = 0;
                NoteMetadataChange(address);
            }
            else if (metadata.Kind != SurfaceMetadataKind.HTile)
            {
                metadata = ReplaceSurfaceMetadata(address, metadata,
                    new SurfaceMetadata { Kind = SurfaceMetadataKind.HTile, ClearMask = image.Description.HtileClearMask });
            }
            metadata.RangeSize = Math.Max(metadata.RangeSize, request.Description.Metadata.Range.Size);
            image.MetadataRegistration = metadata;
        }

        if (request.Description.HasStencil)
        {
            image.Description.Stencil = StencilPlaneOfImage(image.Description, request.Description);
            RefreshStencilPlane(imageIdentifier, image, request.Description.Metadata.StencilCompressed);
            TakeStencilOwnership(imageIdentifier, image);
        }

        TakeGpuOwnership(image);
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

    // A view of the first slices of a layered target names the stencil plane of those layers only. The image keeps
    // its other layers, so its stencil association covers the plane of each of them, as its depth range does.
    private static GuestSpan StencilPlaneOfImage(in ImageDescription image, in ImageDescription request)
    {
        var requestLayers = request.Resources.Layers;
        var imageLayers = image.Resources.Layers;
        if (requestLayers == 0 || imageLayers <= requestLayers || request.Stencil.Size % requestLayers != 0)
        {
            return request.Stencil;
        }

        return new GuestSpan(request.Stencil.Address, request.Stencil.Size / requestLayers * imageLayers);
    }

    private void RefreshStencilPlane(ResourceSlotIdentifier depthIdentifier, CachedImage depth, bool stencilCompressed)
    {
        var association = AssociateStencilRange(depthIdentifier, depth.Description.Stencil, restoreNativeStencil: true);
        if (stencilCompressed || depth.Description.Samples != 1)
        {
            return;
        }

        RefreshFromGuest(association, RefreshRequest(_slots[association]));
    }

    public void MarkGpuWritten(ResourceSlotIdentifier imageIdentifier)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("gpu written");
        }

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
        // The placeholder has the type and sample count of the binding: a 1D or 3D view needs an image of that
        // type, and a multisampled binding a multisampled image.
        var type = request.Description.Type;
        var samples = Math.Max(request.Description.Samples, 1u);
        var key = (request.Description.PixelFormat, type, samples);
        if (_nullImages.TryGetValue(key, out var found))
        {
            return found;
        }

        var description = ImageDescription.Create();
        description.PixelFormat = request.Description.PixelFormat;
        description.GuestFormat = request.Description.GuestFormat;
        description.Type = type;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = SubresourceCount.Single;
        description.Pitch = 1;
        description.BytesPerBlock = Math.Max(request.Description.BytesPerBlock, 1);
        description.Samples = samples;
        description.TileMode = GuestTileMode.Linear;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = description.BytesPerBlock, Pitch = 1, Height = 1 };
        var imageIdentifier = InsertImage(description);
        _nullImages.Add(key, imageIdentifier);
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

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
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
    private const ulong GiB = 1024 * MiB;
    private const ulong MinimumMemorySafetyMargin = 512 * MiB;
    private const ulong NoBudgetImageCacheLimit = 4 * GiB;

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
    private readonly OptimalImageMemoryPool _imageMemoryPool;
    private readonly ImagePageOwnerTable _pageOwners = new();
    private readonly Dictionary<NullImageKey, ResourceSlotIdentifier> _nullImages = new();
    private RecencyQueue<ResourceSlotIdentifier> _recencyQueue = new();
    private readonly HashSet<ResourceSlotIdentifier> _scheduledReadbacks = new();
    private readonly SortedDictionary<ulong, SurfaceMetadata> _surfaceMetadata = new();
    private ulong _totalUsedMemory;
    private long _retiredImageMemoryBytes;
    private ulong _collectionStartBytes;
    private ulong _memoryPressureBytes;
    private ulong _criticalMemoryBytes;
    private ulong _collectionTick;
    private bool _allocationCollectionBlocked;
    private bool _collectionThresholdsOverridden;
    private bool _collectionBudgetLogged;
    private uint _queryEpoch;
    private bool _readbackLinearImages;
    private bool _disposed;

    // The persistent Vulkan image heap must invalidate views before this cache
    // destroys an evicted image. The callback is installed only when the device
    // supports null descriptors and bindless images are active.
    public Action<IReadOnlyList<ImageView>>? BindlessImageInvalidator { get; set; }

    public GuestImageCache(GpuDeviceInfo device, SubmissionScheduler scheduler, PageGuard pages, GuestBufferCache bufferCache, IGuestBackedSpace backing, bool readbackLinearImages)
    {
        _device = device;
        _scheduler = scheduler;
        _pages = pages;
        _bufferCache = bufferCache;
        _backing = backing;
        _readbackLinearImages = readbackLinearImages;
        RefreshCollectionBudget();
        _blit = new ColorToMultisampleDepthBlit(device, scheduler);
        _tiler = new GpuTiler(device, scheduler, bufferCache.GetUtilityBuffer(GpuBufferUsage.Stream));
        _backingPool = ImageBackingPool.Enabled ? new ImageBackingPool(device) : null;
        _imageMemoryPool = new OptimalImageMemoryPool(device);
    }

    // Set once, from the VRAM free when the device starts: recomputed later, the budget would
    // count the cache's own images as used memory and shrink as the cache fills.
    private void RefreshCollectionBudget()
    {
        if (_collectionThresholdsOverridden)
        {
            return;
        }

        var available = Math.Max(_device.DeviceLocalAvailableBytes, 1UL);
        var imageBudget = ComputeImageCacheBudget(available, _device.HasMemoryBudget);
        if (ImageCacheBudgetCap is { } cap)
        {
            imageBudget = Math.Min(imageBudget, cap);
        }

        _collectionStartBytes = Math.Max(imageBudget / 2, MiB);
        _memoryPressureBytes = Math.Max(imageBudget * 3 / 5, MiB);
        _criticalMemoryBytes = Math.Max(imageBudget * 7 / 10, MiB);
        if (!_collectionBudgetLogged)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Image cache budget source={(_device.HasMemoryBudget ? "VK_EXT_memory_budget" : "conservative heap fallback")} " +
                $"heap={_device.DeviceLocalHeapBytes} budget={_device.DeviceLocalBudgetBytes} " +
                $"usage={_device.DeviceLocalUsageBytes} available={_device.DeviceLocalAvailableBytes} " +
                $"image_budget={imageBudget} thresholds={_collectionStartBytes}/{_memoryPressureBytes}/{_criticalMemoryBytes}");
            _collectionBudgetLogged = true;
        }
    }

    // SHARPEMU_IMAGE_CACHE_BUDGET_MB caps the image cache below its VRAM-derived budget, which
    // otherwise keeps most free VRAM for cached images. A lower cap leaves room for other
    // VRAM users, such as a frame-capture tool's copies of every resource.
    private static readonly ulong? ImageCacheBudgetCap =
        ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_CACHE_BUDGET_MB"), out var megabytes) && megabytes > 0
            ? megabytes * MiB
            : null;

    internal static ulong ComputeImageCacheBudget(ulong available, bool hasMemoryBudget)
    {
        available = Math.Max(available, 1UL);
        var safetyMargin = Math.Max(available / 4, MinimumMemorySafetyMargin);
        var imageBudget = available > safetyMargin
            ? available - safetyMargin
            : Math.Max(available / 2, MiB);
        if (!hasMemoryBudget)
        {
            // Without VK_EXT_memory_budget the physical heap size says nothing
            // about allocations already held by the driver or other processes.
            // Keep the historical conservative cap instead of treating all VRAM
            // as available to the image cache.
            imageBudget = Math.Min(imageBudget, NoBudgetImageCacheLimit);
        }

        return imageBudget;
    }

    public ulong TotalUsedMemory => _totalUsedMemory + (ulong)Interlocked.Read(ref _retiredImageMemoryBytes);

    public bool MemoryUnderPressure => CollectionMemoryBytes >= _memoryPressureBytes;

    public bool RetirementOverBudget => Interlocked.Read(ref _retiredImageMemoryBytes) > 256L * 1024 * 1024 ||
        (Interlocked.Read(ref _retiredImageMemoryBytes) > 0 && CollectionMemoryBytes >= _criticalMemoryBytes);

    public bool ScratchOverBudget => _tiler.ScratchOverBudget;

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
        _slots.ForEach((_, image) => image.Dispose());
        _backingPool?.Dispose();
        _imageMemoryPool.ReleaseRetained();
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

        var metadataBaseLayer = request.View.BaseLayer;
        var result = LookUpImage(ref request, exactFormat);

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

        return result;
    }

    private ResourceSlotIdentifier LookUpImage(ref ImageRequest request, bool exactFormat)
    {
        using var held = _lock.Hold();
        if (TryReuseLookup(ref request, exactFormat, out var reused))
        {
            return reused;
        }

        var original = request;
        var generation = _lookupGeneration;
        var result = ResourceSlotIdentifier.Invalid;
        var candidates = FindImagesInRange(request.Description.Data.Address, request.Description.Data.Size, pageOverlap: false);
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

        // A compute shader indexes a storage image in guest pixels and its dispatch grid does
        // not shrink with the host resolution, so a scaled image would be written with holes
        // (upscale) or redundantly (downscale). Such memory drops back to guest resolution.
        if (result.IsValid && request.ShaderWrite && _slots[result].IsScaled)
        {
            result = ReplaceScaled(result);
        }

        if (!result.IsValid)
        {
            result = InsertImage(request.Description);
            var inserted = _slots[result];
            if (_bufferCache.HasGpuDirtyBytes(inserted.Description.Data.Address, inserted.Description.Data.Size))
            {
                inserted.MarkBufferModified();
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

    public CachedImage AcquireStencilStorageImage(ResourceSlotIdentifier imageIdentifier, uint width, uint height)
    {
        using var held = _lock.Hold();
        var attachment = _slots[imageIdentifier];
        TouchImage(attachment);
        CollectForAllocation((ulong)width * height * attachment.Backing.Layers);
        var before = attachment.AccountedSize;
        var storage = attachment.GetOrCreateStencilStorageImage(width, height);
        _totalUsedMemory += attachment.AccountedSize - before;
        return storage;
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

        if (hasData)
        {
            SynchronizeAliases(imageIdentifier);
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

        return image.GetOrCreateView(request.View);
    }

    public ImageView AcquireColorTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
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
        SynchronizeAliases(imageIdentifier);
        image.MarkGpuModified();
        image.Uses.RenderTarget = true;
        RefreshFromGuest(imageIdentifier, request);
        if (request.Description.Metadata.Kind == MetadataKind.Dcc && request.Description.Metadata.Range.Size == 0)
        {
            image.Description.Metadata = request.Description.Metadata;
            var address = request.Description.Metadata.Range.Address;
            if (!_surfaceMetadata.TryGetValue(address, out var metadata))
            {
                metadata = new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc };
                _surfaceMetadata.Add(address, metadata);
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
        }

        TakeGpuOwnership(image);
        ScheduleReadback(imageIdentifier, image);
        return image.GetOrCreateView(request.View);
    }

    public ImageView AcquireDepthTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
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
        SynchronizeAliases(imageIdentifier);
        image.MarkGpuModified();
        image.Uses.DepthTarget = true;
        RefreshFromGuest(imageIdentifier, request);
        if (request.Description.HasMetadata)
        {
            image.Description.Metadata = request.Description.Metadata;
            var address = request.Description.Metadata.Range.Address;
            if (!_surfaceMetadata.TryGetValue(address, out var metadata))
            {
                _surfaceMetadata.Add(address, CreateMetadata(SurfaceMetadataKind.HTile, image.Description.HtileClearMask));
            }
            else if (metadata.Kind == SurfaceMetadataKind.PendingDcc)
            {
                // A pending DCC fill uses the DCC encoding; it must not become HTile state.
                metadata.Kind = SurfaceMetadataKind.HTile;
                metadata.SetFromMask(image.Description.HtileClearMask);
                metadata.FillValue = 0xffffffff;
                metadata.FillSize = 0;
            }
            else if (metadata.Kind != SurfaceMetadataKind.HTile)
            {
                throw SubmissionScheduler.Fatal($"A depth target reuses metadata that is not HTile: address=0x{address:X16} kind={metadata.Kind}.");
            }
        }

        if (request.Description.HasStencil)
        {
            if (image.Description.HasStencil && image.Description.Stencil != request.Description.Stencil)
            {
                ReleaseStencilPlane(imageIdentifier, image);
            }

            image.Description.Stencil = request.Description.Stencil;
            RefreshStencilPlane(imageIdentifier, image, request.Description.Metadata.StencilCompressed);
        }

        TakeGpuOwnership(image);
        return image.GetOrCreateView(request.View);
    }

    // The depth image holds one guest stencil plane at a time. Before it takes another one (a
    // title that alternates stencil buffers under one depth buffer), the plane it holds goes
    // back to guest memory when the GPU changed it, and its association is dropped, so a view
    // of that plane reads guest memory instead of the depth image's new plane.
    private void ReleaseStencilPlane(ResourceSlotIdentifier depthIdentifier, CachedImage depth)
    {
        var plane = depth.Description.Stencil;
        var associations = new List<ResourceSlotIdentifier>();
        foreach (var imageIdentifier in FindImagesInRange(plane.Address, plane.Size, pageOverlap: false))
        {
            if (_slots.TryGet(imageIdentifier) is { } candidate && candidate.DepthOwner == depthIdentifier)
            {
                associations.Add(imageIdentifier);
            }
        }

        if (associations.Any(association => _slots[association].IsGpuModified))
        {
            WriteBackStencilPlane(depth, plane);
        }

        foreach (var association in associations)
        {
            _slots[association].ClearGpuModified();
            DeleteImage(association);
        }
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

    private readonly record struct NullImageKey(Format Format, GuestPixelFormat GuestFormat, GuestImageType Type, uint Samples, uint Layers);

    private ResourceSlotIdentifier GetNullImage(in ImageRequest request)
    {
        var key = new NullImageKey(
            request.Description.PixelFormat,
            request.Description.GuestFormat,
            request.Description.Type,
            request.Description.Samples,
            request.Description.Resources.Layers);
        if (_nullImages.TryGetValue(key, out var found))
        {
            return found;
        }

        var description = ImageDescription.Create();
        description.PixelFormat = request.Description.PixelFormat;
        description.GuestFormat = request.Description.GuestFormat;
        description.Type = request.Description.Type;
        description.Extent = new Extent3D(1, 1, 1);
        description.Resources = new SubresourceCount(1, key.Layers);
        description.Pitch = 1;
        description.BytesPerBlock = Math.Max(request.Description.BytesPerBlock, 1);
        description.Samples = Math.Max(request.Description.Samples, 1);
        description.TileMode = GuestTileMode.Linear;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = description.BytesPerBlock, Pitch = 1, Height = 1 };
        var imageIdentifier = InsertImage(description);
        _nullImages.Add(key, imageIdentifier);
        return imageIdentifier;
    }

    // Guest ranges a compute shader has written through a storage image; see the storage
    // branch of FindImage. Every image over such a range keeps its guest resolution.
    private readonly List<GuestSpan> _unscaledRanges = new();

    private bool IsUnscaledRange(in GuestSpan data)
    {
        if (ImageDescription.IsEmptyRange(data))
        {
            return false;
        }

        foreach (var range in _unscaledRanges)
        {
            if (GuestRangeOverlap.Bytes(range, data))
            {
                return true;
            }
        }

        return false;
    }

    // Drops one image back to guest resolution for good; later images over the same guest
    // memory are created unscaled too, so the decision cannot oscillate.
    public void DemoteRenderScale(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (_slots[imageIdentifier].IsScaled)
        {
            ReplaceScaled(imageIdentifier);
        }
    }

    private ResourceSlotIdentifier ReplaceScaled(ResourceSlotIdentifier cachedImageIdentifier)
    {
        var cached = _slots[cachedImageIdentifier];
        _unscaledRanges.Add(cached.Description.Data);
        var replacementImageIdentifier = InsertImage(cached.Description);
        var replacement = _slots[replacementImageIdentifier];
        replacement.Uses = cached.Uses;
        if (cached.Binding.IsBound || cached.Binding.IsTarget)
        {
            cached.Binding.NeedsRebind = true;
        }

        CopyWholeImage(replacementImageIdentifier, cachedImageIdentifier);
        ReleaseImage(cachedImageIdentifier);
        return replacementImageIdentifier;
    }

    private ResourceSlotIdentifier InsertImage(in ImageDescription description)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageCreate);
        var requiredBytes = (description.Data.Size + 1023) & ~1023UL;
        CollectForAllocation(requiredBytes);
        var allowScaling = !IsUnscaledRange(description.Data);
        CachedImage image;
        try
        {
            image = new CachedImage(_device, _scheduler, _backing, description, _backingPool, _imageMemoryPool, DescribeImageMemory, allowScaling);
        }
        catch (CachedImage.ImageOutOfMemoryException)
        {
            // The budget is an estimate: the device can still run out. Free everything that can
            // go, wait for retired images to be destroyed, and try once more.
            ReclaimAfterFailedAllocation();
            try
            {
                image = new CachedImage(_device, _scheduler, _backing, description, _backingPool, _imageMemoryPool, DescribeImageMemory, allowScaling);
            }
            catch (CachedImage.ImageOutOfMemoryException again)
            {
                throw SubmissionScheduler.Fatal(again.Message);
            }
        }

        var imageIdentifier = _slots.Insert(image);
        if (!ImageDescription.IsEmptyRange(description.Data))
        {
            AddToIndex(imageIdentifier);
        }

        return imageIdentifier;
    }
}

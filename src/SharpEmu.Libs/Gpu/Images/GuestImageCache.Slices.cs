// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public readonly record struct ImageSliceUse(ResourceSlotIdentifier Image, CachedImage Expected, ImageRequest Request);

// Lookups and view acquisitions that only read the cache, for recording threads that run ahead of
// the stream order; the mutations they skip are applied in stream order by ApplyUse.
public sealed partial class GuestImageCache
{
    private enum SliceLookupFailure
    {
        Display,
        TargetDccSync,
        NoCandidate,
        Format,
        Resources,
        ViewType,
        Unregistered,
        TextureMetadata,
        TextureDccSync,
        DccClear,
        NullImage,
        DepthRecreate,
        Grow,
        Park,
        Unresolved,
        ChangedAtJoin,
        MetadataChangedAtJoin,
        DepthUnregistered,
        DepthOwner,
        DepthRebind,
        DepthMetadataAddress,
        DepthMetadataSize,
        DepthMetadataKind,
        DepthMetadataCompression,
        DepthMetadataUnregistered,
        DepthStencil,
        DepthStencilAssociation,
    }

    private static readonly string[] SliceLookupFailureNames =
    [
        "display", "target_dcc_sync", "no_candidate", "format", "resources", "view_type", "unregistered", "texture_metadata", "texture_dcc_sync", "dcc_clear",
        "null_image", "depth_recreate", "grow", "park", "unresolved", "changed_at_join", "metadata_changed_at_join",
        "depth_unregistered", "depth_owner", "depth_rebind", "depth_metadata_address", "depth_metadata_size", "depth_metadata_kind", "depth_metadata_compression", "depth_metadata_unregistered", "depth_stencil", "depth_stencil_association",
    ];
    private static readonly string[] SliceLookupRoleNames = ["texture", "storage", "color", "depth", "display"];
    private static readonly long[] SliceLookupFailures = new long[SliceLookupFailureNames.Length];
    private static readonly long[] SliceLookupRoleFailures = new long[SliceLookupRoleNames.Length];

    private static bool SliceLookupFailed(SliceLookupFailure reason, ImageRole role)
    {
        Interlocked.Increment(ref SliceLookupFailures[(int)reason]);
        Interlocked.Increment(ref SliceLookupRoleFailures[(int)role]);
        return false;
    }

    private static long _parks;
    private static long _parkReleases;
    private static long _unparks;
    private static long _parkExpiries;

    public static string TakeSliceLookupReport() =>
        "lookup_fail=" + TakeCounts(SliceLookupFailures, SliceLookupFailureNames) + " lookup_fail_role=" + TakeCounts(SliceLookupRoleFailures, SliceLookupRoleNames) +
        FormattableString.Invariant($" parks={Interlocked.Exchange(ref _parks, 0)} park_releases={Interlocked.Exchange(ref _parkReleases, 0)} unparks={Interlocked.Exchange(ref _unparks, 0)} park_expiries={Interlocked.Exchange(ref _parkExpiries, 0)}");

    private static string TakeCounts(long[] counters, string[] names)
    {
        var parts = new List<string>();
        for (var index = 0; index < counters.Length; index++)
        {
            var count = Interlocked.Exchange(ref counters[index], 0);
            if (count != 0)
            {
                parts.Add(names[index] + ":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return string.Join(",", parts);
    }

    public CachedImage? PeekImage(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Read();
        return _slots.TryGet(imageIdentifier);
    }

    public bool TryFindImageForSlice(ref ImageRequest request, bool exactFormat, out ResourceSlotIdentifier result)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageLookup);
        result = ResourceSlotIdentifier.Invalid;
        ValidateRequest(request);
        using var held = _lock.Read();
        if (ImageDescription.IsEmptyRange(request.Description.Data))
        {
            return _nullImages.TryGetValue((request.Description.PixelFormat, request.Description.Type, Math.Max(request.Description.Samples, 1u)), out result) ||
                SliceLookupFailed(SliceLookupFailure.NullImage, request.Role);
        }

        if (request.Role == ImageRole.DisplaySurface)
        {
            return SliceLookupFailed(SliceLookupFailure.Display, request.Role);
        }

        if (request.Role == ImageRole.ColorTarget && request.Description.DccSliceSize is var targetSliceSize and not 0 &&
            GuestDccMetadataNeedsSync(request.Description.Metadata.Range.Address, targetSliceSize, request.View.BaseLayer, request.View.LayerCount))
        {
            return SliceLookupFailed(SliceLookupFailure.TargetDccSync, request.Role);
        }

        if (!TryReuseLookupForSlice(ref request, exactFormat, out result))
        {
            var candidates = CollectImagesInRange(request.Description.Data.Address, request.Description.Data.Size);
            foreach (var candidate in candidates)
            {
                if (HasSameBacking(_slots[candidate].Description, request.Description, exactFormat))
                {
                    result = candidate;
                }
            }

            var viewMip = -1;
            var viewLayer = -1;
            if (!result.IsValid && !TryResolveOverlapsForSlice(request.Description, request.Role, candidates, out result, out viewMip, out viewLayer, out var failure))
            {
                return SliceLookupFailed(failure, request.Role);
            }

            var resolved = _slots[result];
            if (exactFormat && resolved.Description.PixelFormat != request.Description.PixelFormat)
            {
                result = ResourceSlotIdentifier.Invalid;
                return SliceLookupFailed(SliceLookupFailure.Format, request.Role);
            }

            if (resolved.Description.Resources < request.Description.Resources)
            {
                result = ResourceSlotIdentifier.Invalid;
                return SliceLookupFailed(SliceLookupFailure.Resources, request.Role);
            }

            if (!resolved.SupportsViewType(request.View) ||
                (request.Role == ImageRole.StorageImage && resolved.Description.IsBlock && !request.Description.IsBlock &&
                 (resolved.Backing.Usage & ImageUsageFlags.StorageBit) == 0))
            {
                result = ResourceSlotIdentifier.Invalid;
                return SliceLookupFailed(SliceLookupFailure.ViewType, request.Role);
            }

            if (viewMip >= 0)
            {
                request.View = request.View with { BaseLevel = (uint)viewMip };
            }

            if (viewLayer >= 0)
            {
                request.View = request.View with { BaseLayer = (uint)viewLayer };
            }
        }

        var image = _slots[result];
        if (!image.Registered || image.Parked)
        {
            result = ResourceSlotIdentifier.Invalid;
            return SliceLookupFailed(SliceLookupFailure.Unregistered, request.Role);
        }

        if (request.Role is ImageRole.Texture or ImageRole.StorageImage)
        {
            if (request.Description.Metadata.Kind == MetadataKind.Dcc && request.Description.Metadata.Range.Size == 0 &&
                image.Description.Metadata.Kind == MetadataKind.None)
            {
                return SliceLookupFailed(SliceLookupFailure.TextureMetadata, request.Role);
            }

            var sliceSize = image.Description.DccSliceSize;
            if (sliceSize != 0 && GuestDccMetadataNeedsSync(image.Description.Metadata.Range.Address, sliceSize, request.View.BaseLayer, request.View.LayerCount))
            {
                return SliceLookupFailed(SliceLookupFailure.TextureDccSync, request.Role);
            }
        }

        return DccClearMaterializationIsNoop(image, in request) || SliceLookupFailed(SliceLookupFailure.DccClear, request.Role);
    }

    // The serial overlap loop restricted to the outcomes that leave the cache unchanged; false names
    // the mutation the render thread has to perform instead.
    private bool TryResolveOverlapsForSlice(in ImageDescription requested, ImageRole role, List<ResourceSlotIdentifier> candidates,
        out ResourceSlotIdentifier result, out int mip, out int layer, out SliceLookupFailure failure)
    {
        result = ResourceSlotIdentifier.Invalid;
        mip = -1;
        layer = -1;
        foreach (var candidate in candidates)
        {
            mip = -1;
            layer = -1;
            var merged = result.IsValid ? _slots[result].Description : requested;
            if (!TryResolveOverlapForSlice(merged, role, candidate, result, out var resolved, out var resolvedMip, out var resolvedLayer, out failure))
            {
                return false;
            }

            if (resolved.IsValid)
            {
                result = resolved;
                mip = resolvedMip;
                layer = resolvedLayer;
            }
        }

        failure = candidates.Count == 0 ? SliceLookupFailure.NoCandidate : SliceLookupFailure.Unresolved;
        return result.IsValid;
    }

    private bool TryResolveOverlapForSlice(in ImageDescription requested, ImageRole role, ResourceSlotIdentifier candidate, ResourceSlotIdentifier merged,
        out ResourceSlotIdentifier resolved, out int mip, out int layer, out SliceLookupFailure failure)
    {
        resolved = merged;
        mip = -1;
        layer = -1;
        failure = SliceLookupFailure.Unresolved;
        if (_slots.TryGet(candidate) is not { } cached)
        {
            return true;
        }

        ref readonly var cachedInfo = ref cached.Description;
        var currentTick = _scheduler.CurrentTick;
        var safeToDelete = currentTick - Math.Min(currentTick, cached.LastAccessTick) > TicksBeforeRemoval;
        if (requested.Data.Address == cachedInfo.Data.Address &&
            requested.BlockExtent.Width == cachedInfo.BlockExtent.Width && requested.BlockExtent.Height == cachedInfo.BlockExtent.Height &&
            requested.BytesPerBlock * requested.Samples == cachedInfo.BytesPerBlock * cachedInfo.Samples)
        {
            if (cachedInfo.IsDepth || requested.IsDepth)
            {
                if (DepthOverlapRecreates(requested, role, cached))
                {
                    failure = SliceLookupFailure.DepthRecreate;
                    return false;
                }

                resolved = candidate;
                return true;
            }

            if ((requested.IsBlock && !cachedInfo.IsBlock) || (requested.Data.Size == cachedInfo.Data.Size && (requested.IsVolume || cachedInfo.IsVolume)))
            {
                failure = SliceLookupFailure.Grow;
                return false;
            }

            if (requested.TileMode != cachedInfo.TileMode || (requested.Resources == cachedInfo.Resources && !SameMipLayout(requested, cachedInfo)))
            {
                if (safeToDelete)
                {
                    failure = SliceLookupFailure.Park;
                    return false;
                }

                return true;
            }

            if (requested.Data.Size == cachedInfo.Data.Size && requested.Resources == cachedInfo.Resources && requested.Type == cachedInfo.Type &&
                requested.Extent.Width > cachedInfo.Extent.Width && requested.Extent.Height >= cachedInfo.Extent.Height &&
                requested.Extent.Depth >= cachedInfo.Extent.Depth && ViewFormatRules.AreCompatible(cachedInfo.PixelFormat, requested.PixelFormat))
            {
                failure = SliceLookupFailure.Grow;
                return false;
            }

            if (requested.PixelFormat != cachedInfo.PixelFormat || requested.Data.Size <= cachedInfo.Data.Size)
            {
                var viewed = merged.IsValid ? merged : candidate;
                resolved = _slots.TryGet(viewed) is { } image && ViewFormatRules.AreCompatible(image.Description.PixelFormat, requested.PixelFormat)
                    ? viewed
                    : ResourceSlotIdentifier.Invalid;
                return true;
            }

            failure = SliceLookupFailure.Grow;
            return false;
        }

        var requestedMip = requested.FindMatchingMipLevel(cachedInfo);
        if (requestedMip >= 0)
        {
            mip = requestedMip;
            layer = requested.FindMatchingArraySlice(cachedInfo, requestedMip);
            resolved = candidate;
            return true;
        }

        if (cachedInfo.FindMatchingMipLevel(requested) >= 0)
        {
            failure = SliceLookupFailure.Grow;
            return false;
        }

        if (requested.Data.Address >= cachedInfo.Data.Address && safeToDelete)
        {
            failure = SliceLookupFailure.Park;
            return false;
        }

        return true;
    }

    private bool TryReuseLookupForSlice(ref ImageRequest request, bool exactFormat, out ResourceSlotIdentifier result)
    {
        result = ResourceSlotIdentifier.Invalid;
        if (!LookupMemoEnabled || _lookups is not { } lookups)
        {
            return false;
        }

        ref var memo = ref lookups[LookupSlot(request, exactFormat)];
        if (memo.ExactFormat != exactFormat || !SameRequest(memo.Request, request) || !LookupUnchangedSince(memo.Generation, memo.Begin, memo.End) ||
            _slots.TryGet(memo.Result) is not { Registered: true, Parked: false })
        {
            return false;
        }

        memo.Generation = _lookupGeneration;
        request.View = memo.View;
        result = memo.Result;
        return true;
    }

    private List<ResourceSlotIdentifier> CollectImagesInRange(ulong address, ulong size, bool includeParked = false)
    {
        var result = new List<ResourceSlotIdentifier>();
        if (!ImagePageOwnerTable.TryGetPageRange(address, size, out var first, out var lastExclusive))
        {
            return result;
        }

        for (var page = first; page < lastExclusive; page++)
        {
            var owners = _pageOwners.Find(page);
            if (owners == null)
            {
                continue;
            }

            for (var ownerIndex = 0; ownerIndex < owners.Count; ownerIndex++)
            {
                var imageIdentifier = owners[ownerIndex];
                var image = _slots.TryGet(imageIdentifier);
                if (image == null || (image.Parked && !includeParked) || result.Contains(imageIdentifier) || !image.Overlaps(address, size))
                {
                    continue;
                }

                result.Add(imageIdentifier);
            }
        }

        return result;
    }

    private bool GuestDccMetadataNeedsSync(ulong metadataAddress, ulong sliceSize, uint baseLayer, uint layerCount)
    {
        if (metadataAddress == 0 || sliceSize == 0 || layerCount == 0 ||
            (ulong)baseLayer + layerCount > (ulong.MaxValue - metadataAddress) / sliceSize)
        {
            return false;
        }

        var address = metadataAddress + (ulong)baseLayer * sliceSize;
        var size = (ulong)layerCount * sliceSize;
        return IsValidRange(address, size) && _bufferCache.HasGpuDirtyBytes(address, size);
    }

    private bool DccClearMaterializationIsNoop(CachedImage image, in ImageRequest request)
    {
        var description = request.Description;
        if (description.Metadata.Kind != MetadataKind.Dcc)
        {
            return true;
        }

        if (!image.Description.Metadata.Equals(description.Metadata))
        {
            return false;
        }

        var range = description.Metadata.Range;
        if (range.Size == 0 || description.Resources.Levels != 1 || image.Description.Resources.Levels != 1)
        {
            return true;
        }

        var layers = description.TransferLayers;
        if (!ImageDescription.IsValidRange(range) || layers == 0 || range.Size % layers != 0)
        {
            return false;
        }

        var view = request.View;
        var volume = description.IsVolume && view.Type == ImageViewType.Type3D;
        var first = volume ? 0u : view.BaseLayer;
        var count = volume ? description.Extent.Depth : view.LayerCount;
        if (first >= layers || count == 0 || count > layers - first)
        {
            return false;
        }

        var sliceSize = range.Size / layers;
        if (_bufferCache.HasGpuDirtyBytes(range.Address + sliceSize * first, sliceSize * count))
        {
            return false;
        }

        Span<byte> firstByte = stackalloc byte[1];
        for (uint slice = 0; slice < count; slice++)
        {
            var address = range.Address + sliceSize * (first + slice);
            if (!_backing.TryReadBacking(address, firstByte))
            {
                return false;
            }

            if (PackedClearValue.TryDecodeDccColor(view.Format, firstByte[0] * 0x01010101u, description.Metadata,
                    allowClearToRegister: request.Role == ImageRole.ColorTarget, out _))
            {
                return false;
            }
        }

        return true;
    }

    public bool IsStaleForSlice(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Read();
        var image = _slots.TryGet(imageIdentifier);
        return image is null || (!image.Registered && !ImageDescription.IsEmptyRange(image.Description.Data)) || image.Binding.NeedsRebind;
    }

    public bool CanReuseAcquiredForSlice(ResourceSlotIdentifier imageIdentifier, ulong backing)
    {
        using var held = _lock.Read();
        return _slots.TryGet(imageIdentifier) is { Registered: true, Parked: false } image && !image.DepthOwner.IsValid &&
               !image.Binding.NeedsRebind && !image.IsCpuDirty && !image.IsBufferModified && image.Backing.Handle.Handle == backing;
    }

    public bool TryRememberedForSlice(ResourceSlotIdentifier imageIdentifier, ref ulong generation, ulong begin, ulong end)
    {
        using var held = _lock.Read();
        if (!LookupUnchangedSince(generation, begin, end) || _slots.TryGet(imageIdentifier) is not { Registered: true, Parked: false })
        {
            return false;
        }

        generation = _lookupGeneration;
        return true;
    }

    public bool CompleteLookupIsNoopForSlice(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var held = _lock.Read();
        var image = _slots.TryGet(imageIdentifier);
        if (image is null)
        {
            return false;
        }

        if (request.Role is ImageRole.Texture or ImageRole.StorageImage)
        {
            if (request.Description.Metadata.Kind == MetadataKind.Dcc && request.Description.Metadata.Range.Size == 0 &&
                image.Description.Metadata.Kind == MetadataKind.None)
            {
                return false;
            }

            var sliceSize = image.Description.DccSliceSize;
            if (sliceSize != 0 && GuestDccMetadataNeedsSync(image.Description.Metadata.Range.Address, sliceSize, request.View.BaseLayer, request.View.LayerCount))
            {
                return false;
            }
        }

        return request.Role != ImageRole.DisplaySurface && DccClearMaterializationIsNoop(image, in request);
    }

    public bool TryPrepareTextureViewForSlice(ResourceSlotIdentifier imageIdentifier, in ImageRequest request, out ImageView view)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        view = default;
        if (request.Role is not (ImageRole.Texture or ImageRole.StorageImage))
        {
            return false;
        }

        using var held = _lock.Read();
        var image = _slots.TryGet(imageIdentifier);
        if (image is null)
        {
            return false;
        }

        var hasData = !ImageDescription.IsEmptyRange(image.Description.Data);
        if (hasData && (!image.Registered || image.DepthOwner.IsValid || image.Binding.NeedsRebind))
        {
            return false;
        }

        if (hasData && image.Description.Metadata.Compression != DisplayCompression.Uncompressed && (image.IsBufferModified || image.IsDefinitelyCpuDirty))
        {
            return false;
        }

        view = image.GetOrCreateView(request.View);
        return true;
    }

    public bool TryPrepareColorTargetViewForSlice(ResourceSlotIdentifier imageIdentifier, in ImageRequest request, out ImageView view)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        view = default;
        if (request.Role != ImageRole.ColorTarget)
        {
            return false;
        }

        using var held = _lock.Read();
        var image = _slots.TryGet(imageIdentifier);
        if (image is null || !image.Registered || image.DepthOwner.IsValid || image.Binding.NeedsRebind)
        {
            return false;
        }

        if (request.Description.Metadata.Kind == MetadataKind.Dcc && request.Description.Metadata.Range.Size == 0)
        {
            if (!image.Description.Metadata.Equals(request.Description.Metadata) ||
                !_surfaceMetadata.TryGetValue(request.Description.Metadata.Range.Address, out var metadata) ||
                metadata.Kind != SurfaceMetadataKind.Dcc ||
                metadata.Size < request.Description.DccSliceSize * request.Description.TransferLayers)
            {
                return false;
            }
        }

        view = image.GetOrCreateView(request.View);
        return true;
    }

    public bool TryPrepareDepthTargetViewForSlice(ResourceSlotIdentifier imageIdentifier, in ImageRequest request, out ImageView view)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        view = default;
        if (request.Role != ImageRole.DepthTarget)
        {
            return false;
        }

        using var held = _lock.Read();
        var image = _slots.TryGet(imageIdentifier);
        if (image is null || !image.Registered)
        {
            return SliceLookupFailed(SliceLookupFailure.DepthUnregistered, request.Role);
        }

        if (image.DepthOwner.IsValid)
        {
            return SliceLookupFailed(SliceLookupFailure.DepthOwner, request.Role);
        }

        if (image.Binding.NeedsRebind)
        {
            return SliceLookupFailed(SliceLookupFailure.DepthRebind, request.Role);
        }

        if (request.Description.HasMetadata && !SameMetadataPlacement(image.Description.Metadata, request.Description.Metadata))
        {
            ref readonly var cached = ref image.Description.Metadata;
            ref readonly var requested = ref request.Description.Metadata;
            return SliceLookupFailed(cached.Range.Address != requested.Range.Address ? SliceLookupFailure.DepthMetadataAddress :
                cached.Kind != requested.Kind ? SliceLookupFailure.DepthMetadataKind : SliceLookupFailure.DepthMetadataCompression, request.Role);
        }

        if (request.Description.HasMetadata &&
            (!_surfaceMetadata.TryGetValue(request.Description.Metadata.Range.Address, out var metadata) || metadata.Kind != SurfaceMetadataKind.HTile))
        {
            return SliceLookupFailed(SliceLookupFailure.DepthMetadataUnregistered, request.Role);
        }

        if (request.Description.HasStencil)
        {
            if (!image.Description.Stencil.Equals(request.Description.Stencil) || !ImageDescription.IsValidRange(request.Description.Stencil))
            {
                return SliceLookupFailed(SliceLookupFailure.DepthStencil, request.Role);
            }

            var association = ResourceSlotIdentifier.Invalid;
            foreach (var candidate in CollectImagesInRange(request.Description.Stencil.Address, request.Description.Stencil.Size))
            {
                if (_slots[candidate].Description.Data.Address == request.Description.Stencil.Address)
                {
                    association = candidate;
                }
            }

            if (!association.IsValid)
            {
                return SliceLookupFailed(SliceLookupFailure.DepthStencilAssociation, request.Role);
            }
        }

        view = image.GetOrCreateView(request.View);
        return true;
    }

    private static bool SameMetadataPlacement(in MetadataDescription cached, in MetadataDescription requested) =>
        cached.Range.Address == requested.Range.Address && cached.Kind == requested.Kind && cached.Compression == requested.Compression;

    // Whether TryClearImageFromBuffer would select an image for the fill without recording anything.
    public bool WouldPublishImagesUnderWrite(ulong address, ulong size, bool wholeImages)
    {
        if (!IsValidRange(address, size))
        {
            return false;
        }

        using var held = _lock.Read();
        foreach (var imageIdentifier in CollectImagesInRange(address, size))
        {
            var image = _slots[imageIdentifier];
            var range = image.Description.Data;
            if (image.IsGpuModified && !image.DepthOwner.IsValid && image.Overlaps(address, size) &&
                (wholeImages || address > range.Address || address + size < range.Address + range.Size) &&
                image.SafeToDownload && !image.RawWriteAfterGpuWrite)
            {
                return true;
            }
        }

        return false;
    }

    public bool WouldClearImageFromBuffer(ulong address, ulong size, uint packedClear)
    {
        if (!IsValidRange(address, size))
        {
            return false;
        }

        using var held = _lock.Read();
        var selected = ResourceSlotIdentifier.Invalid;
        ImageAspectFlags aspect = 0;
        foreach (var imageIdentifier in CollectImagesInRange(address, size, includeParked: true))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null)
            {
                continue;
            }

            ImageAspectFlags candidate = 0;
            var candidateId = imageIdentifier;
            if (owner.DepthOwner.IsValid && owner.Description.Data.Address == address && owner.Description.Data.Size == size)
            {
                candidate = ImageAspectFlags.StencilBit;
                candidateId = owner.DepthOwner;
                owner = _slots.TryGet(candidateId);
                if (owner == null || !owner.Backing.Exists || !owner.Description.HasStencil)
                {
                    continue;
                }
            }
            else if (!owner.DepthOwner.IsValid && owner.Description.Data.Address == address && owner.Description.Data.Size == size)
            {
                candidate = owner.Description.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
            }

            if (candidate == 0)
            {
                continue;
            }

            if (selected.IsValid && selected != candidateId)
            {
                return false;
            }

            selected = candidateId;
            aspect = candidate;
        }

        if (!selected.IsValid)
        {
            return false;
        }

        var image = _slots[selected];
        return aspect switch
        {
            ImageAspectFlags.ColorBit => PackedClearValue.TryDecodeBufferColor(image.Description.PixelFormat, packedClear, out _),
            ImageAspectFlags.DepthBit => PackedClearValue.TryDecodeDepth(image.Description.PixelFormat, packedClear, out _),
            _ => PackedClearValue.TryDecodeStencil(packedClear, out _),
        };
    }

    public bool ValidateSliceImages(List<ImageSliceUse> uses, ulong lookupGeneration, long metadataGeneration)
    {
        using var held = _lock.Read();
        foreach (var use in uses)
        {
            var image = _slots.TryGet(use.Image);
            if (!ReferenceEquals(image, use.Expected) || image is null)
            {
                return false;
            }

            if (ImageDescription.IsEmptyRange(image.Description.Data))
            {
                continue;
            }

            if (!image.Registered || image.Parked || image.Binding.NeedsRebind || image.DepthOwner.IsValid)
            {
                return false;
            }

            var range = UnionRange(use.Request.Description.Data, image.Description.Data);
            if (!LookupUnchangedSince(lookupGeneration, range.Address, range.End))
            {
                return SliceLookupFailed(SliceLookupFailure.ChangedAtJoin, use.Request.Role);
            }

            if ((use.Request.Description.HasMetadata && !MetadataUnchangedSince(metadataGeneration, use.Request.Description.Metadata.Range.Address)) ||
                (image.Description.HasMetadata && !MetadataUnchangedSince(metadataGeneration, image.Description.Metadata.Range.Address)))
            {
                return SliceLookupFailed(SliceLookupFailure.MetadataChangedAtJoin, use.Request.Role);
            }
        }

        return true;
    }

    // Applies in stream order what a slice's recording thread only read: recency, refreshes from
    // guest memory, GPU ownership, metadata registration and readback scheduling.
    public void ApplySliceUse(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageAcquire);
        using var held = _lock.Hold();
        var image = _slots.TryGet(imageIdentifier);
        if (image is null)
        {
            throw SubmissionScheduler.Fatal($"A slice used an image that no longer exists: image={imageIdentifier.Index}.");
        }

        image.LastAccessTick = _scheduler.CurrentTick;
        TouchImage(image);
        switch (request.Role)
        {
            case ImageRole.Texture:
            case ImageRole.StorageImage:
                AcquireTextureCore(imageIdentifier, image, in request);
                break;
            case ImageRole.ColorTarget:
                AcquireColorTargetCore(imageIdentifier, image, in request);
                break;
            case ImageRole.DepthTarget:
                AcquireDepthTargetCore(imageIdentifier, image, in request);
                break;
            default:
                throw SubmissionScheduler.Fatal($"The slice image role is invalid: role={request.Role}.");
        }
    }
}

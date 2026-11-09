// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.Images;

// Registration in the page owner index, page watches, CPU-write invalidation and unmapping.
public sealed partial class GuestImageCache
{
    private void AddToIndex(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (image.Registered || ImageDescription.IsEmptyRange(image.Description.Data))
        {
            throw SubmissionScheduler.Fatal($"The image registration is invalid: address=0x{image.Description.Data.Address:X16} size=0x{image.Description.Data.Size:X} registered={image.Registered}.");
        }

        if (!ImagePageOwnerTable.TryGetPageRange(image.Description.Data.Address, image.Description.Data.Size, out var first, out var lastExclusive))
        {
            throw SubmissionScheduler.Fatal($"The image registration is outside the guest address space: address=0x{image.Description.Data.Address:X16} size=0x{image.Description.Data.Size:X}.");
        }

        for (var page = first; page < lastExclusive; page++)
        {
            _pageOwners.GetOrCreate(page).Add(imageIdentifier);
        }

        image.Registered = true;
        InvalidateLookups(image.Description.Data.Address, image.Description.Data.End);
        image.RecencyEntryIndex = _recencyQueue.Insert(imageIdentifier, _collectionTick);
        _totalUsedMemory += image.AccountedSize;
    }

    private void RemoveFromIndex(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (!image.Registered)
        {
            return;
        }

        UnwatchImage(imageIdentifier);
        if (!ImagePageOwnerTable.TryGetPageRange(image.Description.Data.Address, image.Description.Data.Size, out var first, out var lastExclusive))
        {
            throw SubmissionScheduler.Fatal($"The registered image is outside the guest address space: address=0x{image.Description.Data.Address:X16} size=0x{image.Description.Data.Size:X}.");
        }

        for (var page = first; page < lastExclusive; page++)
        {
            var owners = _pageOwners.Find(page);
            if (owners == null || !owners.Remove(imageIdentifier))
            {
                throw SubmissionScheduler.Fatal($"The image is missing from the page owner index: address=0x{image.Description.Data.Address:X16} page={page}.");
            }
        }

        _recencyQueue.Free(image.RecencyEntryIndex);
        var accounted = image.AccountedSize;
        if (accounted > _totalUsedMemory)
        {
            throw SubmissionScheduler.Fatal($"The image memory accounting underflows: accounted=0x{accounted:X} total=0x{_totalUsedMemory:X}.");
        }

        _totalUsedMemory -= accounted;
        image.Registered = false;
        image.HeapChanged?.Invoke(image);
    }

    // Removes the image and its stencil associations; the slot is freed after the current tick.
    private void DeleteImage(ResourceSlotIdentifier imageIdentifier)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageDelete);
        var image = _slots.TryGet(imageIdentifier);
        if (image == null || !image.Registered)
        {
            return;
        }

        if (image.Parked)
        {
            image.Parked = false;
            _parked.Remove(imageIdentifier);
        }

        if (!image.DepthOwner.IsValid)
        {
            var associations = new List<ResourceSlotIdentifier>();
            _slots.ForEach((candidate, associated) =>
            {
                if (associated.DepthOwner == imageIdentifier)
                {
                    associations.Add(candidate);
                }
            });
            foreach (var association in associations)
            {
                var associated = _slots[association];
                ReportImageLifetime(associated, "delete-depth-association", deletion: true);
                if (associated.IsGpuModified)
                {
                    associated.ClearGpuModified();
                }

                DeleteImage(association);
            }
        }

        if (image.IsGpuModified)
        {
            throw SubmissionScheduler.Fatal($"A GPU-modified image cannot be deleted before its contents are resolved: address=0x{image.Description.Data.Address:X16} size=0x{image.Description.Data.Size:X}.");
        }

        _scheduledReadbacks.Remove(imageIdentifier);
        if (image.Description.HasMetadata &&
            _surfaceMetadata.TryGetValue(image.Description.Metadata.Range.Address, out var registeredMetadata) &&
            ReferenceEquals(registeredMetadata, image.MetadataRegistration))
        {
            RecordMetadataEvent("remove-image-metadata", image.Description.Metadata.Range.Address,
                image.Description.Metadata.Range.Size);
            _surfaceMetadata.Remove(image.Description.Metadata.Range.Address);
            NoteMetadataChange(image.Description.Metadata.Range.Address);
        }

        RemoveFromIndex(imageIdentifier);
        if (_scheduler.Active)
        {
            var bytes = (long)image.Backing.AllocationSize;
            Interlocked.Add(ref _pendingDestroyBytes, bytes);
            _scheduler.QueueCompletionAction(() =>
            {
                _slots.Erase(imageIdentifier);
                Interlocked.Add(ref _pendingDestroyBytes, -bytes);
            });
        }
        else
        {
            _slots.Erase(imageIdentifier);
        }
    }

    private void ReleaseImage(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        TraceVolumeState("release", image, ImageRequest.Refresh(image.Description, ImageRole.Texture));
        if (image.IsGpuModified)
        {
            image.ClearGpuModified();
        }

        DeleteImage(imageIdentifier);
    }

    private void ParkOrReleaseImage(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        var park = !image.Parked && !image.DepthOwner.IsValid && image.Description.Samples <= 1;
        if (!park)
        {
            Interlocked.Increment(ref _parkReleases);
            ReleaseImage(imageIdentifier);
            return;
        }

        Interlocked.Increment(ref _parks);
        image.Parked = true;
        image.ParkedTick = _collectionTick;
        _parked.Add(imageIdentifier);
    }

    private bool TryUnparkImage(in ImageDescription requested, out ResourceSlotIdentifier result)
    {
        result = ResourceSlotIdentifier.Invalid;
        for (var index = _parked.Count - 1; index >= 0; index--)
        {
            var imageIdentifier = _parked[index];
            var image = _slots.TryGet(imageIdentifier);
            if (image == null || !image.Registered || !image.Parked)
            {
                _parked.RemoveAt(index);
                continue;
            }

            if (!HasSameBacking(image.Description, requested, exactFormat: true) || image.Description.Resources != requested.Resources ||
                image.Description.Type != requested.Type)
            {
                continue;
            }

            _parked.RemoveAt(index);
            Interlocked.Increment(ref _unparks);
            image.Parked = false;
            image.LastAccessTick = _scheduler.CurrentTick;
            TouchImage(image);
            InvalidateLookups(image.Description.Data.Address, image.Description.Data.End);
            result = imageIdentifier;
            return true;
        }

        return false;
    }

    private void ExpireParkedImages(ulong tick)
    {
        for (var index = _parked.Count - 1; index >= 0; index--)
        {
            var imageIdentifier = _parked[index];
            var image = _slots.TryGet(imageIdentifier);
            if (image == null || !image.Registered || !image.Parked)
            {
                _parked.RemoveAt(index);
                continue;
            }

            if (tick - Math.Min(tick, image.ParkedTick) <= ParkedFramesBeforeRemoval && _parked.Count <= ParkedImageLimit)
            {
                continue;
            }

            _parked.RemoveAt(index);
            Interlocked.Increment(ref _parkExpiries);
            image.Parked = false;
            ReleaseImage(imageIdentifier);
        }
    }

    private void TouchImage(CachedImage image)
    {
        if (image.Registered)
        {
            _recencyQueue.Touch(image.RecencyEntryIndex, _collectionTick);
        }
    }

    private void MarkMaybeCpuDirty(ResourceSlotIdentifier imageIdentifier, CachedImage image)
    {
        image.MarkMaybeCpuDirty();
        if (image.NeedsMaybeCpuHash)
        {
            image.SetMaybeCpuHash(image.HashGuestEdges());
        }

        UnwatchImage(imageIdentifier);
    }

    private void WatchImage(ResourceSlotIdentifier imageIdentifier)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTracking);
        var image = _slots[imageIdentifier];
        if (!image.Registered)
        {
            return;
        }

        var imageBegin = image.Description.Data.Address;
        var imageEnd = image.Description.Data.End;
        if (imageBegin == image.WatchBegin && imageEnd == image.WatchEnd)
        {
            return;
        }

        if (!image.IsWatched)
        {
            image.WatchBegin = imageBegin;
            image.WatchEnd = imageEnd;
            _pages.AddWatch(imageBegin, image.Description.Data.Size, blockReads: false);
            return;
        }

        if (imageBegin < image.WatchBegin)
        {
            WatchImageHead(imageIdentifier);
        }

        if (image.WatchEnd < imageEnd)
        {
            WatchImageTail(imageIdentifier);
        }
    }

    private void WatchImageHead(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (!image.Registered)
        {
            return;
        }

        var imageBegin = image.Description.Data.Address;
        if (imageBegin == image.WatchBegin)
        {
            return;
        }

        if (!image.IsWatched || imageBegin > image.WatchBegin)
        {
            throw SubmissionScheduler.Fatal($"The image head watch range is invalid: address=0x{imageBegin:X16} watchBegin=0x{image.WatchBegin:X16} watchEnd=0x{image.WatchEnd:X16}.");
        }

        var size = image.WatchBegin - imageBegin;
        image.WatchBegin = imageBegin;
        _pages.AddWatch(imageBegin, size, blockReads: false);
    }

    private void WatchImageTail(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (!image.Registered)
        {
            return;
        }

        var imageEnd = image.Description.Data.End;
        if (imageEnd == image.WatchEnd)
        {
            return;
        }

        if (!image.IsWatched || image.WatchEnd > imageEnd)
        {
            throw SubmissionScheduler.Fatal($"The image tail watch range is invalid: end=0x{imageEnd:X16} watchBegin=0x{image.WatchBegin:X16} watchEnd=0x{image.WatchEnd:X16}.");
        }

        var address = image.WatchEnd;
        var size = imageEnd - address;
        image.WatchEnd = imageEnd;
        _pages.AddWatch(address, size, blockReads: false);
    }

    private void UnwatchImage(ResourceSlotIdentifier imageIdentifier)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTracking);
        var image = _slots[imageIdentifier];
        if (!image.IsWatched)
        {
            return;
        }

        var address = image.WatchBegin;
        var size = image.WatchEnd - image.WatchBegin;
        image.WatchBegin = 0;
        image.WatchEnd = 0;
        if (size != 0)
        {
            _pages.RemoveWatch(address, size, blockReads: false);
        }
    }

    private void UnwatchImageHead(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        var begin = image.Description.Data.Address;
        if (!image.IsWatched || begin < image.WatchBegin)
        {
            return;
        }

        var address = (begin + TrackerLayout.PageBytes) & ~(TrackerLayout.PageBytes - 1);
        var size = address - begin;
        image.WatchBegin = address;
        if (image.WatchBegin == image.WatchEnd)
        {
            MarkMaybeCpuDirty(imageIdentifier, image);
        }

        if (size != 0)
        {
            _pages.RemoveWatch(begin, size, blockReads: false);
        }
    }

    private void UnwatchImageTail(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        var end = image.Description.Data.End;
        if (!image.IsWatched || image.WatchEnd < end)
        {
            return;
        }

        var address = end & ~(TrackerLayout.PageBytes - 1);
        var size = end - address;
        image.WatchEnd = address;
        if (image.WatchBegin == image.WatchEnd)
        {
            MarkMaybeCpuDirty(imageIdentifier, image);
        }

        if (size != 0)
        {
            _pages.RemoveWatch(address, size, blockReads: false);
        }
    }

    private void ScheduleReadback(ResourceSlotIdentifier imageIdentifier, CachedImage image)
    {
        if (_readbackLinearImages && !image.Description.IsTiled && !ImageDescription.IsEmptyRange(image.Description.Data))
        {
            if (!image.IsGpuModified)
            {
                throw SubmissionScheduler.Fatal($"An image the GPU does not own cannot be scheduled for readback: address=0x{image.Description.Data.Address:X16}.");
            }

            _scheduledReadbacks.Add(imageIdentifier);
        }
    }

    // Caller holds the lock; the query epoch marks each image once per query.
    private ImageQueryResults FindImagesInRange(ulong address, ulong size, bool pageOverlap, bool includeParked = false)
    {
        var result = new ImageQueryResults();
        if (!ImagePageOwnerTable.TryGetPageRange(address, size, out var first, out var lastExclusive))
        {
            return result;
        }

        var queryEpoch = ++_queryEpoch;
        if (queryEpoch == 0)
        {
            _slots.ForEach((_, image) => image.QueryEpoch = 0);
            queryEpoch = ++_queryEpoch;
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
                if (image == null || image.QueryEpoch == queryEpoch || (image.Parked && !includeParked))
                {
                    continue;
                }

                image.QueryEpoch = queryEpoch;
                if (image.Overlaps(address, size, pageOverlap))
                {
                    result.Add(imageIdentifier);
                }
            }
        }

        return result;
    }

    // A CPU write on the faulting thread: true when an image page covers the range.
    bool IGuestImageStore.MarkCpuWrite(ulong address, ulong size)
    {
        GpuMemoryAccessProfile.CountImageCpuWrite();
        if (!IsValidRange(address, size))
        {
            return false;
        }

        // Readbacks still to land keep the stored pages. An evicted image owns no pages, so they hear of the
        // store before the owner checks, and outside the image lock.
        _readbackLandings.NoteGuestStore(address, size);

        // Skip the lock only when neither image pages nor metadata can cover the write.
        if (!MayOwnPages(address, size) && !MayOverlapMetadata(address, size))
        {
            return false;
        }

        using var held = _lock.Hold();
        if (!_pageOwners.MayHaveOwners(address, size))
        {
            // Metadata can occupy a page with no image data owner.
            InvalidateMetadataForCpuWrite(address, size);
            return false;
        }

        return InvalidateAliases(address, size);
    }

    private const ulong MaxLockFreeOwnerPages = 64;

    private bool MayOwnPages(ulong address, ulong size)
    {
        if (!ImagePageOwnerTable.TryGetPageRange(address, size, out var first, out var lastExclusive) ||
            lastExclusive - first > MaxLockFreeOwnerPages)
        {
            return true;
        }

        for (var page = first; page < lastExclusive; page++)
        {
            if (_pageOwners.Find(page) is { IsEmpty: false })
            {
                return true;
            }
        }

        return false;
    }

    public void InvalidateMemory(ulong address, ulong size)
    {
        GpuMemoryAccessProfile.CountImageCpuWrite();
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The memory invalidation range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        _readbackLandings.NoteGuestStore(address, size);
        using var held = _lock.Hold();
        InvalidateAliases(address, size);
    }

    // A byte overlap makes an image definitely dirty; a page-only overlap unwatches an edge page
    // or makes the image maybe dirty. Returns whether any image shares a page with the range.
    private bool InvalidateAliases(ulong address, ulong size)
    {
        RecordResourceHistory("cpu-write-notification", address, size);
        if (ImageClearTrace.Enabled)
            _metadataHistory.RecordRange(CreateMetadataEvent("cpu-write-notification", address, size, guestCpuWrite: true));
        InvalidateMetadataForCpuWrite(address, size);
        var pageBegin = address & ~(TrackerLayout.PageBytes - 1);
        var pageEnd = (address + size + TrackerLayout.PageBytes - 1) & ~(TrackerLayout.PageBytes - 1);
        var covered = false;
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true, includeParked: true))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null)
            {
                continue;
            }

            covered = true;
            if (owner.Overlaps(address, size))
            {
                owner.InvalidateCpuWrite(address, size);
                UnwatchImage(imageIdentifier);
                continue;
            }

            var imageBegin = owner.Description.Data.Address;
            var imageEnd = owner.Description.Data.End;
            if (pageEnd < imageEnd)
            {
                UnwatchImageHead(imageIdentifier);
            }
            else if (imageBegin < pageBegin)
            {
                UnwatchImageTail(imageIdentifier);
            }
            else
            {
                MarkMaybeCpuDirty(imageIdentifier, owner);
            }
        }

        return covered;
    }

    public void InvalidateMemoryFromGpu(ulong address, ulong size) => InvalidateMemoryFromGpu(address, size, publish: true);

    // A formatted shader write whose images were published when its stage was prepared: the images under it
    // lose their ownership only after its buffer took the contents of an image it starts at.
    public void InvalidateMemoryFromGpuAfterPublish(ulong address, ulong size) => InvalidateMemoryFromGpu(address, size, publish: false);

    private void InvalidateMemoryFromGpu(ulong address, ulong size, bool publish)
    {
        if (SubmissionScheduler.ReadOnlyThread)
        {
            throw SubmissionScheduler.ReadOnlyViolation("image invalidate");
        }

        if (!IsValidRange(address, size))
        {
            return;
        }

        if (publish)
        {
            PublishPartlyWrittenImages(address, size);
        }

        using var held = _lock.Hold();
        if (ImageClearTrace.Enabled)
            _metadataHistory.RecordRange(CreateMetadataEvent("gpu-write-notification", address, size));
        RecordResourceHistory("gpu-buffer-write-notification", address, size);
        var found = new List<ResourceSlotIdentifier>();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true, includeParked: true))
        {
            found.Add(imageIdentifier);
        }

        foreach (var imageIdentifier in found)
        {
            var image = _slots[imageIdentifier];
            if (!image.Overlaps(address, size))
            {
                continue;
            }

            if (image.IsGpuModified && IsNestedAliasWrite(image, found, address, size))
            {
                continue;
            }

            if (image.IsGpuModified)
            {
                image.ClearGpuModified();
            }

            image.MarkBufferModified();
        }
    }

    // A write over part of a GPU-modified image leaves the image's other bytes in memory: its GPU-only
    // contents move into the buffer over its range first, so its next upload, or another image over those
    // bytes, starts from them instead of stale guest memory. Buffer bytes the GPU wrote under the image
    // before it are older than its contents: a buffer write after it over part of it published it then.
    // A raw binding over the whole image leaves it GPU-modified; its bytes may be newer than the image,
    // so such an image is not published, unless a raw binding about to be written is exactly the image's
    // range. A formatted write takes the ownership of the whole images under it too, which then publish as
    // well. The buffer is obtained outside the lock; contents that cannot be copied are lost as before.
    private bool PublishPartlyWrittenImages(ulong address, ulong size, bool wholeImages = false, bool exactImages = false)
    {
        var published = false;
        List<ResourceSlotIdentifier>? partlyWritten = null;
        using (var held = _lock.Hold())
        {
            foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
            {
                var image = _slots[imageIdentifier];
                var range = image.Description.Data;
                var exact = exactImages && address == range.Address && size == range.Size;
                if (image.IsGpuModified && image.Overlaps(address, size) &&
                    (wholeImages || exact || address > range.Address || address + size < range.Address + range.Size) &&
                    image.SafeToDownload && !image.RawWriteAfterGpuWrite && !HasNewerHandedOverImage(imageIdentifier, image))
                {
                    (partlyWritten ??= []).Add(imageIdentifier);
                }
            }

            // Images over the same bytes publish in the order the GPU wrote them, so the buffer ends with
            // the newest contents whichever image the owner table lists first.
            partlyWritten?.Sort((left, right) => _slots[left].GpuWriteSequence.CompareTo(_slots[right].GpuWriteSequence));
        }

        foreach (var imageIdentifier in partlyWritten ?? [])
        {
            GuestSpan range;
            using (var held = _lock.Hold())
            {
                if (_slots.TryGet(imageIdentifier) is not { IsGpuModified: true } image)
                {
                    continue;
                }

                range = image.Description.Data;
            }

            if (_bufferCache.ObtainBufferForImageWriteBack(range.Address, range.Size) is not { } buffer)
            {
                continue;
            }

            using (var held = _lock.Hold())
            {
                // The buffer was obtained outside the lock, which can land held writes and run completed work:
                // the image must still be safe to download and the newest owner of its bytes.
                if (_slots.TryGet(imageIdentifier) is { IsGpuModified: true, SafeToDownload: true, RawWriteAfterGpuWrite: false } image &&
                    !HasNewerHandedOverImage(imageIdentifier, image) && TryDownloadImageToBuffer(image, buffer))
                {
                    image.ClearGpuModified();
                    MarkOlderOverlappingImagesOverwritten(imageIdentifier, image);
                    published = true;
                }
            }
        }

        return published;
    }

    // A newer GPU-written image over the same memory that has already handed its bytes to the buffer (or lost
    // them to a CPU write): publishing this older image would put its stale contents over them. Newer images
    // that still hold their bytes publish after this one, oldest first. The caller holds the lock.
    private bool HasNewerHandedOverImage(ResourceSlotIdentifier imageIdentifier, CachedImage image)
    {
        var range = image.Description.Data;
        foreach (var otherIdentifier in FindImagesInRange(range.Address, range.Size, pageOverlap: false))
        {
            if (otherIdentifier != imageIdentifier && _slots.TryGet(otherIdentifier) is { IsGpuModified: false } other &&
                other.DepthOwner != imageIdentifier && other.GpuWriteSequence > image.GpuWriteSequence && other.Overlaps(range.Address, range.Size))
            {
                return true;
            }
        }

        return false;
    }

    // After a publish the buffer holds this image's bytes over its whole range; an older GPU-written image
    // over the same memory must not publish its contents over them later. The caller holds the lock.
    private void MarkOlderOverlappingImagesOverwritten(ResourceSlotIdentifier imageIdentifier, CachedImage image)
    {
        var range = image.Description.Data;
        foreach (var otherIdentifier in FindImagesInRange(range.Address, range.Size, pageOverlap: false))
        {
            if (otherIdentifier != imageIdentifier && _slots.TryGet(otherIdentifier) is { IsGpuModified: true } other &&
                other.GpuWriteSequence < image.GpuWriteSequence && other.Overlaps(range.Address, range.Size))
            {
                other.MarkRawWriteAfterGpuWrite();
            }
        }
    }

    // A raw binding that covers only part of a GPU-modified image names memory the guest gave to a buffer.
    // Before the shader writes it, the image's contents move into the buffer, so no later publish of the
    // image can put them over the shader's bytes. A binding that covers an image whole may be a window
    // that never writes there; the image keeps its bytes. A binding of exactly the image's range names the
    // image and publishes it as well.
    public void PublishImagesUnderRawWrite(ulong address, ulong size)
    {
        if (IsValidRange(address, size))
        {
            PublishPartlyWrittenImages(address, size, exactImages: true);
        }
    }

    // A formatted binding takes the ownership of every image under it once its buffer is obtained, while its
    // shader may write only part of the window. The images it covers whole move their contents into the
    // buffer first as well, so the bytes the shader leaves keep them.
    public void PublishImagesUnderFormattedWrite(ulong address, ulong size)
    {
        if (IsValidRange(address, size))
        {
            PublishPartlyWrittenImages(address, size, wholeImages: true);
        }
    }

    public void PublishImagesUnderRead(ulong address, ulong size)
    {
        if (IsValidRange(address, size))
        {
            PublishPartlyWrittenImages(address, size, wholeImages: true);
        }
    }

    // A GPU-modified image owns its bytes; a raw binding can overlap it without writing there.
    public void InvalidateMemoryCopiesFromGpu(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            return;
        }

        using var held = _lock.Hold();
        RecordResourceHistory("gpu-buffer-write-notification", address, size);
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true, includeParked: true))
        {
            var image = _slots[imageIdentifier];
            if (image.DepthOwner.IsValid || !image.Overlaps(address, size))
            {
                continue;
            }

            if (image.IsGpuModified)
            {
                image.MarkRawWriteAfterGpuWrite();
                continue;
            }

            image.MarkBufferModified();
        }
    }

    private bool IsNestedAliasWrite(CachedImage image, List<ResourceSlotIdentifier> found, ulong address, ulong size)
    {
        var outer = image.Description.Data;
        foreach (var otherIdentifier in found)
        {
            var other = _slots[otherIdentifier];
            if (ReferenceEquals(other, image))
            {
                continue;
            }

            var inner = other.Description.Data;
            if (inner.Size < outer.Size && inner.Address >= outer.Address && inner.Address + inner.Size <= outer.Address + outer.Size &&
                address >= inner.Address && address + size <= inner.Address + inner.Size)
            {
                return true;
            }
        }

        return false;
    }

    public ImageRegionInfo QueryRegion(ulong address, ulong size)
    {
        GpuMemoryAccessProfile.CountImageQuery(gpuDirtyOnly: false);
        if (!IsValidRange(address, size))
        {
            return default;
        }

        using var held = _lock.Hold();
        var imagePages = false;
        var imageBytes = false;
        var gpuImageBytes = false;
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true))
        {
            var image = _slots[imageIdentifier];
            imagePages = true;
            imageBytes |= image.Overlaps(address, size);
            gpuImageBytes |= !image.DepthOwner.IsValid && image.GpuOverlaps(address, size);
        }

        return new ImageRegionInfo(imagePages, imageBytes, gpuImageBytes);
    }

    // Only byte overlap with a GPU-owned image can make a clean backing read unsafe.
    public bool HasGpuModifiedImageBytes(ulong address, ulong size)
    {
        GpuMemoryAccessProfile.CountImageQuery(gpuDirtyOnly: true);
        if (!IsValidRange(address, size) || !_pageOwners.MayHaveOwners(address, size) || !MayOwnPages(address, size)) return false;

        using var held = _lock.Hold();
        if (!ImagePageOwnerTable.TryGetPageRange(address, size, out var first, out var lastExclusive)) return false;
        for (var page = first; page < lastExclusive; page++)
        {
            var owners = _pageOwners.Find(page);
            if (owners is null) continue;
            for (var ownerIndex = 0; ownerIndex < owners.Count; ownerIndex++)
            {
                var image = _slots.TryGet(owners[ownerIndex]);
                if (image is not null && !image.DepthOwner.IsValid && image.GpuOverlaps(address, size)) return true;
            }
        }
        return false;
    }

    // The guest unmapped the range: drop its metadata and every image in it without a readback.
    void IGuestImageStore.Unregister(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The unmap range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        using var held = _lock.Hold();
        if (ImageClearTrace.Enabled)
            _metadataHistory.RecordRange(CreateMetadataEvent("unmap", address, size));
        RecordResourceHistory("unmap", address, size);
        Interlocked.Increment(ref _unmapGeneration);
        var end = address + size;
        var stale = new List<ulong>();
        foreach (var metadataAddress in _surfaceMetadata.Keys)
        {
            if (metadataAddress >= address && metadataAddress < end)
            {
                stale.Add(metadataAddress);
            }
        }

        foreach (var metadataAddress in stale)
        {
            _surfaceMetadata.Remove(metadataAddress);
            NoteMetadataChange(metadataAddress);
        }

        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false, includeParked: true))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null)
            {
                continue;
            }

            if (owner.IsGpuModified)
            {
                owner.ClearGpuModified();
            }

            DeleteImage(imageIdentifier);
        }
    }
}

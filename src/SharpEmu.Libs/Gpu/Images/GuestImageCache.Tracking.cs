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
        if (image.Description.HasMetadata)
        {
            _surfaceMetadata.Remove(image.Description.Metadata.Range.Address);
        }

        RemoveFromIndex(imageIdentifier);
        if (_scheduler.Active)
        {
            _scheduler.QueueCompletionAction(() => _slots.Erase(imageIdentifier));
        }
        else
        {
            _slots.Erase(imageIdentifier);
        }
    }

    private void ReleaseImage(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (image.IsGpuModified)
        {
            image.ClearGpuModified();
        }

        DeleteImage(imageIdentifier);
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
    private List<ResourceSlotIdentifier> FindImagesInRange(ulong address, ulong size, bool pageOverlap)
    {
        var result = new List<ResourceSlotIdentifier>();
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

            owners.ForEach(imageIdentifier =>
            {
                var image = _slots.TryGet(imageIdentifier);
                if (image == null || image.QueryEpoch == queryEpoch)
                {
                    return;
                }

                image.QueryEpoch = queryEpoch;
                if (image.Overlaps(address, size, pageOverlap))
                {
                    result.Add(imageIdentifier);
                }
            });
        }

        return result;
    }

    // A CPU write on the faulting thread: true when an image page covers the range.
    bool IGuestImageStore.MarkCpuWrite(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            return false;
        }

        using var held = _lock.Hold();
        return InvalidateAliases(address, size);
    }

    public void InvalidateMemory(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The memory invalidation range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        using var held = _lock.Hold();
        InvalidateAliases(address, size);
    }

    // A byte overlap makes an image definitely dirty; a page-only overlap unwatches an edge page
    // or makes the image maybe dirty. Returns whether any image shares a page with the range.
    private bool InvalidateAliases(ulong address, ulong size)
    {
        var pageBegin = address & ~(TrackerLayout.PageBytes - 1);
        var pageEnd = (address + size + TrackerLayout.PageBytes - 1) & ~(TrackerLayout.PageBytes - 1);
        var covered = false;
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true))
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null || owner.DepthOwner.IsValid)
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

    public void InvalidateMemoryFromGpu(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            return;
        }

        using var held = _lock.Hold();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true))
        {
            var image = _slots[imageIdentifier];
            if (image.DepthOwner.IsValid || !image.Overlaps(address, size))
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

    public ImageRegionInfo QueryRegion(ulong address, ulong size)
    {
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
            if (image.DepthOwner.IsValid)
            {
                continue;
            }

            imagePages = true;
            imageBytes |= image.Overlaps(address, size);
            gpuImageBytes |= image.GpuOverlaps(address, size);
        }

        return new ImageRegionInfo(imagePages, imageBytes, gpuImageBytes);
    }

    // The guest unmapped the range: drop its metadata and every image in it without a readback.
    void IGuestImageStore.Unregister(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            throw SubmissionScheduler.Fatal($"The unmap range is invalid: address=0x{address:X16} size=0x{size:X16}.");
        }

        using var held = _lock.Hold();
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
        }

        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
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

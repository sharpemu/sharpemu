// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

// Garbage collection by recency and memory pressure, and the scheduled readback flush.
public sealed partial class GuestImageCache
{
    // Cached images allocate their own device memory (see CachedImage), so the pressure
    // includes retired images until GPU completion actually releases their memory.
    private ulong CollectionMemoryBytes => TotalUsedMemory +
        _imageMemoryPool.UnusedBytes + (_backingPool?.PooledBytes ?? 0);

    // Allocation failure runs with the cache lock already held. Enumerate only
    // on failure, so these diagnostics add no per-draw walk or logging traffic.
    private string DescribeImageMemory()
    {
        var registered = 0;
        ulong cleanBytes = 0, modifiedLinearBytes = 0, modifiedTiledBytes = 0;
        _slots.ForEach((_, image) =>
        {
            if (!image.Registered) return;
            registered++;
            if (!image.IsGpuModified) cleanBytes += image.AccountedSize;
            else if (image.Description.IsTiled) modifiedTiledBytes += image.AccountedSize;
            else modifiedLinearBytes += image.AccountedSize;
        });
        return $"registered_images={registered} clean_image_bytes={cleanBytes} gpu_modified_linear_bytes={modifiedLinearBytes} " +
            $"gpu_modified_tiled_bytes={modifiedTiledBytes} retired_image_bytes={Interlocked.Read(ref _retiredImageMemoryBytes)} " +
            $"retained_backing_bytes={_backingPool?.PooledBytes ?? 0} collection_bytes={CollectionMemoryBytes} critical_bytes={_criticalMemoryBytes}";
    }

    // Allocation-time pressure is deliberately separate from the periodic sweep:
    // a visibility-buffer burst can create thousands of images before the next
    // frame boundary. Destruction still goes through DeleteImage's completion
    // action, so no image is freed while the current tick can reference it.
    private void CollectForAllocation(ulong requiredBytes)
    {
        FinishRetiredImagesForAllocation(requiredBytes);
        // A burst can contain only newly touched images. Once the recency walk finds
        // no evictable image, retrying it for every allocation in the same tick is
        // pure repeated work; the next tick will make older images eligible.
        if (_allocationCollectionBlocked)
        {
            return;
        }

        var current = CollectionMemoryBytes;
        if (requiredBytes > _criticalMemoryBytes || current <= _criticalMemoryBytes - requiredBytes)
        {
            return;
        }

        var target = _criticalMemoryBytes - requiredBytes;
        var blocked = false;
        // Retired images still occupy memory until completion. Do not retire
        // the entire old working set merely because that completion is pending.
        while (CollectionMemoryBytes - (ulong)Interlocked.Read(ref _retiredImageMemoryBytes) > target)
        {
            var before = _totalUsedMemory;
            Collect(_collectionTick, allowAggressive: true);
            if (_totalUsedMemory == before)
            {
                blocked = true;
                break;
            }
        }

        _allocationCollectionBlocked = blocked;
        FinishRetiredImagesForAllocation(requiredBytes);
    }

    private void FinishRetiredImagesForAllocation(ulong requiredBytes)
    {
        var retired = Interlocked.Read(ref _retiredImageMemoryBytes);
        if (retired == 0 || !_scheduler.Active || _scheduler.InsideTickCallback)
            return;
        var resident = CollectionMemoryBytes;
        if (!RetirementOverBudget && requiredBytes <= _criticalMemoryBytes &&
            resident <= _criticalMemoryBytes - requiredBytes)
            return;

        // Completion can call back into the cache. Never wait while holding its
        // region lock. Images touched by this preparation remain registered;
        // only previously retired objects are destroyed by these callbacks.
        _lock.Exit();
        try { _scheduler.Finish(); }
        finally { _lock.Enter(); }
        ReleaseUnusedMemoryCore();
        _allocationCollectionBlocked = false;
    }

    public void RunGarbageCollector()
    {
        using var held = _lock.Hold();
        var tick = _collectionTick++;
        _allocationCollectionBlocked = false;
        if (MemoryUnderPressure)
            ReleaseUnusedMemoryCore();
        else
            _imageMemoryPool.ReleaseRetained();
        if (CollectionMemoryBytes < _collectionStartBytes)
        {
            return;
        }

        Collect(tick, allowAggressive: false);
        if (CollectionMemoryBytes >= _criticalMemoryBytes)
        {
            Collect(tick, allowAggressive: true);
        }
    }

    public void ReleaseUnusedMemory()
    {
        using var held = _lock.Hold();
        ReleaseUnusedMemoryCore();
    }

    private void ReleaseUnusedMemoryCore()
    {
        // Release retained image objects before trimming the allocator blocks
        // containing them. Neither operation touches a registered image.
        _backingPool?.ReleaseRetained();
        _imageMemoryPool.ReleaseRetained();
        _device.Slabs.ReleaseUnused();
        _tiler.ReleaseUnusedScratch();
    }

    private void Collect(ulong tick, bool allowAggressive)
    {
        var pressured = CollectionMemoryBytes >= _memoryPressureBytes;
        var aggressive = allowAggressive && CollectionMemoryBytes >= _criticalMemoryBytes;
        // Under pressure, retain the current collection interval's bindings only.
        // Recorded GPU references are protected by deferred destruction in DeleteImage;
        // waiting more frames as memory fills prevents reclamation in slow titles.
        var age = Math.Min(pressured ? 1UL : 16UL, tick);
        var deletions = aggressive ? 40 : pressured ? 20 : 10;
        var candidates = new List<ResourceSlotIdentifier>(deletions);
        // Deleting a depth image also deletes its stencil association, so the recency walk ends first.
        _recencyQueue.ForEachItemAtOrBeforeTick(tick - age, imageIdentifier =>
        {
            candidates.Add(imageIdentifier);
            // A protected prefix must not hide older reclaimable images behind it.
            // Stop after successful retirements below, not after visiting candidates.
            return false;
        });
        foreach (var imageIdentifier in candidates)
        {
            if (deletions <= 0)
            {
                break;
            }

            if (pressured && _scheduler.Active && !_scheduler.InsideTickCallback &&
                _tiler.ScratchOverBudget)
            {
                // Readback scratch and retired images are still resident until
                // completion. Bound the batch before allocating the next copy.
                // Completion publishes guest data and can re-enter this cache.
                _lock.Exit();
                try { _scheduler.Finish(); }
                finally { _lock.Enter(); }
                ReleaseUnusedMemoryCore();
            }

            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null || !owner.Registered || owner.DepthOwner.IsValid)
            {
                continue;
            }

            if (owner.IsGpuModified)
            {
                var safe = CanReadBack(owner);
                if (safe && !pressured)
                {
                    continue;
                }

                if (safe && !TryDownloadToGuest(imageIdentifier))
                {
                    continue;
                }

                owner.ClearGpuModified();
            }

            deletions -= DeleteImage(imageIdentifier);
            if (CollectionMemoryBytes < _criticalMemoryBytes && aggressive)
            {
                deletions >>= 2;
                aggressive = false;
            }

            if (CollectionMemoryBytes < _memoryPressureBytes && pressured)
            {
                deletions >>= 1;
                pressured = false;
            }
        }
    }

    // Publishes every scheduled linear GPU-written image to guest memory.
    public void FlushScheduledReadbacks()
    {
        using var held = _lock.Hold();
        foreach (var imageIdentifier in _scheduledReadbacks)
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner != null && owner.Registered && owner.IsGpuModified)
            {
                _ = TryDownloadToGuest(imageIdentifier);
            }
        }

        _scheduledReadbacks.Clear();
    }

    // Test seams: thresholds, the recency order and the private state the tests inspect.
    internal void SetCollectionThresholds(ulong trigger, ulong pressure, ulong critical, ulong tick)
    {
        _collectionThresholdsOverridden = true;
        _collectionStartBytes = trigger;
        _memoryPressureBytes = pressure;
        _criticalMemoryBytes = critical;
        _collectionTick = tick;
    }

    internal void ResetRecency(ReadOnlySpan<ResourceSlotIdentifier> oldest, ulong tick)
    {
        var live = new List<ResourceSlotIdentifier>();
        _recencyQueue = new RecencyQueue<ResourceSlotIdentifier>();
        _slots.ForEach((imageIdentifier, image) =>
        {
            if (image.Registered)
            {
                image.LastAccessTick = _scheduler.CurrentTick;
                live.Add(imageIdentifier);
            }
        });
        foreach (var imageIdentifier in oldest)
        {
            var owner = _slots.TryGet(imageIdentifier);
            if (owner != null && owner.Registered)
            {
                owner.LastAccessTick = 0;
                owner.RecencyEntryIndex = _recencyQueue.Insert(imageIdentifier, 0);
            }
        }

        foreach (var imageIdentifier in live)
        {
            if (oldest.IndexOf(imageIdentifier) < 0)
            {
                _slots[imageIdentifier].RecencyEntryIndex = _recencyQueue.Insert(imageIdentifier, tick);
            }
        }
    }

    internal bool Contains(ResourceSlotIdentifier imageIdentifier) => _slots.TryGet(imageIdentifier) is { Registered: true };

    internal CachedImage? Owner(ResourceSlotIdentifier imageIdentifier) => _slots.TryGet(imageIdentifier);

    internal bool IsReadbackScheduled(ResourceSlotIdentifier imageIdentifier) => _scheduledReadbacks.Contains(imageIdentifier);

    internal int NullImageCount => _nullImages.Count;

    internal void SetLinearReadback(bool enabled) => _readbackLinearImages = enabled;

    internal List<ResourceSlotIdentifier> FindImagesInRangeForTest(ulong address, ulong size, bool pageOverlap)
    {
        using var held = _lock.Hold();
        var result = new List<ResourceSlotIdentifier>();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap)) result.Add(imageIdentifier);
        return result;
    }

    internal int PageOwnerCount(ulong address)
    {
        using var held = _lock.Hold();
        return _pageOwners.Find(address >> ImagePageOwnerTable.PageBits)?.Count ?? 0;
    }

    internal int OwnedPageCount(ulong address, ulong size, ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (!ImagePageOwnerTable.TryGetPageRange(address, size, out var first, out var lastExclusive))
        {
            return 0;
        }

        var count = 0;
        for (var page = first; page < lastExclusive; page++)
        {
            count += _pageOwners.Find(page)?.Contains(imageIdentifier) == true ? 1 : 0;
        }

        return count;
    }

    internal void AddPageOwner(ulong address, ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        _pageOwners.GetOrCreate(address >> ImagePageOwnerTable.PageBits).Add(imageIdentifier);
    }

    internal bool RemovePageOwner(ulong address, ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        return _pageOwners.Find(address >> ImagePageOwnerTable.PageBits)?.Remove(imageIdentifier) == true;
    }

    internal uint QueryEpoch
    {
        get
        {
            using var held = _lock.Hold();
            return _queryEpoch;
        }
        set
        {
            using var held = _lock.Hold();
            _queryEpoch = value;
        }
    }

    internal ResourceSlotIdentifier InsertImageForTest(in ImageDescription description)
    {
        using var held = _lock.Hold();
        return InsertImage(description);
    }

    internal void DeleteImageForTest(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        DeleteImage(imageIdentifier);
    }

    internal void ScheduleReadbackForTest(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        ScheduleReadback(imageIdentifier, _slots[imageIdentifier]);
    }

    internal void AssociateStencilForTest(ResourceSlotIdentifier depth, HLE.GpuMemory.GuestSpan stencil)
    {
        using var held = _lock.Hold();
        AssociateStencilRange(depth, stencil);
    }

    internal bool TryDownloadForTest(ResourceSlotIdentifier imageIdentifier) => TryDownloadToGuest(imageIdentifier);

    internal GpuTiler TilerForTest => _tiler;

    internal void RegisterHtileMetadataForTest(ulong address)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var metadata))
        {
            metadata = new SurfaceMetadata();
            _surfaceMetadata.Add(address, metadata);
        }

        metadata.Kind = SurfaceMetadataKind.HTile;
        metadata.SetFromMask(0);
    }

    internal RegionLockScope HoldLockForTest() => new(_lock);

    internal readonly struct RegionLockScope : IDisposable
    {
        private readonly HLE.GpuMemory.RegionLock _lock;

        public RegionLockScope(HLE.GpuMemory.RegionLock regionLock)
        {
            _lock = regionLock;
            regionLock.Enter();
        }

        public void Dispose() => _lock.Exit();
    }
}

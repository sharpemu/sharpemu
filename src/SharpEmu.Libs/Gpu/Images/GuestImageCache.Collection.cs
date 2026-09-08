// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

// Garbage collection by recency and memory pressure, and the scheduled readback flush.
public sealed partial class GuestImageCache
{
    public void RunGarbageCollector()
    {
        using var held = _lock.Hold();
        var tick = _collectionTick++;
        if (_totalUsedMemory < _collectionStartBytes)
        {
            return;
        }

        Collect(tick, allowAggressive: false);
        if (_totalUsedMemory >= _criticalMemoryBytes)
        {
            Collect(tick, allowAggressive: true);
        }
    }

    private void Collect(ulong tick, bool allowAggressive)
    {
        var pressured = _totalUsedMemory >= _memoryPressureBytes;
        var aggressive = allowAggressive && _totalUsedMemory >= _criticalMemoryBytes;
        var age = Math.Min(aggressive ? 160UL : pressured ? 80UL : 16UL, tick);
        var deletions = aggressive ? 40 : pressured ? 20 : 10;
        var candidates = new List<ResourceSlotIdentifier>(deletions);
        // Deleting a depth image also deletes its stencil association, so the recency walk ends first.
        _recencyQueue.ForEachItemAtOrBeforeTick(tick - age, imageIdentifier =>
        {
            candidates.Add(imageIdentifier);
            return candidates.Count == deletions;
        });
        foreach (var imageIdentifier in candidates)
        {
            if (deletions == 0)
            {
                break;
            }

            deletions--;
            var owner = _slots.TryGet(imageIdentifier);
            if (owner == null || !owner.Registered || owner.DepthOwner.IsValid)
            {
                continue;
            }

            if (owner.IsGpuModified)
            {
                var safe = CanReadBack(owner);
                if (safe && owner.Description.IsTiled)
                {
                    continue;
                }

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

            DeleteImage(imageIdentifier);
            if (_totalUsedMemory < _criticalMemoryBytes && aggressive)
            {
                deletions >>= 2;
                aggressive = false;
            }

            if (_totalUsedMemory < _memoryPressureBytes && pressured)
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
        return FindImagesInRange(address, size, pageOverlap);
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
        metadata.ClearMask = 0;
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

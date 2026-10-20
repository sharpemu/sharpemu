// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

// Garbage collection by recency and memory pressure, and the scheduled readback flush.
public sealed partial class GuestImageCache
{
    public void RunGarbageCollector() => RunGarbageCollector(endsFrame: true);

    // Image ages are counted in frames. Collection also runs after guest submissions, so memory
    // pressure is relieved during long stretches without a flip, but only a frame advances the
    // age: a title submits dozens of command buffers per frame, and counting those made every
    // texture it samples once per frame look stale, so it was deleted and uploaded again.
    public void RunGarbageCollector(bool endsFrame)
    {
        using var held = _lock.Hold();
        var tick = endsFrame ? _collectionTick++ : _collectionTick;
        if (endsFrame && SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled && tick % 300 == 0)
            ReportImageMemory("collection", true, tick);
        _allocationCollectionBlocked = false;
        if (MemoryUnderPressure)
        {
            // Idle pool memory is kept for reuse until the cache is short of memory.
            ReleaseUnusedMemoryCore();
        }

        if (CollectionMemoryBytes < _collectionStartBytes)
        {
            return;
        }

        Collect(tick, allowAggressive: false, midFrame: !endsFrame);
        if (CollectionMemoryBytes >= _criticalMemoryBytes)
        {
            Collect(tick, allowAggressive: true, midFrame: !endsFrame);
        }
    }

    public void ReleaseUnusedMemory()
    {
        using var held = _lock.Hold();
        ReleaseUnusedMemoryCore();
    }

    // Allocation-time pressure is separate from the periodic sweep: a burst of draws can create
    // thousands of images before the next frame boundary. Destruction still goes through
    // DeleteImage's completion action, so no image is freed while the current tick can use it.
    // Below the hard limit it takes only images neither this frame nor the previous one used
    // (CollectCore): a frame samples again what the previous one did, so evicting those to stay
    // under the critical target recreated, uploaded and read back the whole working set on every
    // frame larger than the target. The flip's collection removes what the frame did not use.
    private void CollectForAllocation(ulong requiredBytes)
    {
        FinishRetiredImagesForAllocation(requiredBytes);
        var current = CollectionMemoryBytes;
        if (requiredBytes > _criticalMemoryBytes || current <= _criticalMemoryBytes - requiredBytes)
        {
            return;
        }

        // A burst can contain only newly touched images. Once the recency walk finds nothing
        // evictable, repeating it for every allocation in the same tick is wasted work; the next
        // collection makes older images eligible. Past the hard limit the previous frame's images
        // are eligible as well, so the walk runs again.
        if (_allocationCollectionBlocked && current < _hardLimitBytes)
        {
            return;
        }

        var target = _criticalMemoryBytes - requiredBytes;
        var blocked = false;
        // Retired images still occupy memory until completion; do not retire the whole old
        // working set merely because that completion is pending.
        while (CollectionMemoryBytes > target)
        {
            var before = _totalUsedMemory;
            Collect(_collectionTick, allowAggressive: true, midFrame: true);
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
        if (Interlocked.Read(ref _retiredImageMemoryBytes) == 0 || !_scheduler.Active || _scheduler.InsideTickCallback)
        {
            return;
        }

        if (!RetirementOverBudget && requiredBytes <= _hardLimitBytes &&
            TotalUsedMemory <= _hardLimitBytes - requiredBytes)
        {
            return;
        }

        // Completion destroys the retired images and can call back into the cache: never wait
        // while holding its lock. Images this preparation touched stay registered; only the
        // previously retired ones are destroyed.
        _lock.Exit();
        try
        {
            ReleaseRetiredImagesCore(RetiredBytesAllowed(requiredBytes));
        }
        finally
        {
            _lock.Enter();
        }

        ReleaseUnusedMemoryCore();
        _allocationCollectionBlocked = false;
    }

    // Waits until the retired images fit the budget again: first the ones whose work already
    // completed, then the oldest ticks, and the recording tick only when it holds the excess.
    // A full drain stalled every draw that came after a tick with many retirements.
    public void ReleaseRetiredImages() => ReleaseRetiredImagesCore(RetiredBytesAllowed(1));

    private void ReleaseRetiredImagesCore(long allowedBytes)
    {
        _scheduler.RunCompletedOperations();
        var retired = Interlocked.Read(ref _retiredImageMemoryBytes);
        if (retired <= allowedBytes)
        {
            return;
        }

        var tick = OldestTickReleasing(retired - allowedBytes);
        if (tick >= _scheduler.CurrentTick)
        {
            _scheduler.Finish();
            return;
        }

        _scheduler.Wait(tick);
        _scheduler.RunCompletedOperations();
    }

    // The retired bytes that leave the registered images and this allocation under the hard
    // limit, and within the retirement budget.
    private long RetiredBytesAllowed(ulong requiredBytes)
    {
        var used = _totalUsedMemory + requiredBytes;
        var room = requiredBytes <= _hardLimitBytes && used <= _hardLimitBytes ? _hardLimitBytes - used : 0;
        return (long)Math.Min((ulong)RetiredBudgetBytes, room);
    }

    private sealed class RetiredTick(ulong tick)
    {
        public ulong Tick { get; } = tick;
        public long Bytes { get; set; }
    }

    private void NoteRetired(ulong tick, long bytes)
    {
        lock (_retiredTicks)
        {
            RetiredTick? entry = null;
            foreach (var candidate in _retiredTicks)
            {
                if (candidate.Tick == tick)
                {
                    entry = candidate;
                    break;
                }
            }

            if (entry is null)
            {
                entry = new RetiredTick(tick);
                _retiredTicks.Enqueue(entry);
            }

            entry.Bytes += bytes;
            while (_retiredTicks.TryPeek(out var oldest) && oldest.Bytes == 0)
            {
                _retiredTicks.Dequeue();
            }
        }
    }

    // The first tick whose completion, with every tick before it, frees at least this many
    // retired bytes; the recording tick when only it does.
    private ulong OldestTickReleasing(long bytes)
    {
        lock (_retiredTicks)
        {
            var released = 0L;
            foreach (var entry in _retiredTicks)
            {
                released += entry.Bytes;
                if (released >= bytes)
                {
                    return entry.Tick;
                }
            }
        }

        return _scheduler.CurrentTick;
    }

    // An allocation failed despite the cache's own accounting: the device is full. Retire every
    // image collection allows, wait for the retired ones to be destroyed and hand the pools'
    // idle memory back to the device.
    public void ReclaimForAllocation()
    {
        using var held = _lock.Hold();
        ReclaimAfterFailedAllocation();
    }

    private void ReclaimAfterFailedAllocation()
    {
        for (var rounds = 0; rounds < 64; rounds++)
        {
            var before = _totalUsedMemory;
            Collect(_collectionTick, allowAggressive: true, reclaim: true);
            if (_totalUsedMemory == before)
            {
                break;
            }
        }

        // Completion destroys the retired images and can call back into the cache, so the wait
        // happens without its lock.
        if (_scheduler.Active && !_scheduler.InsideTickCallback)
        {
            _lock.Exit();
            try
            {
                _scheduler.Finish();
            }
            finally
            {
                _lock.Enter();
            }
        }

        ReleaseUnusedMemoryCore();
    }

    // Idle memory the pools keep for reuse: retired backings, empty image blocks and buffer slab
    // chunks without a live buffer. Registered images and live buffers are untouched.
    private void ReleaseUnusedMemoryCore()
    {
        _backingPool?.ReleaseRetained();
        _imageMemoryPool.ReleaseRetained();
        _device.Slabs.ReleaseUnused();
    }

    private static readonly bool TraceCollection =
        Environment.GetEnvironmentVariable("SHARPEMU_IMAGE_COLLECT_TRACE") == "1";

    private void Collect(ulong tick, bool allowAggressive, bool reclaim = false, bool midFrame = false)
    {
        if (!TraceCollection)
        {
            CollectCore(tick, allowAggressive, reclaim, midFrame);
            return;
        }

        var usedBefore = _totalUsedMemory;
        var countBefore = _slots.Count;
        CollectCore(tick, allowAggressive, reclaim, midFrame);
        Console.Error.WriteLine(
            $"[IMAGE_COLLECT] tick={tick} aggressive={allowAggressive} reclaim={reclaim} midFrame={midFrame} images={countBefore}->{_slots.Count} " +
            $"live={usedBefore}->{_totalUsedMemory} retired={Interlocked.Read(ref _retiredImageMemoryBytes)} " +
            $"pool_unused={_imageMemoryPool.UnusedBytes} pooled_backing={_backingPool?.PooledBytes ?? 0} " +
            $"thresholds={_memoryPressureBytes}/{_criticalMemoryBytes}/{_hardLimitBytes}");
    }

    private const int MaxExaminedPerSweep = 1024;

    private void CollectCore(ulong tick, bool allowAggressive, bool reclaim, bool midFrame)
    {
        var pressured = reclaim || CollectionMemoryBytes >= _memoryPressureBytes;
        var aggressive = reclaim || (allowAggressive && CollectionMemoryBytes >= _criticalMemoryBytes);
        // Under pressure only the images the current frame touched are kept: the work being
        // recorded needs those, and whatever it already references is destroyed after its
        // tick completes. Waiting more frames as memory fills would stop reclamation in a
        // slow title. Mid-frame, though, an image is a frame old only because the rest of the
        // frame has not sampled it yet: the pressured sweep would delete what the next draws use
        // and they would create and upload it again, so it keeps the normal age there; only the
        // critical and allocation passes reclaim the previous frame's images before the flip.
        // The critical and allocation passes in the middle of a frame keep the previous frame's
        // images too (age 2) until the cache reaches its hard limit: those are the frame's working
        // set, and evicting them only moved the recreation a few draws later.
        var profile = SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled;
        var age = !pressured ? 16UL
            : !midFrame ? 1UL
            : !allowAggressive ? 16UL
            : CollectionMemoryBytes >= _hardLimitBytes ? 1UL : 2UL;
        if (tick < age)
        {
            return;
        }

        var deletions = aggressive ? 40 : pressured ? 20 : 10;
        var candidates = new List<ResourceSlotIdentifier>(deletions);
        // The periodic sweep examines a bounded slice per step and resumes where the last one stopped;
        // the pressure and reclaim passes take every aged image at once.
        var bounded = !(reclaim || allowAggressive);
        var chunk = deletions;
        var moreToScan = true;
        var finished = false;
        var examined = 0;
        while (!finished && moreToScan && deletions > 0)
        {
            candidates.Clear();
            // Deleting a depth image also deletes its stencil association, so the recency walk ends first.
            if (bounded)
            {
                moreToScan = _recencyQueue.CollectNextAtOrBeforeTick(tick - age, chunk, candidates);
            }
            else
            {
                moreToScan = false;
                _recencyQueue.ForEachItemAtOrBeforeTick(tick - age, imageIdentifier =>
                {
                    candidates.Add(imageIdentifier);
                    // A protected prefix must not hide older reclaimable images behind it: the walk takes
                    // every candidate and the budget below counts retirements, not candidates visited.
                    return false;
                });
                PreferImagesWithoutGpuContents(candidates);
            }

            foreach (var imageIdentifier in candidates)
            {
                if (deletions <= 0 || (!reclaim && CollectionMemoryBytes < _collectionStartBytes))
                {
                    finished = true;
                    break;
                }

                if (profile) _collectionCandidates++;
                var owner = _slots.TryGet(imageIdentifier);
                if (owner == null || !owner.Registered || owner.DepthOwner.IsValid || _lookupPins.ContainsKey(imageIdentifier))
                {
                    if (profile && owner?.DepthOwner.IsValid == true) _collectionAssociations++;
                    continue;
                }

                if (reclaim && (owner.Binding.IsBound || owner.Binding.IsTarget))
                {
                    // The draw state being built still holds this image's views.
                    continue;
                }

                if (owner.IsGpuModified)
                {
                    if (owner.IsMaybeCpuDirty)
                    {
                        if (owner.NeedsMaybeCpuHash)
                            continue;
                        _ = owner.ResolveMaybeCpuHash(owner.HashGuestEdges());
                    }

                    var safe = CanReadBack(owner);
                    // A dirty buffer overlap does not prove that it replaces the image contents.
                    if (!safe && owner.SafeToDownload)
                    {
                        if (profile) _collectionBufferOverlapRetentions++;
                        continue;
                    }
                    // A tiled image is read back and detiled like a linear one; uploads over its range wait
                    // for that readback, so under pressure it is retired instead of held forever.
                    if (safe && owner.Description.IsTiled && !pressured)
                    {
                        if (profile) _collectionTiledRetentions++;
                        continue;
                    }

                    if (safe && !pressured)
                    {
                        if (profile) _collectionUnpressuredRetentions++;
                        continue;
                    }

                    if (safe && !TryDownloadToGuest(imageIdentifier))
                    {
                        if (profile) _collectionReadbackFailures++;
                        continue;
                    }

                    ReportImageLifetime(owner, safe ? "collect-readback-queued" : "collect-unreadable-gpu", deletion: true);
                    owner.ClearGpuModified();
                }
                else
                {
                    ReportImageLifetime(owner, "collect-no-gpu-ownership", deletion: true);
                }

                deletions -= DeleteImage(imageIdentifier);
                if (profile) _collectionDeletions++;
                if (reclaim)
                {
                    continue;
                }

                if (CollectionMemoryBytes < _criticalMemoryBytes && aggressive)
                {
                    deletions >>= 2;
                    aggressive = false;
                }

                if (pressured && CollectionMemoryBytes < _memoryPressureBytes)
                {
                    // Back under the threshold: stop. The oldest images went first, so what the
                    // next frames sample again stays resident.
                    finished = true;
                    break;
                }
            }

            // A periodic step only passes over so many images; the cursor resumes after them next time.
            examined += candidates.Count;
            if (bounded && examined >= MaxExaminedPerSweep)
            {
                break;
            }
        }
    }

    // Orders the candidates so that images guest memory already holds go first, oldest first in
    // each group. Evicting one costs nothing now and an upload if it comes back; evicting a
    // GPU-written image first reads it back, which waits for the GPU.
    private void PreferImagesWithoutGpuContents(List<ResourceSlotIdentifier> candidates)
    {
        var written = new List<ResourceSlotIdentifier>();
        var kept = 0;
        for (var index = 0; index < candidates.Count; index++)
        {
            var imageIdentifier = candidates[index];
            if (_slots.TryGet(imageIdentifier) is { IsGpuModified: true })
            {
                written.Add(imageIdentifier);
            }
            else
            {
                candidates[kept++] = imageIdentifier;
            }
        }

        candidates.RemoveRange(kept, candidates.Count - kept);
        candidates.AddRange(written);
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
    internal void SetCollectionThresholds(ulong trigger, ulong pressure, ulong critical, ulong tick, ulong hardLimit = 0)
    {
        _collectionStartBytes = trigger;
        _memoryPressureBytes = pressure;
        _criticalMemoryBytes = critical;
        _hardLimitBytes = hardLimit != 0 ? hardLimit : critical;
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
        InvalidateLookups();
        _pageOwners.GetOrCreate(address >> ImagePageOwnerTable.PageBits).Add(imageIdentifier);
    }

    internal bool RemovePageOwner(ulong address, ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        InvalidateLookups();
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

    internal void RegisterHtileMetadataForTest(ulong address, ulong size, uint layers)
    {
        RegisterHtileMetadataForTest(address);
        using var held = _lock.Hold();
        _surfaceMetadata[address].Size = size;
        _surfaceMetadata[address].Layers = layers;
    }

    internal void RegisterHtileMetadataForTest(ulong address)
    {
        using var held = _lock.Hold();
        IncludeMetadataWriteRange(address, 1);
        if (!_surfaceMetadata.TryGetValue(address, out var metadata))
        {
            metadata = new SurfaceMetadata();
            _surfaceMetadata.Add(address, metadata);
        }

        metadata.Kind = SurfaceMetadataKind.HTile;
        metadata.SetFromMask(0);
    }

    internal void RegisterDccMetadataForTest(ulong address, ulong size)
    {
        using var held = _lock.Hold();
        IncludeMetadataWriteRange(address, size);
        _surfaceMetadata[address] = new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc, Size = size };
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

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private readonly Dictionary<string, (ulong Count, ulong Bytes)> _imageCreationTotals = new();
    private ulong _exactImageMatches;
    private ulong _overlapImageMatches;
    private ulong _emptyImageSearches;
    private ulong _unmatchedImageOverlaps;
    private ulong _collectionCandidates;
    private ulong _collectionAssociations;
    private ulong _collectionTiledRetentions;
    private ulong _collectionUnpressuredRetentions;
    private ulong _collectionReadbackFailures;
    private ulong _collectionDeletions;
    private ulong _collectionBufferOverlapRetentions;
    private int _largeImageReports;
    private int _largeImageLookupReports;
    private Dictionary<ulong, ulong>? _imageDeletionHistory;
    private ulong _imageLifetimeSequence;

    private void ReportImageLifetime(CachedImage image, string operation, bool deletion = false)
    {
        if (!SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled || _imageLifetimeSequence >= 4096)
            return;
        var range = image.Description.Data;
        if (ImageDescription.IsEmptyRange(range)) return;
        _imageDeletionHistory ??= new Dictionary<ulong, ulong>();
        var known = _imageDeletionHistory.TryGetValue(range.Address, out var previousDeletion);
        if (!known && (!deletion || _imageDeletionHistory.Count >= 256)) return;
        var sequence = ++_imageLifetimeSequence;
        if (deletion) _imageDeletionHistory[range.Address] = sequence;
        Console.Error.WriteLine($"[GPU][INFO] ImageLifetime sequence={sequence} operation={operation} " +
            $"previous_deletion={previousDeletion} address=0x{range.Address:X} guest_bytes={range.Size} " +
            $"collection_tick={_collectionTick} submission_tick={_scheduler.CurrentTick} " +
            $"format={image.Backing.Format} extent={image.Backing.Extent.Width}x{image.Backing.Extent.Height}x{image.Backing.Extent.Depth} " +
            $"layers={image.Backing.Layers} levels={image.Backing.MipLevels} tiled={image.Description.IsTiled} " +
            $"gpu_modified={image.IsGpuModified} cpu_dirty={image.IsDefinitelyCpuDirty} maybe_cpu_dirty={image.IsMaybeCpuDirty} " +
            $"buffer_modified={image.IsBufferModified} buffer_overlap={_bufferCache.HasGpuDirtyBytes(range.Address, range.Size)} " +
            $"depth_association={image.DepthOwner.IsValid} cpu_write_address=0x{image.LastCpuWriteAddress:X} cpu_write_bytes={image.LastCpuWriteSize}");
        if (sequence == 4096)
            Console.Error.WriteLine("[GPU][INFO] ImageLifetime report limit reached.");
    }

    private void ReportLargeImageCreation(CachedImage image, string path)
    {
        if (!SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled || image.Backing.AllocationSize < 64UL * 1024 * 1024 || _largeImageReports >= 64)
            return;
        _largeImageReports++;
        Console.Error.WriteLine($"[GPU][INFO] ImageMemoryCreateLarge sequence={_largeImageReports} path={path} " +
            $"collection_tick={_collectionTick} address=0x{image.Description.Data.Address:X} guest_bytes={image.Description.Data.Size} " +
            $"allocation_bytes={image.Backing.AllocationSize} type={image.Description.Type} format={image.Backing.Format} " +
            $"extent={image.Backing.Extent.Width}x{image.Backing.Extent.Height}x{image.Backing.Extent.Depth} " +
            $"layers={image.Backing.Layers} levels={image.Backing.MipLevels} samples={image.Backing.Samples} " +
            $"tile={image.Description.TileMode} guest_format={image.Description.GuestFormat}");
    }

    private void ReportImageMemory(string reason, bool includeLargest, ulong? collectionEvaluationTick = null)
    {
        var evaluationTick = collectionEvaluationTick ?? _collectionTick;
        var pressured = _totalUsedMemory >= _memoryPressureBytes;
        var normalAge = Math.Min(pressured ? 80UL : 16UL, evaluationTick);
        var aggressiveAge = Math.Min(160UL, evaluationTick);
        ulong allocated = 0, pending = 0, modified = 0, tiledModified = 0;
        var pendingCount = 0;
        var largest = includeLargest ? new List<CachedImage>() : null;
        _slots.ForEach((_, image) =>
        {
            var bytes = image.Backing.AllocationSize;
            allocated += bytes;
            if (!image.Registered && !ImageDescription.IsEmptyRange(image.Description.Data))
            {
                pending += bytes;
                pendingCount++;
            }
            if (image.IsGpuModified)
            {
                modified += bytes;
                if (image.Description.IsTiled)
                    tiledModified += bytes;
            }
            largest?.Add(image);
        });
        Console.Error.WriteLine($"[GPU][INFO] ImageMemory reason={reason} collection_tick={_collectionTick} " +
            $"live_images={_slots.Count} created={_createdImageCount} destroyed={_createdImageCount - (ulong)_slots.Count} " +
            $"allocated_bytes={allocated} indexed_bytes={_totalUsedMemory} pending_images={pendingCount} pending_bytes={pending} " +
            $"gpu_modified_bytes={modified} tiled_gpu_modified_bytes={tiledModified} " +
            $"device_allocations={_device.LiveAllocations} device_allocation_limit={_device.MaxMemoryAllocationCount} " +
            $"collection_start_bytes={_collectionStartBytes} collection_enabled={_totalUsedMemory >= _collectionStartBytes} " +
            $"pressure_bytes={_memoryPressureBytes} critical_bytes={_criticalMemoryBytes}");
        Console.Error.WriteLine($"[GPU][INFO] ImageMemoryPaths exact_matches={_exactImageMatches} overlap_matches={_overlapImageMatches} " +
            $"empty_searches={_emptyImageSearches} unmatched_overlaps={_unmatchedImageOverlaps} candidates={_collectionCandidates} " +
            $"association_skips={_collectionAssociations} tiled_retentions={_collectionTiledRetentions} buffer_overlap_retentions={_collectionBufferOverlapRetentions} " +
            $"unpressured_retentions={_collectionUnpressuredRetentions} readback_failures={_collectionReadbackFailures} deletions={_collectionDeletions}");
        foreach (var (path, totals) in _imageCreationTotals)
            Console.Error.WriteLine($"[GPU][INFO] ImageMemoryCreated path={path} count={totals.Count} bytes={totals.Bytes}");
        if (largest == null)
            return;
        largest.Sort((left, right) => right.Backing.AllocationSize.CompareTo(left.Backing.AllocationSize));
        foreach (var image in largest.Take(8))
        {
            var lastUse = image.Registered ? _recencyQueue.GetLastUseTick(image.RecencyEntryIndex) : 0;
            var age = evaluationTick - Math.Min(evaluationTick, lastUse);
            var normalEligible = image.Registered && lastUse <= evaluationTick - normalAge;
            var aggressiveEligible = image.Registered && _totalUsedMemory >= _criticalMemoryBytes &&
                lastUse <= evaluationTick - aggressiveAge;
            var retention = !image.Registered
                ? ImageDescription.IsEmptyRange(image.Description.Data) ? "placeholder" : "pending-release"
                : image.DepthOwner.IsValid ? "depth-association"
                : image.IsGpuModified && image.SafeToDownload
                    ? !CanReadBack(image) ? "dirty-buffer-overlap"
                    : image.Description.IsTiled ? "tiled-gpu-content"
                    : !pressured ? "gpu-content-no-pressure" : "requires-readback"
                : "removal-candidate";
            Console.Error.WriteLine($"[GPU][INFO] ImageMemoryLargest address=0x{image.Description.Data.Address:X} " +
                $"bytes={image.Backing.AllocationSize} format={image.Backing.Format} " +
                $"extent={image.Backing.Extent.Width}x{image.Backing.Extent.Height}x{image.Backing.Extent.Depth} " +
                $"layers={image.Backing.Layers} levels={image.Backing.MipLevels} samples={image.Backing.Samples} guest_bytes={image.Description.Data.Size} " +
                $"registered={image.Registered} gpu_modified={image.IsGpuModified} tiled={image.Description.IsTiled} " +
                $"depth_association={image.DepthOwner.IsValid} last_access_tick={image.LastAccessTick} " +
                $"collection_evaluation_tick={evaluationTick} collection_last_use={lastUse} collection_age={age} " +
                $"normal_age_threshold={normalAge} aggressive_age_threshold={aggressiveAge} " +
                $"normal_age_eligible={normalEligible} aggressive_age_eligible={aggressiveEligible} retention={retention}");
        }
    }
}

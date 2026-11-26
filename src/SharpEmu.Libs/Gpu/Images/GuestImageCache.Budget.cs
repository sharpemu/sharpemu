// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private long _nextBudgetSample;
    private bool _hasBudget;
    private bool _nearBudget;
    private ulong _lastCollectionCreation;
    private long _pendingBudgetBytes;
    private List<ResourceSlotIdentifier>? _budgetCandidates;

    private long _nextIdleSweep;

    internal (ulong Scratch, ulong Images) TrimExpiredPools(long now)
    {
        if (now < _nextIdleSweep) return default;
        _nextIdleSweep = now + 500;
        // Only completed returns enter these pools. Keep recent bursts.
        var cutoff = now - 10_000;
        return (_tiler.TrimIdleScratch(null, 64 * MiB, cutoff, 2),
            _backingPool?.Trim(null, 64 * MiB, cutoff, 4) ?? 0);
    }

    private bool ShouldScanImages(ulong tick, bool endsFrame)
    {
        _device.Residency?.Advance((long)tick);
        var now = Environment.TickCount64;
        var sampled = now >= _nextBudgetSample;
        if (sampled)
        {
            _nextBudgetSample = now + 500;
            var (budget, usage) = SelectBudget(_device.ReadLocalBudget(), _device.DxgiBudget?.Read() ?? default);
            _hasBudget = budget != 0;
            var target = BudgetReclaimTarget(budget, usage, ref _nearBudget);
            var heap = _device.LocalBudgetHeap;
            if (target > 0)
            {
                // Completions may already cover the shortage. Recheck the driver on the next sample.
                var pending = (ulong)Volatile.Read(ref _pendingBudgetBytes);
                target -= Math.Min(target, pending);
                target = Math.Min(target, 64 * MiB);
                var freed = target != 0 ? ReleaseIdleMemory(heap, target, now - 100, 16) : 0;
                if (freed < target)
                    RetireColdTextures(heap, target - freed, tick);
            }
        }

        return NeedsRecencyScan(_hasBudget && !_nearBudget, endsFrame, sampled, _createdImageCount, ref _lastCollectionCreation);
    }

    // Compare headroom to the near-budget watermark for each API's own usage and budget.
    internal static (ulong Budget, ulong Usage) SelectBudget((ulong Budget, ulong Usage) vk,
        (ulong Budget, ulong Usage) dxgi)
    {
        static double Headroom((ulong Budget, ulong Usage) sample) =>
            (double)(sample.Budget - Math.Min(512 * MiB, sample.Budget / 16)) - sample.Usage;
        return dxgi.Budget != 0 && (vk.Budget == 0 || Headroom(dxgi) < Headroom(vk)) ? dxgi : vk;
    }

    // Submissions in one frame have the same recency cutoff. Respond to growth even before the next flip.
    internal static bool NeedsRecencyScan(bool hasHeadroom, bool endsFrame, bool sampled, ulong created, ref ulong lastCreation)
    {
        var scan = !hasHeadroom || endsFrame || sampled || created != lastCreation;
        lastCreation = created;
        return scan;
    }

    internal static ulong BudgetReclaimTarget(ulong budget, ulong usage, ref bool near)
    {
        var margin = Math.Min(512 * MiB, budget / 16);
        var recoveryUsage = budget - 2 * margin;
        near = budget != 0 && usage > (near ? recoveryUsage : budget - margin);
        return near ? usage - recoveryUsage : 0;
    }

    // Allocation retries may run on other threads. These pools contain only completed GPU work.
    private ulong ReleaseIdleMemoryOnAllocationFailure(uint heap, ulong bytes) => ReleaseIdleMemory(heap, bytes, long.MaxValue);

    private ulong ReleaseIdleMemory(uint heap, ulong bytes, long returnedThrough, int limitPerPool = int.MaxValue)
    {
        var freed = _backingPool?.Trim(heap, bytes, returnedThrough, limitPerPool) ?? 0;
        if (freed < bytes)
            freed += _tiler.TrimIdleScratch(heap, bytes - freed, returnedThrough, limitPerPool);
        return freed;
    }

    internal ulong RetireColdTextures(uint heap, ulong target, ulong tick)
    {
        const ulong minimumAge = 16;
        if (tick < minimumAge || target == 0)
            return 0;
        _budgetCandidates ??= new(20);
        _budgetCandidates.Clear();
        _recencyQueue.CollectNextBudgetCandidates(tick - minimumAge, 20, _budgetCandidates);
        ulong scheduled = 0;
        foreach (var id in _budgetCandidates)
        {
            var image = _slots.TryGet(id);
            if (image is not { Registered: true, Parked: false, IsGpuModified: false, IsBufferModified: false, HeapChanged: null, DimensionTwin: null } ||
                !image.Backing.Exists || image.Backing.MemoryHeap != heap || image.DepthOwner.IsValid ||
                image.Description.IsDepth || image.Description.HasStencil || image.Description.HasMetadata ||
                image.Description.Metadata.Compression != DisplayCompression.Uncompressed || image.Uses.Storage ||
                image.Uses.RenderTarget || image.Uses.DepthTarget || image.Uses.VideoOut || image.Binding.IsBound || image.Binding.IsTarget ||
                !_backing.IsBackedRange(image.Description.Data.Address, image.Description.Data.Size))
                continue;

            // The guest or its buffer still owns the contents. Use the normal unwatch and deferred destruction.
            scheduled += image.Backing.AllocationSize;
            ReportImageLifetime(image, "collect-budget-clean", deletion: true);
            DeleteImage(id, discardBacking: true);
            if (scheduled >= target)
                break;
        }
        return scheduled;
    }
}

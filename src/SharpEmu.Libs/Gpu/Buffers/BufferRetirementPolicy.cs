// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

internal readonly record struct BufferRetirementPlan(
    ulong LatestEligibleTick, int MaximumBufferCount, bool DownloadDirtyBuffers);

// Collection ticks measure cache activity, not elapsed time.
internal sealed class BufferRetirementPolicy
{
    private ulong _collectionThreshold = 1024UL * 1024 * 1024;
    private ulong _criticalThreshold = 2048UL * 1024 * 1024;

    public ulong CurrentTick { get; private set; }

    public void SetThresholds(ulong collectionThreshold, ulong criticalThreshold)
    {
        _collectionThreshold = collectionThreshold;
        _criticalThreshold = criticalThreshold;
    }

    public bool TryBeginCollection(ulong usedMemory, out BufferRetirementPlan plan)
    {
        var tick = CurrentTick++;
        plan = default;
        if (usedMemory < _collectionThreshold)
        {
            return false;
        }

        var downloadDirtyBuffers = usedMemory >= _criticalThreshold;
        var minimumAge = downloadDirtyBuffers ? 80UL : 160UL;
        plan = new BufferRetirementPlan(
            tick - Math.Min(minimumAge, tick), downloadDirtyBuffers ? 64 : 32, downloadDirtyBuffers);
        return true;
    }
}

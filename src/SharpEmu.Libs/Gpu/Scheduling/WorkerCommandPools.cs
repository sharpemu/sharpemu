// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Libs.Gpu.Scheduling;

// Only the recording worker allocates, frees or trims its pools. The submitting thread
// stamps the existing ring and publishes the shell to the same bag as before.
internal sealed class WorkerCommandPools(SubmissionScheduler scheduler, IRenderingState rendering)
{
    private const int IdleMilliseconds = 5000;
    private IGpuCommandBufferSource? _shared;
    private int _sharedShells;
    private long _nextSweep;
    private readonly List<RecordingBuffer> _retiring = [];
    private readonly List<RecordingBuffer> _recent = [];

    internal RecordingBuffer Take(ConcurrentBag<RecordingBuffer> spare, long now)
    {
        Sweep(spare, now);
        if (spare.TryTake(out var available))
        {
            available.LastWorkerUse = now;
            return available;
        }
        _shared ??= scheduler.RecordingDevice.CreateBufferSource();
        _sharedShells++;
        return new RecordingBuffer(scheduler, _shared, rendering, compact: true) { LastWorkerUse = now };
    }

    internal bool NeedsSweep => _shared is not null || _retiring.Count != 0;

    internal void Sweep(ConcurrentBag<RecordingBuffer> spare, long now)
    {
        if (now < _nextSweep) return;
        _nextSweep = now + 1000;
        var freed = 0;
        while (spare.TryTake(out var idle))
        {
            if (now - idle.LastWorkerUse >= IdleMilliseconds)
                _retiring.Add(idle);
            else
            {
                freed += idle.Ring.TrimIdle(now - IdleMilliseconds);
                _recent.Add(idle);
            }
        }
        foreach (var buffer in _recent) spare.Add(buffer);
        _recent.Clear();
        if (freed != 0) _shared!.Trim();
        Reclaim();
    }

    private void Reclaim()
    {
        if (_retiring.Count == 0) return;
        scheduler.Timeline.RefreshCompletedTick();
        var freedShared = false;
        for (var i = _retiring.Count - 1; i >= 0; i--)
        {
            var shell = _retiring[i];
            if (!shell.Ring.Completed) continue;
            shell.Ring.FreeCompleted();
            _sharedShells--;
            freedShared = true;
            _retiring.RemoveAt(i);
        }
        if (freedShared && _sharedShells == 0)
        {
            scheduler.RecordingDevice.ReleaseBufferSource(_shared!);
            _shared = null;
        }
        else if (freedShared) _shared!.Trim();
    }
}

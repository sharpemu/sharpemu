// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class TickedBufferRing
{
    private const int GrowStep = 4;
    private const ulong InUse = ulong.MaxValue;

    private readonly IGpuCommandBufferSource _source;
    private readonly TickTimeline _timeline;
    private readonly List<nint> _buffers = new();
    private readonly List<ulong> _ticks = new();
    private int _hint;
    internal bool Compact { get; init; }
    private List<long>? _usedAt;
    internal int TrimIdle(long cutoff)
    {
        if (_usedAt is null) return 0;
        var removed = 0;
        var complete = _timeline.CompletedTick;
        // Retain the first slot so steady single-buffer recordings stay allocation-free.
        for (var i = _buffers.Count - 1; i > 0; i--)
        {
            if (_ticks[i] > complete || _usedAt[i] > cutoff) continue;
            _source.FreeBuffer(_buffers[i]);
            _buffers.RemoveAt(i);
            _ticks.RemoveAt(i);
            _usedAt.RemoveAt(i);
            removed++;
        }
        return removed;
    }
    internal bool Completed
    {
        get
        {
            var completed = _timeline.CompletedTick;
            foreach (var tick in _ticks)
                if (tick > completed) return false;
            return true;
        }
    }

    internal void FreeCompleted()
    {
        if (!Completed) throw new InvalidOperationException("Command buffers are still pending.");
        foreach (var buffer in _buffers) _source.FreeBuffer(buffer);
        _buffers.Clear();
        _ticks.Clear();
        _usedAt?.Clear();
        _hint = 0;
    }

    public TickedBufferRing(IGpuCommandBufferSource source, TickTimeline timeline)
    {
        _source = source;
        _timeline = timeline;
    }

    public int Count => _buffers.Count;

    // Reuse a buffer whose submission completed or allocate more; the buffer stays in use until
    // Submitted stamps it with the tick that carries it.
    public nint AcquireBuffer()
    {
        var gpuTick = _timeline.CompletedTick;
        var found = FindReusableBuffer(_hint, _ticks.Count, gpuTick);
        if (found < 0)
        {
            _timeline.RefreshCompletedTick();
            gpuTick = _timeline.CompletedTick;
            found = FindReusableBuffer(_hint, _ticks.Count, gpuTick);
        }

        if (found < 0)
        {
            found = FindReusableBuffer(0, _hint, gpuTick);
        }

        if (found < 0)
        {
            found = AllocateMoreBuffers();
        }

        _ticks[found] = InUse;
        _hint = found + 1;
        if (_hint == _ticks.Count)
        {
            _hint = 0;
        }

        return _buffers[found];
    }

    internal nint AcquireWorkerHandle()
    {
        if (!Compact) return AcquireBuffer();
        var slot = FindReusableBuffer(0, Count, _timeline.CompletedTick);
        if (slot < 0)
        {
            _timeline.RefreshCompletedTick();
            slot = FindReusableBuffer(0, Count, _timeline.CompletedTick);
        }
        if (slot < 0)
        {
            slot = Count;
            _buffers.Add(_source.AllocateBuffers(1)[0]);
            _ticks.Add(0);
            (_usedAt ??= []).Add(0);
        }
        _ticks[slot] = InUse;
        _usedAt![slot] = Environment.TickCount64;
        return _buffers[slot];
    }

    public void Submitted(nint buffer, ulong tick)
    {
        var index = _buffers.IndexOf(buffer);
        if (index < 0 || _ticks[index] != InUse)
        {
            throw SubmissionScheduler.Fatal($"The submitted command buffer is not in use: buffer=0x{buffer:X} tick={tick}.");
        }

        _ticks[index] = tick;
    }

    private int FindReusableBuffer(int begin, int end, ulong gpuTick)
    {
        for (var index = begin; index < end; index++)
        {
            if (gpuTick >= _ticks[index])
            {
                return index;
            }
        }

        return -1;
    }

    private int AllocateMoreBuffers()
    {
        var first = _buffers.Count;
        _buffers.AddRange(_source.AllocateBuffers(GrowStep));
        _ticks.AddRange(new ulong[GrowStep]);
        return first;
    }
}

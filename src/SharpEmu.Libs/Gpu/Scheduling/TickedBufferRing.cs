// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class TickedBufferRing
{
    private const int GrowStep = 4;

    private readonly IGpuTickDevice _device;
    private readonly TickTimeline _timeline;
    private readonly List<nint> _buffers = new();
    private readonly List<ulong> _ticks = new();
    private int _hint;

    public TickedBufferRing(IGpuTickDevice device, TickTimeline timeline)
    {
        _device = device;
        _timeline = timeline;
    }

    public int Count => _buffers.Count;

    // Reuse a completed buffer or allocate more buffers. Mark it with the current tick without waiting.
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
            _ticks[found] = _timeline.CurrentTick;
        }

        _hint = found + 1;
        if (_hint == _ticks.Count)
        {
            _hint = 0;
        }

        return _buffers[found];
    }

    private int FindReusableBuffer(int begin, int end, ulong gpuTick)
    {
        for (var index = begin; index < end; index++)
        {
            if (gpuTick >= _ticks[index])
            {
                _ticks[index] = _timeline.CurrentTick;
                return index;
            }
        }

        return -1;
    }

    private int AllocateMoreBuffers()
    {
        var first = _buffers.Count;
        _buffers.AddRange(_device.AllocateBuffers(GrowStep));
        _ticks.AddRange(new ulong[GrowStep]);
        return first;
    }
}

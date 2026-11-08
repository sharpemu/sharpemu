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

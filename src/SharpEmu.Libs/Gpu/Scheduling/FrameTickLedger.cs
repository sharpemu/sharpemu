// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class FrameTickLedger
{
    public const int Capacity = 64;

    private readonly object _gate = new();
    private readonly ulong[] _lastTicks = new ulong[Capacity];
    private long _latestFrame;

    public long LatestSubmittedFrame => Volatile.Read(ref _latestFrame);

    public long NoteFrameSubmitted(ulong lastTick)
    {
        lock (_gate)
        {
            var frame = _latestFrame + 1;
            _lastTicks[frame % Capacity] = Math.Max(lastTick, LastTickOf(_latestFrame));
            Volatile.Write(ref _latestFrame, frame);
            return frame;
        }
    }

    public long FrameOf(ulong tick)
    {
        lock (_gate)
        {
            var frame = _latestFrame;
            if (frame == 0 || LastTickOf(frame) < tick)
            {
                return frame + 1;
            }

            var oldest = OldestKnownFrame();
            while (frame > oldest && LastTickOf(frame - 1) >= tick)
            {
                frame--;
            }

            return frame;
        }
    }

    public long CompletedFrame(ulong completedTick)
    {
        lock (_gate)
        {
            var frame = _latestFrame;
            var oldest = OldestKnownFrame();
            while (frame >= oldest && frame != 0 && LastTickOf(frame) > completedTick)
            {
                frame--;
            }

            return frame;
        }
    }

    private long OldestKnownFrame() => Math.Max(1, _latestFrame - Capacity + 1);

    private ulong LastTickOf(long frame) => frame == 0 ? 0 : _lastTicks[frame % Capacity];
}

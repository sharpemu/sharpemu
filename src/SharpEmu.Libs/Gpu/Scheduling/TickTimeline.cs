// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class TickTimeline
{
    private readonly IGpuTickDevice _device;
    private ulong _gpuTick;
    private ulong _currentTick = 1;

    public TickTimeline(IGpuTickDevice device) => _device = device;

    public ulong CurrentTick => Volatile.Read(ref _currentTick);

    public ulong CompletedTick => Volatile.Read(ref _gpuTick);

    public ulong Handle => _device.TimelineHandle;

    public bool IsTickComplete(ulong tick) => CompletedTick >= tick;

    public ulong ReserveTick() => Interlocked.Increment(ref _currentTick) - 1;

    public void RefreshCompletedTick()
    {
        var counter = _device.ReadTimeline();
        var known = Volatile.Read(ref _gpuTick);
        while (known < counter)
        {
            var seen = Interlocked.CompareExchange(ref _gpuTick, counter, known);
            if (seen == known)
            {
                return;
            }

            known = seen;
        }
    }

    public void Wait(ulong tick)
    {
        if (IsTickComplete(tick))
        {
            return;
        }

        RefreshCompletedTick();
        if (IsTickComplete(tick))
        {
            return;
        }

        if (!_device.TryWaitTimeline(tick, out var failure))
        {
            throw SubmissionScheduler.Fatal($"vkWaitSemaphores failed: {failure}, tick={tick}");
        }

        RefreshCompletedTick();
    }
}

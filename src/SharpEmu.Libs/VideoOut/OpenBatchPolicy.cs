// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// The command thread hands its work to the render thread in small pieces, so the render thread empties its queue
// many times a frame, and it submitted its batch each time (about 290 submissions a frame in a UE5 scene, 10 ms of
// the render thread). The batch stays open while the queue refills within a short grace. It goes out when the queue
// stays empty (the command thread may be waiting for the GPU), when a guest queue waits on memory (the batch may
// write the label it waits for), or once the batch is old enough that the GPU should start on it.
internal static class OpenBatchPolicy
{
    public static readonly long GraceTicks = Stopwatch.Frequency / 20_000;
    public static readonly long MaxAgeTicks = Stopwatch.Frequency / 1_000;

    public static bool KeepOpen(long openedTicks, Func<long> clock, Func<bool> hasWork, Func<bool> guestQueueWaits)
    {
        var now = clock();
        if (now - openedTicks >= MaxAgeTicks || guestQueueWaits())
        {
            return false;
        }

        var until = now + GraceTicks;
        var spinner = new SpinWait();
        while (!hasWork())
        {
            if (clock() >= until)
            {
                return false;
            }

            spinner.SpinOnce(-1);
        }

        return !guestQueueWaits();
    }
}

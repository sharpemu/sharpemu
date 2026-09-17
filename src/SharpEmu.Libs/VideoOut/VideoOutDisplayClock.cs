// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// Callers hold the port-state lock when they advance or read the clock.
internal sealed class VideoOutDisplayClock(long openedAt, ulong processCounter, ulong timestampCounter, ulong timestampFrequency)
{
    public ulong Count { get; private set; }
    public long LastTimestamp { get; private set; }
    public ulong ProcessCounter { get; private set; }
    public ulong TimestampCounter { get; private set; }
    public ulong ProcessMicroseconds => (ulong)((UInt128)ProcessCounter * 1_000_000 / (ulong)Stopwatch.Frequency);

    public void Advance(long timestamp, uint refreshRate)
    {
        var interval = RefreshInterval(refreshRate);
        var count = (ulong)(Math.Max(0, timestamp - openedAt) / interval);
        if (count <= Count) return;
        Count = count;
        var elapsed = (ulong)interval * count;
        LastTimestamp = openedAt + (long)elapsed;
        ProcessCounter = processCounter + elapsed;
        TimestampCounter = timestampCounter + (ulong)((UInt128)elapsed * timestampFrequency / (ulong)Stopwatch.Frequency);
    }

    public long NextTimestamp(uint refreshRate) => openedAt + checked((long)(Count + 1) * RefreshInterval(refreshRate));

    internal static long RefreshInterval(uint refreshRate) => Math.Max(1, Stopwatch.Frequency / Math.Max(1L, refreshRate));
}

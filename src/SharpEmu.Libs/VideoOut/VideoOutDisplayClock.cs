// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// Callers hold the port-state lock when they advance or read the clock.
internal sealed class VideoOutDisplayClock(long openedAt, ulong processCounter, ulong timestampCounter, ulong timestampFrequency)
{
    private const int FlipModeAsap = 2;
    private const int FlipModeWindow = 3;
    internal const int FlipModeVsyncMultiple = 4;
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

    internal static long NextFlipTimestamp(long openedAt, long lastPresentedAt, long timestamp,
        uint refreshRate, int flipRate, int flipMode, uint height, int windowTop, int windowBottom, long readyAt)
    {
        if (flipMode == FlipModeAsap) return timestamp;
        if (flipMode == FlipModeVsyncMultiple)
        {
            // All flips ready in one interval share its next boundary.
            // Keep readiness fixed so polling cannot move the deadline.
            var refreshInterval = RefreshInterval(refreshRate);
            var readyInterval = Math.Max(0, readyAt - openedAt) / refreshInterval;
            return checked(openedAt + (readyInterval + 1) * refreshInterval);
        }
        if (lastPresentedAt < 0 && flipMode != FlipModeWindow) return timestamp;
        var interval = RefreshInterval(refreshRate);
        var flipInterval = interval * Math.Max(1L, (long)flipRate + 1);
        var previousInterval = Math.Max(0, lastPresentedAt - openedAt) / flipInterval;
        var next = openedAt + (previousInterval + 1) * flipInterval;
        if (flipMode != FlipModeWindow || height == 0) return Math.Max(timestamp, next);

        // Window margins are scanline positions at the top and bottom of the image.
        var topEnd = interval * Math.Clamp((long)windowTop, 0, height) / height;
        var bottomStart = interval * Math.Clamp((long)windowBottom, 0, height) / height;
        if (topEnd == 0 && bottomStart == interval)
            return lastPresentedAt < 0 ? timestamp : Math.Max(timestamp, next);
        var windowLead = interval - bottomStart;
        var previousWindow = lastPresentedAt < 0
            ? -1
            : (Math.Max(0, lastPresentedAt - openedAt) + windowLead) / flipInterval;
        var windowStart = openedAt + (previousWindow + 1) * flipInterval - windowLead;
        if (timestamp < windowStart) return windowStart;
        var phase = Math.Max(0, timestamp - openedAt) % interval;
        return phase <= topEnd || phase >= bottomStart
            ? timestamp
            : timestamp - phase + bottomStart;
    }
}

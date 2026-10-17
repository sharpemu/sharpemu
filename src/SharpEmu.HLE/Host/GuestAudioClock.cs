// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.Host;

/// <summary>
/// How much guest audio the host device has actually played, in seconds.
///
/// This is the only clock in the emulator that advances at the rate the player
/// hears. Wall clock runs ahead of it whenever the guest cannot feed the device
/// (the stream underruns and the missing time is never played), so anything
/// that has to stay in step with the guest's audio — host-decoded video being
/// the case that matters — has to follow this rather than <see cref="Stopwatch"/>.
///
/// Reported per stream and kept as the furthest-along value: the guest's ports
/// all carry one mix, and the leading port is the one whose position the
/// listener perceives.
/// </summary>
public static class GuestAudioClock
{
    private static long _playedMicroseconds;
    private static long _lastAdvanceTimestamp;

    /// <summary>Seconds of guest audio the device has played. Monotonic.</summary>
    public static double PlayedSeconds =>
        Interlocked.Read(ref _playedMicroseconds) / 1_000_000.0;

    /// <summary>
    /// True while a stream has reported progress recently. False means no guest
    /// audio is playing, and callers must fall back to wall clock rather than
    /// stalling on a clock that will never advance.
    /// </summary>
    public static bool IsRunning
    {
        get
        {
            var last = Interlocked.Read(ref _lastAdvanceTimestamp);
            return last != 0 &&
                   Stopwatch.GetElapsedTime(last) < TimeSpan.FromMilliseconds(250);
        }
    }

    public static void Report(double playedSeconds)
    {
        if (double.IsNaN(playedSeconds) || playedSeconds < 0)
        {
            return;
        }

        var microseconds = (long)(playedSeconds * 1_000_000.0);
        var current = Interlocked.Read(ref _playedMicroseconds);
        while (microseconds > current)
        {
            var seen = Interlocked.CompareExchange(
                ref _playedMicroseconds,
                microseconds,
                current);
            if (seen == current)
            {
                Interlocked.Exchange(ref _lastAdvanceTimestamp, Stopwatch.GetTimestamp());
                return;
            }

            current = seen;
        }
    }
}

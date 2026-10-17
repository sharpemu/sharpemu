// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.VideoOut;

public static class FlipProgressTracker
{
    private static long _lastFlipTimestamp;
    private static long _lastFlipVersion;
    private static int _hasFlipped;

    public static void RecordFlip(long version)
    {
        Volatile.Write(ref _lastFlipVersion, version);
        Volatile.Write(ref _lastFlipTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _hasFlipped, 1);
    }

    public static bool HasFlipped => Volatile.Read(ref _hasFlipped) != 0;

    public static long LastFlipVersion => Volatile.Read(ref _lastFlipVersion);

    public static double? SecondsSinceLastFlip()
    {
        if (Volatile.Read(ref _hasFlipped) == 0)
        {
            return null;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - Volatile.Read(ref _lastFlipTimestamp);
        return elapsedTicks / (double)Stopwatch.Frequency;
    }
}

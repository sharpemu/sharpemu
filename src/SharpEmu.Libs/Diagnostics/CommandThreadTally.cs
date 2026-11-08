// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

internal static class CommandThreadTally
{
    public static readonly bool Enabled = VideoOut.RenderPhaseProfile.Enabled;

    private static long _slices;
    private static long _sliceTicks;
    private static long _idleTicks;
    private static long _blockedTicks;
    private static long _spaceWaits;
    private static long _spaceWaitTicks;
    private static long _pendingWaits;
    private static long _pendingWaitTicks;
    private static long _roundTrips;
    private static long _roundTripTicks;
    private static long _shadowReads;
    private static long _allocated;
    private static long _rangeWaits;
    private static long _rangeWaitTicks;

    public static void NoteSlice(long ticks)
    {
        Interlocked.Increment(ref _slices);
        Interlocked.Add(ref _sliceTicks, ticks);
    }

    public static void NoteIdle(long ticks) => Interlocked.Add(ref _idleTicks, ticks);

    public static void NoteAllocated(long bytes) => Interlocked.Add(ref _allocated, bytes);

    public static void NoteBlocked(long ticks) => Interlocked.Add(ref _blockedTicks, ticks);

    public static void NoteSpaceWait(long ticks)
    {
        Interlocked.Increment(ref _spaceWaits);
        Interlocked.Add(ref _spaceWaitTicks, ticks);
    }

    private static readonly long[] _rangeHits = new long[5];
    private static readonly string[] RangeHitNames = ["fill_small", "fill_large", "copy_small", "copy_large", "lod"];
    private static long _smallFills;
    private static long _smallCopies;

    public static void NoteRangeWait(long ticks, int bucket)
    {
        Interlocked.Increment(ref _rangeWaits);
        Interlocked.Add(ref _rangeWaitTicks, ticks);
        Interlocked.Increment(ref _rangeHits[bucket]);
    }

    public static void NoteSmallTransfer(bool copy)
    {
        if (Enabled)
        {
            Interlocked.Increment(ref copy ? ref _smallCopies : ref _smallFills);
        }
    }

    public static void NotePendingWait(long ticks)
    {
        Interlocked.Increment(ref _pendingWaits);
        Interlocked.Add(ref _pendingWaitTicks, ticks);
    }

    public static void NoteRoundTrip(long ticks)
    {
        Interlocked.Increment(ref _roundTrips);
        Interlocked.Add(ref _roundTripTicks, ticks);
    }

    private static readonly long[] _shadowSources = new long[3];

    public static void NoteShadowRead(int source)
    {
        Interlocked.Increment(ref _shadowReads);
        Interlocked.Increment(ref _shadowSources[source]);
    }

    private static string Ms(ref long ticks) =>
        FormattableString.Invariant($"{Interlocked.Exchange(ref ticks, 0) * 1000.0 / Stopwatch.Frequency:F1}");

    public static string TakeReport() =>
        FormattableString.Invariant($"[PERF][COMMAND_MS] slices={Interlocked.Exchange(ref _slices, 0)} slice_ms={Ms(ref _sliceTicks)} ") +
        FormattableString.Invariant($"idle_ms={Ms(ref _idleTicks)} blocked_ms={Ms(ref _blockedTicks)} ") +
        FormattableString.Invariant($"space_waits={Interlocked.Exchange(ref _spaceWaits, 0)} space_wait_ms={Ms(ref _spaceWaitTicks)} ") +
        FormattableString.Invariant($"pending_waits={Interlocked.Exchange(ref _pendingWaits, 0)} pending_wait_ms={Ms(ref _pendingWaitTicks)} ") +
        FormattableString.Invariant($"range_waits={Interlocked.Exchange(ref _rangeWaits, 0)} range_wait_ms={Ms(ref _rangeWaitTicks)} ") +
        "range_hits=" + string.Join(",", RangeHitNames.Select((name, index) => name + ":" + Interlocked.Exchange(ref _rangeHits[index], 0).ToString(System.Globalization.CultureInfo.InvariantCulture))) + " " +
        FormattableString.Invariant($"small_fills={Interlocked.Exchange(ref _smallFills, 0)} small_copies={Interlocked.Exchange(ref _smallCopies, 0)} ") +
        FormattableString.Invariant($"round_trips={Interlocked.Exchange(ref _roundTrips, 0)} round_trip_ms={Ms(ref _roundTripTicks)} shadow_reads={Interlocked.Exchange(ref _shadowReads, 0)} ") +
        FormattableString.Invariant($"shadow_sources=dispatch:{Interlocked.Exchange(ref _shadowSources[0], 0)},fill:{Interlocked.Exchange(ref _shadowSources[1], 0)},copy:{Interlocked.Exchange(ref _shadowSources[2], 0)} ") +
        FormattableString.Invariant($"alloc_mb={Interlocked.Exchange(ref _allocated, 0) / 1048576.0:F0}");
}

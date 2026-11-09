// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

internal static class ComputeCompileProfile
{
    internal enum Part { StartDelay, SlotWait, CacheLoad, DriverCompile, ConsumeDelay, Count }
    private static readonly long[] Ticks = new long[(int)Part.Count];
    private static readonly long[] Calls = new long[(int)Part.Count];

    internal static long Start() => RenderPhaseProfile.Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Record(Part part, long start)
    {
        if (!RenderPhaseProfile.Enabled || start == 0) return;
        Interlocked.Add(ref Ticks[(int)part], Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref Calls[(int)part]);
    }

    internal static void Report()
    {
        if (!RenderPhaseProfile.Enabled) return;
        var parts = new List<string>();
        for (var index = 0; index < (int)Part.Count; index++)
        {
            var ticks = Interlocked.Read(ref Ticks[index]);
            var calls = Interlocked.Read(ref Calls[index]);
            if (calls != 0)
                parts.Add($"{(Part)index}={ticks * 1000.0 / Stopwatch.Frequency:F3}ms/n{calls}");
        }
        if (parts.Count != 0)
            Console.Error.WriteLine($"[PERF][COMPUTE_COMPILE] cumulative=1 worker_wall_time=1 {string.Join(" ", parts)}");
    }
}

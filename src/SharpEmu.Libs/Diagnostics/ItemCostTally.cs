// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

internal static class ItemCostTally
{
    public const int Dispatch = 0;
    public const int IndirectDraw = 1;
    public const int DirectDraw = 2;
    public const int ResolvedDraw = 3;
    public const int Other = 4;
    public const int Barrier = 5;

    public const int Resolve = 0;
    public const int Record = 1;
    public const int Inline = 2;
    public const int Joined = 3;

    private static readonly string[] KindNames = ["dispatch", "indirect_draw", "direct_draw", "resolved_draw", "other", "barrier"];
    private static readonly string[] StageNames = ["resolve", "record", "inline", "joined"];
    private static readonly ThreadCounter[] Counts = NewCounters();
    private static readonly ThreadCounter[] Ticks = NewCounters();

    private static ThreadCounter[] NewCounters()
    {
        var counters = new ThreadCounter[KindNames.Length * StageNames.Length];
        for (var slot = 0; slot < counters.Length; slot++)
        {
            counters[slot] = new ThreadCounter();
        }

        return counters;
    }

    public static void Note(int kind, int stage, long ticks)
    {
        var slot = kind * StageNames.Length + stage;
        Counts[slot].Increment();
        Ticks[slot].Add(ticks);
    }

    public static string TakeReport()
    {
        var parts = new List<string>();
        for (var kind = 0; kind < KindNames.Length; kind++)
        {
            var stages = new List<string>();
            for (var stage = 0; stage < StageNames.Length; stage++)
            {
                var slot = kind * StageNames.Length + stage;
                var count = Counts[slot].Take();
                var ticks = Ticks[slot].Take();
                if (count == 0)
                {
                    continue;
                }

                stages.Add(stage == Joined
                    ? StageNames[stage] + ":" + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : StageNames[stage] + ":" + FormattableString.Invariant($"{count}/{ticks * 1_000_000.0 / Stopwatch.Frequency / count:F2}us"));
            }

            if (stages.Count != 0)
            {
                parts.Add(KindNames[kind] + "=" + string.Join(",", stages));
            }
        }

        return "item_cost=" + string.Join(" ", parts);
    }
}

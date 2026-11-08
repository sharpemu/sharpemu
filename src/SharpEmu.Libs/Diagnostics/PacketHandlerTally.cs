// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.Diagnostics;

internal static class PacketHandlerTally
{
    public static readonly bool Enabled = VideoOut.RenderPhaseProfile.Enabled;

    [ThreadStatic]
    public static long WaitedTicks;

    private static readonly long[] _opcodeTicks = new long[256];
    private static readonly long[] _opcodeCounts = new long[256];
    private static readonly long[] _customTicks = new long[PacketCustomCode.Count];
    private static readonly long[] _customCounts = new long[PacketCustomCode.Count];
    private static readonly string[] _opcodeNames = Names(typeof(PacketOpcode), 256);
    private static readonly string[] _customNames = Names(typeof(PacketCustomCode), (int)PacketCustomCode.Count);

    public static void Note(uint opcode, uint header, long ticks)
    {
        if (opcode == PacketOpcode.Nop)
        {
            var code = PacketHeader.CustomCode(header);
            Interlocked.Add(ref _customTicks[code], ticks);
            Interlocked.Increment(ref _customCounts[code]);
            return;
        }

        if (opcode != PacketOpcode.IndirectBuffer)
        {
            Interlocked.Add(ref _opcodeTicks[opcode], ticks);
        }

        Interlocked.Increment(ref _opcodeCounts[opcode]);
    }

    public static string TakeReport()
    {
        var parts = new List<(string Name, long Ticks, long Count)>();
        Take(_opcodeTicks, _opcodeCounts, _opcodeNames, string.Empty, parts);
        Take(_customTicks, _customCounts, _customNames, "Nop.", parts);
        parts.Sort(static (left, right) => right.Ticks.CompareTo(left.Ticks));
        var total = parts.Sum(part => part.Ticks) * 1000.0 / Stopwatch.Frequency;
        return FormattableString.Invariant($"[PERF][PACKET_HANDLERS] total_ms={total:F1} ") + RegisterCopyTally.TakeReport() + " " +
            string.Join(" ", parts.Select(part => part.Name + FormattableString.Invariant($"={part.Ticks * 1000.0 / Stopwatch.Frequency:F1}ms/n{part.Count}")));
    }

    private static void Take(long[] ticks, long[] counts, string[] names, string prefix, List<(string Name, long Ticks, long Count)> parts)
    {
        for (var index = 0; index < ticks.Length; index++)
        {
            var count = Interlocked.Exchange(ref counts[index], 0);
            var elapsed = Interlocked.Exchange(ref ticks[index], 0);
            if (count != 0)
            {
                parts.Add((prefix + names[index], elapsed, count));
            }
        }
    }

    private static string[] Names(Type type, int capacity)
    {
        var names = new string[capacity];
        for (var index = 0; index < capacity; index++)
        {
            names[index] = FormattableString.Invariant($"0x{index:X2}");
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && field.GetRawConstantValue() is uint value && value < (uint)capacity)
            {
                names[value] = field.Name;
            }
        }

        return names;
    }
}

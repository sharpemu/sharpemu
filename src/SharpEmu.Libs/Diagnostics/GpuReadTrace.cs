// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Diagnostics;

internal static class GpuReadTrace
{
    private const int MaxReports = 64;

    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GPU_READS") == "1";

    [ThreadStatic]
    public static ulong CurrentShader;

    [ThreadStatic]
    public static string? CurrentStage;

    private static readonly HashSet<(ulong Shader, ulong Page, bool Table)> _reported = new();
    private static readonly Dictionary<ulong, (long Reads, long Changes, uint Last, uint First, ulong Shader)> _values = new();
    private static long _nextValueReport = System.Diagnostics.Stopwatch.GetTimestamp();

    public static void RecordValue(ulong address, uint word)
    {
        lock (_values)
        {
            _values[address] = _values.TryGetValue(address, out var entry)
                ? (entry.Reads + 1, entry.Changes + (entry.Last != word ? 1 : 0), word, entry.First, entry.Shader)
                : (1, 0, word, word, CurrentShader);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _nextValueReport)
            {
                return;
            }

            _nextValueReport = now + System.Diagnostics.Stopwatch.Frequency * 5;
            foreach (var (key, value) in _values.OrderByDescending(pair => pair.Value.Reads).Take(16))
            {
                Console.Error.WriteLine(
                    $"[GPU][SYNC_READ_VALUE] address=0x{key:X16} reads={value.Reads} changes={value.Changes} first=0x{value.First:X8} last=0x{value.Last:X8} shader=0x{value.Shader:X16}");
            }
        }
    }

    public static void Record(ulong address, bool table)
    {
        var key = (CurrentShader, address & ~0xFFFUL, table);
        lock (_reported)
        {
            if (_reported.Count >= MaxReports || !_reported.Add(key))
            {
                return;
            }
        }

        Console.Error.WriteLine(
            $"[GPU][SYNC_READ] shader=0x{CurrentShader:X16} stage={CurrentStage} address=0x{address:X16} table={table}");
    }
}

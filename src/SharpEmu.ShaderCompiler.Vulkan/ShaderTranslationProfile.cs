// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.ShaderCompiler.Vulkan;

public static class ShaderTranslationProfile
{
    internal enum Part { Validation, ContextSetup, Blocks, FlowAnalysis, Declarations, InitialState, Emission, Finalization, Serialization, Count }
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1" ||
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    private static readonly long[] Ticks = new long[(int)Part.Count];
    private static readonly long[] Calls = new long[(int)Part.Count];
    private static long _instructions;
    private static long _blocks;

    internal static void RecordBlocks(int instructions, int blocks)
    {
        if (!Enabled) return;
        Interlocked.Add(ref _instructions, instructions);
        Interlocked.Add(ref _blocks, blocks);
    }

    internal struct Scope
    {
        private Part _part;
        private long _start;
        internal Scope(Part part)
        {
            _part = part;
            _start = Enabled ? Stopwatch.GetTimestamp() : 0;
        }
        internal void Switch(Part part)
        {
            if (_start == 0) return;
            var now = Stopwatch.GetTimestamp();
            Record(now);
            _part = part;
            _start = now;
        }
        private void Record(long now)
        {
            Interlocked.Add(ref Ticks[(int)_part], now - _start);
            Interlocked.Increment(ref Calls[(int)_part]);
        }
        internal void Dispose()
        {
            if (_start == 0) return;
            Record(Stopwatch.GetTimestamp());
            _start = 0;
        }
    }

    public static void Report()
    {
        if (!Enabled) return;
        var parts = new List<string>();
        for (var index = 0; index < (int)Part.Count; index++)
        {
            var calls = Interlocked.Read(ref Calls[index]);
            var ticks = Interlocked.Read(ref Ticks[index]);
            if (calls != 0) parts.Add($"{(Part)index}={ticks * 1000.0 / Stopwatch.Frequency:F3}ms/n{calls}");
        }
        if (parts.Count != 0)
            Console.Error.WriteLine($"[PERF][SHADER_TRANSLATION] cumulative=1 all_compile_threads=1 includes_prewarm=1 " +
                $"instructions={Interlocked.Read(ref _instructions)} blocks={Interlocked.Read(ref _blocks)} {string.Join(" ", parts)}");
    }
}

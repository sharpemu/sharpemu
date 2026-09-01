// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Agc;

internal static class DcbParseProfile
{
    internal enum DrawPhase
    {
        Create,
        Textures,
        GlobalBuffers,
        VertexBuffers,
        TargetData,
        Submit,
    }

    public static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_DCB_PARSE"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _reporting;
    private static long _parseCalls;
    private static long _parseDwords;
    private static long _parseTicks;
    private static long _suspended;
    private static long _drawCalls;
    private static long _drawTicks;
    private static long _dispatchCalls;
    private static long _dispatchTicks;
    private static long _flipCalls;
    private static long _flipTicks;
    private static readonly long[] DrawPhaseCalls = new long[6];
    private static readonly long[] DrawPhaseTicks = new long[6];

    public static void RecordParse(uint dwords, long ticks, bool suspended)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _parseCalls);
        Interlocked.Add(ref _parseDwords, dwords);
        Interlocked.Add(ref _parseTicks, ticks);
        if (suspended)
        {
            Interlocked.Increment(ref _suspended);
        }

        TryReport();
    }

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void RecordDraw(long startedAt) =>
        RecordPhase(ref _drawCalls, ref _drawTicks, startedAt);

    public static void RecordDispatch(long startedAt) =>
        RecordPhase(ref _dispatchCalls, ref _dispatchTicks, startedAt);

    public static void RecordFlip(long startedAt) =>
        RecordPhase(ref _flipCalls, ref _flipTicks, startedAt);

    public static void RecordDrawPhase(DrawPhase phase, long startedAt)
    {
        if (!Enabled)
        {
            return;
        }

        var elapsed = Stopwatch.GetTimestamp() - startedAt;
        Interlocked.Increment(ref DrawPhaseCalls[(int)phase]);
        Interlocked.Add(ref DrawPhaseTicks[(int)phase], elapsed);
    }

    private static void TryReport()
    {
        var now = Stopwatch.GetTimestamp();
        var windowStart = Volatile.Read(ref _windowStart);
        if (now - windowStart < 5L * Stopwatch.Frequency ||
            Interlocked.CompareExchange(ref _reporting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            now = Stopwatch.GetTimestamp();
            windowStart = Interlocked.Exchange(ref _windowStart, now);
            var seconds = Math.Max(
                (now - windowStart) / (double)Stopwatch.Frequency,
                double.Epsilon);
            var parseCalls = Interlocked.Exchange(ref _parseCalls, 0);
            var parseDwords = Interlocked.Exchange(ref _parseDwords, 0);
            var parseTicks = Interlocked.Exchange(ref _parseTicks, 0);
            var suspended = Interlocked.Exchange(ref _suspended, 0);
            var drawCalls = Interlocked.Exchange(ref _drawCalls, 0);
            var drawTicks = Interlocked.Exchange(ref _drawTicks, 0);
            var dispatchCalls = Interlocked.Exchange(ref _dispatchCalls, 0);
            var dispatchTicks = Interlocked.Exchange(ref _dispatchTicks, 0);
            var flipCalls = Interlocked.Exchange(ref _flipCalls, 0);
            var flipTicks = Interlocked.Exchange(ref _flipTicks, 0);
            var drawPhaseCalls = ExchangeAll(DrawPhaseCalls);
            var drawPhaseTicks = ExchangeAll(DrawPhaseTicks);

            var parseMs = ToMilliseconds(parseTicks);
            var drawMs = ToMilliseconds(drawTicks);
            var dispatchMs = ToMilliseconds(dispatchTicks);
            var flipMs = ToMilliseconds(flipTicks);
            var otherMs = Math.Max(0, parseMs - drawMs - dispatchMs - flipMs);
            var createMs = ToMilliseconds(drawPhaseTicks[(int)DrawPhase.Create]);
            var textureMs = ToMilliseconds(drawPhaseTicks[(int)DrawPhase.Textures]);
            var globalMs = ToMilliseconds(drawPhaseTicks[(int)DrawPhase.GlobalBuffers]);
            var vertexMs = ToMilliseconds(drawPhaseTicks[(int)DrawPhase.VertexBuffers]);
            var targetMs = ToMilliseconds(drawPhaseTicks[(int)DrawPhase.TargetData]);
            var submitMs = ToMilliseconds(drawPhaseTicks[(int)DrawPhase.Submit]);
            var profiledDrawMs = createMs + textureMs + globalMs + vertexMs + targetMs + submitMs;
            var unprofiledDrawMs = Math.Max(0, drawMs - profiledDrawMs);
            Console.Error.WriteLine(
                $"[PERF][DCB_PARSE] {seconds:F1}s calls={parseCalls} " +
                $"calls_per_s={parseCalls / seconds:F1} dwords={parseDwords} " +
                $"total_ms={parseMs:F2} avg_ms={(parseCalls == 0 ? 0 : parseMs / parseCalls):F3} " +
                $"suspended={suspended} draw_ms={drawMs:F2}/n{drawCalls} " +
                $"dispatch_ms={dispatchMs:F2}/n{dispatchCalls} " +
                $"flip_ms={flipMs:F2}/n{flipCalls} other_ms={otherMs:F2} " +
                $"draw_create_ms={createMs:F2}/n{drawPhaseCalls[(int)DrawPhase.Create]} " +
                $"draw_texture_ms={textureMs:F2}/n{drawPhaseCalls[(int)DrawPhase.Textures]} " +
                $"draw_global_ms={globalMs:F2}/n{drawPhaseCalls[(int)DrawPhase.GlobalBuffers]} " +
                $"draw_vertex_ms={vertexMs:F2}/n{drawPhaseCalls[(int)DrawPhase.VertexBuffers]} " +
                $"draw_target_ms={targetMs:F2}/n{drawPhaseCalls[(int)DrawPhase.TargetData]} " +
                $"draw_submit_ms={submitMs:F2}/n{drawPhaseCalls[(int)DrawPhase.Submit]} " +
                $"draw_unprofiled_ms={unprofiledDrawMs:F2}");
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }

    private static void RecordPhase(ref long calls, ref long totalTicks, long startedAt)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref calls);
        Interlocked.Add(ref totalTicks, Stopwatch.GetTimestamp() - startedAt);
    }

    private static long[] ExchangeAll(long[] counters)
    {
        var snapshot = new long[counters.Length];
        for (var index = 0; index < counters.Length; index++)
        {
            snapshot[index] = Interlocked.Exchange(ref counters[index], 0);
        }

        return snapshot;
    }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;
}

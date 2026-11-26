// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.HLE.Diagnostics;

/// <summary>
/// SHARPEMU_TRACE_FRAME_TIMELINE=startSeconds,windowSeconds,outputPath records timestamped spans per thread for
/// a short window of a steady run and writes a Chrome trace (chrome://tracing, Perfetto) plus a text table.
/// Outside the window every call is one volatile read. Spans shorter than their threshold are dropped, so what
/// remains is waits and long work: the edges of the critical path.
/// </summary>
public static class FrameTimeline
{
    private sealed class ThreadBuffer
    {
        public int ThreadId;
        public string Name = "";
        public long[] Start = new long[1 << 15];
        public long[] End = new long[1 << 15];
        public int[] Label = new int[1 << 15];
        public int Count;
    }

    public static readonly bool Enabled;

    private static readonly double StartSeconds;
    private static readonly double WindowSeconds;
    private static readonly string OutputPath = "";
    private static readonly ConcurrentBag<ThreadBuffer> Buffers = new();
    private static readonly ConcurrentDictionary<string, int> LabelIds = new();
    private static readonly List<string> Labels = new();
    private static volatile bool _active;
    private static long _windowStart;

    [ThreadStatic]
    private static ThreadBuffer? _mine;

    static FrameTimeline()
    {
        var spec = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FRAME_TIMELINE");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return;
        }

        var parts = spec.Split(',', 3);
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out StartSeconds) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out WindowSeconds))
        {
            return;
        }

        OutputPath = parts[2];
        Enabled = true;
        var controller = new Thread(Control) { IsBackground = true, Name = "FrameTimeline" };
        controller.Start();
    }

    private static void Control()
    {
        Thread.Sleep(TimeSpan.FromSeconds(StartSeconds));
        _windowStart = Stopwatch.GetTimestamp();
        _active = true;
        Thread.Sleep(TimeSpan.FromSeconds(WindowSeconds));
        _active = false;
        Thread.Sleep(200);
        try
        {
            Dump();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[PERF][TIMELINE] dump failed: {exception.Message}");
        }
    }

    /// <summary>Start of a span, or 0 when no window is open.</summary>
    public static long Begin() => _active ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Close a span; dropped when shorter than <paramref name="minMicroseconds"/>.</summary>
    public static void End(long start, string label, int minMicroseconds = 10)
    {
        if (start == 0 || !_active)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if ((now - start) * 1_000_000 < (long)minMicroseconds * Stopwatch.Frequency)
        {
            return;
        }

        var buffer = _mine ??= Register();
        var index = buffer.Count;
        if (index >= buffer.Start.Length)
        {
            return;
        }

        buffer.Start[index] = start;
        buffer.End[index] = now;
        buffer.Label[index] = LabelIds.GetOrAdd(label, Intern);
        buffer.Count = index + 1;
    }

    private static int Intern(string label)
    {
        lock (Labels)
        {
            Labels.Add(label);
            return Labels.Count - 1;
        }
    }

    private static ThreadBuffer Register()
    {
        var buffer = new ThreadBuffer
        {
            ThreadId = Environment.CurrentManagedThreadId,
            Name = Thread.CurrentThread.Name ?? "",
        };
        Buffers.Add(buffer);
        return buffer;
    }

    private static void Dump()
    {
        var json = new StringBuilder("{\"traceEvents\":[");
        var first = true;
        var table = new StringBuilder();
        table.AppendLine("thread\tstart_ms\tdur_ms\tlabel");
        var frequency = (double)Stopwatch.Frequency;
        var events = new List<(long Start, long End, int Thread, string Name, string Label)>();
        foreach (var buffer in Buffers)
        {
            var name = string.IsNullOrEmpty(buffer.Name) ? $"thread-{buffer.ThreadId}" : buffer.Name;
            if (!first) json.Append(',');
            first = false;
            json.Append(CultureInfo.InvariantCulture,
                $"{{\"ph\":\"M\",\"name\":\"thread_name\",\"pid\":1,\"tid\":{buffer.ThreadId},\"args\":{{\"name\":\"{name} ({buffer.ThreadId})\"}}}}");
            for (var index = 0; index < buffer.Count; index++)
            {
                string label;
                lock (Labels)
                {
                    label = Labels[buffer.Label[index]];
                }

                events.Add((buffer.Start[index], buffer.End[index], buffer.ThreadId, name, label));
            }
        }

        events.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        foreach (var e in events)
        {
            var startUs = (e.Start - _windowStart) * 1_000_000.0 / frequency;
            var durUs = (e.End - e.Start) * 1_000_000.0 / frequency;
            json.Append(CultureInfo.InvariantCulture,
                $",{{\"ph\":\"X\",\"name\":\"{e.Label.Replace("\"", "'")}\",\"pid\":1,\"tid\":{e.Thread},\"ts\":{startUs:F1},\"dur\":{durUs:F1}}}");
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{e.Name}({e.Thread})\t{startUs / 1000.0:F3}\t{durUs / 1000.0:F3}\t{e.Label}"));
        }

        json.Append("]}");
        File.WriteAllText(OutputPath, json.ToString());
        File.WriteAllText(Path.ChangeExtension(OutputPath, ".tsv"), table.ToString());
        Console.Error.WriteLine($"[PERF][TIMELINE] wrote {events.Count} spans to {OutputPath}");
    }
}

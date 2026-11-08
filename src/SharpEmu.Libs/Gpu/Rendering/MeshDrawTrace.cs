// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Rendering;

internal static class MeshDrawTrace
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_MESH") == "1";
    [ThreadStatic] private static Scope? _current;
    private static long _nextDraw;
    private static long _nextEvent;
    private static readonly object Gate = new();
    private static readonly EventWindow Events = new(4096, 8192, 32768);
    private static bool _flushed;
    private static readonly (ulong Address, ulong Size, Scope? Owner)[] Ranges = new (ulong, ulong, Scope?)[1024];
    private static int _nextRange;
    private static readonly Dictionary<ulong, (Scope First, Scope Last, long Count)> Commands = new();
    internal static bool Active => Enabled && _current is not null;

    static MeshDrawTrace()
    {
        if (!Enabled) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Flush();
    }

    internal static Scope? Begin(ulong submission, ulong shader, uint stages)
    {
        if (!Enabled || (stages & 0x20u) == 0) return null;
        var scope = new Scope(Interlocked.Increment(ref _nextDraw), submission, shader, _current);
        _current = scope;
        Write("begin", $"stages=0x{stages:X8}");
        return scope;
    }

    internal static void Write(string operation, string details)
    {
        if (!Active) return;
        Write(_current!, operation, details);
    }

    internal static void Range(string operation, ulong address, ulong size, string details, bool remember = false)
    {
        if (!Enabled) return;
        Scope? scope = _current;
        var historical = scope is null;
        lock (Gate)
        {
            if (scope is null)
            {
                for (var offset = 1; offset <= Ranges.Length; offset++)
                {
                    var range = Ranges[(_nextRange - offset + Ranges.Length) % Ranges.Length];
                    if (range.Owner is not null && Overlaps(address, size, range.Address, range.Size))
                    {
                        scope = range.Owner;
                        break;
                    }
                }
            }
            if (scope is null) return;
            if (remember && !historical && size != 0)
            {
                Ranges[_nextRange] = (address, size, scope);
                _nextRange = (_nextRange + 1) % Ranges.Length;
            }
        }
        Write(scope, operation, $"address=0x{address:X16} size=0x{size:X} historicalMatch={historical} {details}");
    }

    internal static bool Overlaps(ulong first, ulong firstSize, ulong second, ulong secondSize) =>
        firstSize != 0 && secondSize != 0 &&
        (first <= second ? second - first < firstSize : first - second < secondSize);

    internal static void RecordedCommand(ulong command)
    {
        if (!Active) return;
        lock (Gate)
        {
            if (Commands.TryGetValue(command, out var entry))
                Commands[command] = (entry.First, _current!, entry.Count + 1);
            else if (Commands.Count < 1024)
                Commands.Add(command, (_current!, _current!, 1));
        }
        Write("command-buffer", $"handle=0x{command:X16}");
    }

    internal static void Submitted(ulong command, ulong tick, bool accepted, string failure)
    {
        if (!Enabled) return;
        (Scope First, Scope Last, long Count) entry;
        lock (Gate)
        {
            if (!Commands.Remove(command, out entry)) return;
        }
        Write(entry.Last, "queue-submit", $"command=0x{command:X16} tick={tick} accepted={accepted} firstDraw={entry.First.Draw} lastDraw={entry.Last.Draw} meshDraws={entry.Count} failure={failure}");
    }

    private static void Write(Scope scope, string operation, string details)
    {
        var key = $"{scope.Shader:X16}:{operation}:{details}";
        (string Line, bool Emit, bool LimitReached) entry;
        lock (Gate)
        {
            entry = Events.Record(key, $"[MESH][TRACE] event={++_nextEvent} thread={Environment.CurrentManagedThreadId} draw={scope.Draw} submission={scope.Submission} shader=0x{scope.Shader:X16} operation={operation} {details}");
        }
        if (entry.Emit) Console.Error.WriteLine(entry.Line);
        if (entry.LimitReached) Console.Error.WriteLine("[MESH][TRACE] Live output limit reached; recent history remains active.");
    }

    private static void Flush()
    {
        string[] history;
        lock (Gate)
        {
            if (_flushed) return;
            _flushed = true;
            history = Events.Recent.ToArray();
        }
        Console.Error.WriteLine("[MESH][HISTORY] Recent mesh events begin.");
        foreach (var line in history) Console.Error.WriteLine(line);
        Console.Error.WriteLine("[MESH][HISTORY] Recent mesh events end.");
    }

    internal sealed class Scope(long draw, ulong submission, ulong shader, Scope? previous) : IDisposable
    {
        internal long Draw => draw;
        internal ulong Submission => submission;
        internal ulong Shader => shader;
        private string _outcome = "returned-or-unwound-before-recording";
        internal void Complete(string outcome) => _outcome = outcome;
        public void Dispose()
        {
            Write("end", $"outcome={_outcome}");
            _current = previous;
        }
    }

    internal static void Recorded() => _current?.Complete("recorded-not-yet-completed");

    internal sealed class EventWindow(int historyCapacity, int keyCapacity, int outputCapacity)
    {
        private readonly Dictionary<string, long> _occurrences = new();
        private readonly Queue<string> _history = new();
        private int _written;
        internal IEnumerable<string> Recent => _history;
        internal int KeyCount => _occurrences.Count;

        internal (string Line, bool Emit, bool LimitReached) Record(string key, string text)
        {
            var count = _occurrences.GetValueOrDefault(key) + 1;
            var tracked = _occurrences.ContainsKey(key) || _occurrences.Count < keyCapacity;
            if (tracked) _occurrences[key] = count;
            var line = $"{text} occurrence={(tracked ? count : 0)}";
            if (_history.Count == historyCapacity) _history.Dequeue();
            _history.Enqueue(line);
            var emit = _written < outputCapacity && (count == 1 || (count & (count - 1)) == 0);
            if (emit) _written++;
            return (line, emit, emit && _written == outputCapacity);
        }
    }
}

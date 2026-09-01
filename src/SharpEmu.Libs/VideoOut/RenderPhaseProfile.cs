// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Self-time accounting for the render thread, enabled with
/// SHARPEMU_PROFILE_RENDER=1 or the unified performance profile. The existing
/// videoout counters report how much work was done (draws, pipelines, SPIR-V)
/// but not where the render thread's second went, which is the number that
/// decides whether a low frame rate is the emulator recording commands, the
/// GPU executing them, or neither.
///
/// Scopes nest: entering a phase suspends the enclosing one and resumes it on
/// dispose, so a <see cref="Phase.QueueSubmit"/> inside
/// <see cref="Phase.Flush"/> is never counted twice.
/// </summary>
internal static class RenderPhaseProfile
{
    internal enum Phase
    {
        /// <summary>Outside any measured phase — loop overhead.</summary>
        Unattributed = 0,
        /// <summary>Parked because no guest work and no newer flip exist.</summary>
        Idle,
        /// <summary>Blocked on the frame slot's fence: the GPU is behind.</summary>
        FrameSlotWait,
        /// <summary>Reaping completed guest submissions (fence polls).</summary>
        Collect,
        Evict,
        /// <summary>Dequeuing the next guest work item.</summary>
        TakeWork,
        /// <summary>Building the diagnostic label for a work item.</summary>
        Describe,
        /// <summary>Publishing a work item's completion to its waiters.</summary>
        CompleteWork,
        /// <summary>Selecting the presentation to show this iteration.</summary>
        TakePresentation,
        Draw,
        Compute,
        ColorClear,
        ImageWrite,
        OrderedAction,
        Flip,
        /// <summary>Closing and submitting the batched guest command buffer.</summary>
        Flush,
        /// <summary>vkQueueSubmit itself.</summary>
        QueueSubmit,
        /// <summary>vkAcquireNextImageKHR.</summary>
        Acquire,
        /// <summary>Recording + submitting the presentation command buffer.</summary>
        Present,
        /// <summary>vkQueuePresentKHR.</summary>
        QueuePresent,
        Count,
    }

    public static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Breaks down the CPU-visible actions which are deliberately serialized
    /// behind guest GPU work. The dedicated and unified performance switches
    /// both opt into this bounded, render-thread-local category accounting.
    /// </summary>
    public static readonly bool OrderedActionDetailsEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_ORDERED_ACTION"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    private static readonly double _reportSeconds =
        double.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER_REPORT_S"),
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? seconds
            : 5.0;

    private static readonly long[] _ticks = new long[(int)Phase.Count];
    private static readonly long[] _entries = new long[(int)Phase.Count];
    private static long _frames;
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static readonly Dictionary<string, OrderedActionStats> _orderedActions =
        new(StringComparer.Ordinal);

    // The render loop is single-threaded, so plain fields are enough and keep
    // the per-scope cost to two timestamp reads.
    [ThreadStatic] private static Phase _current;
    [ThreadStatic] private static long _lastTimestamp;

    internal readonly ref struct Scope
    {
        private readonly Phase _previous;
        private readonly bool _active;

        internal Scope(Phase previous)
        {
            _previous = previous;
            _active = true;
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            Charge(_previous);
        }
    }

    public static Scope Measure(Phase phase)
    {
        if (!Enabled)
        {
            return default;
        }

        var previous = Charge(phase);
        _entries[(int)phase]++;
        return new Scope(previous);
    }

    public static void RecordOrderedAction(string debugName, bool completed)
    {
        if (!OrderedActionDetailsEnabled)
        {
            return;
        }

        var category = GetOrderedActionCategory(debugName);
        ref var stats = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            _orderedActions,
            category,
            out _);
        stats.Executed += completed ? 1 : 0;
        stats.Deferred += completed ? 0 : 1;
    }

    /// <summary>
    /// Closes out the running phase and switches to <paramref name="next"/>,
    /// returning the phase that was running.
    /// </summary>
    private static Phase Charge(Phase next)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = _current;
        if (_lastTimestamp != 0)
        {
            _ticks[(int)previous] += now - _lastTimestamp;
        }

        _lastTimestamp = now;
        _current = next;
        return previous;
    }

    /// <summary>Called once per presented frame; also drives the report.</summary>
    public static void RecordFrame()
    {
        if (!Enabled)
        {
            return;
        }

        _frames++;
        var now = Stopwatch.GetTimestamp();
        var elapsedTicks = now - _windowStart;
        if (elapsedTicks < _reportSeconds * Stopwatch.Frequency)
        {
            return;
        }

        _windowStart = now;
        var seconds = elapsedTicks / (double)Stopwatch.Frequency;
        var frames = _frames;
        _frames = 0;

        var parts = new List<(Phase Phase, double Percent, long Entries)>((int)Phase.Count);
        var accounted = 0L;
        for (var index = 0; index < (int)Phase.Count; index++)
        {
            var phaseTicks = _ticks[index];
            _ticks[index] = 0;
            var entries = _entries[index];
            _entries[index] = 0;
            accounted += phaseTicks;
            if (phaseTicks <= 0)
            {
                continue;
            }

            parts.Add(((Phase)index, phaseTicks * 100.0 / elapsedTicks, entries));
        }

        parts.Sort(static (left, right) => right.Percent.CompareTo(left.Percent));
        Console.Error.WriteLine(
            $"[PERF][RENDER] {seconds:F1}s fps={frames / seconds:F1} " +
            $"covered={accounted * 100.0 / elapsedTicks:F0}% " +
            string.Join(
                " ",
                parts.Select(part =>
                    $"{part.Phase}={part.Percent:F1}%" +
                    (part.Entries > 0 ? $"/n{part.Entries}" : string.Empty))));

        if (OrderedActionDetailsEnabled && _orderedActions.Count != 0)
        {
            var ordered = _orderedActions
                .OrderByDescending(static pair => pair.Value.Executed + pair.Value.Deferred)
                .Take(12)
                .Select(static pair =>
                    $"{pair.Key}=ok{pair.Value.Executed}/defer{pair.Value.Deferred}");
            Console.Error.WriteLine($"[PERF][ORDERED] {string.Join(" ", ordered)}");
            _orderedActions.Clear();
        }
    }

    private static string GetOrderedActionCategory(string debugName)
    {
        if (debugName.EndsWith(" completion", StringComparison.Ordinal))
        {
            return "completion";
        }

        var firstSpace = debugName.IndexOf(' ');
        if (firstSpace < 0)
        {
            return debugName;
        }

        // AGC labels conventionally begin with "agc <packet>". Keeping the
        // packet token separates DMA, submit and register traffic without
        // retaining guest addresses in the diagnostic key.
        if (debugName.StartsWith("agc ", StringComparison.Ordinal))
        {
            var secondSpace = debugName.IndexOf(' ', firstSpace + 1);
            return secondSpace < 0 ? debugName : debugName[..secondSpace];
        }

        return debugName[..firstSpace];
    }

    private struct OrderedActionStats
    {
        public long Executed;
        public long Deferred;
    }
}

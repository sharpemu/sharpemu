// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// Keep the longest polls in each report interval without logging on the event path.
internal static class WindowPollProfile
{
    internal readonly record struct Sample(long StartedAt, long ElapsedTicks, bool ReturnedEvent, uint EventType);

    internal sealed class Counters
    {
        internal const int Capacity = 4;
        public Sample[] Longest { get; } = new Sample[Capacity];
        public int SampleCount { get; private set; }
        public long Calls { get; private set; }
        public long ReturnedEvents { get; private set; }
        public long TotalTicks { get; private set; }
        public long AtLeastOneMillisecond { get; private set; }
        public long AtLeastSixteenMilliseconds { get; private set; }

        public void Record(long startedAt, long finishedAt, bool returnedEvent, uint eventType)
        {
            var elapsedTicks = Math.Max(0, finishedAt - startedAt);
            Calls++;
            if (returnedEvent) ReturnedEvents++;
            TotalTicks += elapsedTicks;
            if (elapsedTicks >= Stopwatch.Frequency / 1000) AtLeastOneMillisecond++;
            if (elapsedTicks >= Stopwatch.Frequency * 16 / 1000) AtLeastSixteenMilliseconds++;

            var insertion = SampleCount;
            while (insertion > 0 && Longest[insertion - 1].ElapsedTicks < elapsedTicks)
                insertion--;
            if (insertion == Capacity) return;
            var last = Math.Min(SampleCount, Capacity - 1);
            for (var index = last; index > insertion; index--)
                Longest[index] = Longest[index - 1];
            Longest[insertion] = new Sample(startedAt, elapsedTicks, returnedEvent, returnedEvent ? eventType : 0);
            SampleCount = Math.Min(SampleCount + 1, Capacity);
        }
    }

    [ThreadStatic] private static Counters? _counters;

    internal static bool Enabled =>
        RenderPhaseProfile.FrameTraceEnabled && RenderPhaseProfile.DetailMeasurementsEnabled;

    internal static void Record(long startedAt, long finishedAt, bool returnedEvent, uint eventType)
    {
        if (!Enabled) return;
        (_counters ??= new Counters()).Record(startedAt, finishedAt, returnedEvent, eventType);
    }

    internal static void Report()
    {
        if (!RenderPhaseProfile.Enabled || _counters is not { } counters) return;
        _counters = null;
        Console.Error.WriteLine($"[PERF][WINDOW_POLL] calls={counters.Calls} events={counters.ReturnedEvents} " +
            $"empty={counters.Calls - counters.ReturnedEvents} total_ms={counters.TotalTicks * 1000.0 / Stopwatch.Frequency:F3} " +
            $"at_least_1ms={counters.AtLeastOneMillisecond} at_least_16ms={counters.AtLeastSixteenMilliseconds} frequency={Stopwatch.Frequency}");
        for (var index = 0; index < counters.SampleCount; index++)
        {
            var sample = counters.Longest[index];
            Console.Error.WriteLine($"[PERF][WINDOW_POLL_MAX] timestamp={sample.StartedAt} " +
                $"elapsed_ms={sample.ElapsedTicks * 1000.0 / Stopwatch.Frequency:F3} " +
                $"returned_event={(sample.ReturnedEvent ? 1 : 0)} event_type=0x{sample.EventType:X}");
        }
    }
}

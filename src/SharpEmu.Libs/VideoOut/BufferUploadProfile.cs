// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

// The render thread owns these bounded counters. Reports use the existing profile interval.
internal static class BufferUploadProfile
{
    internal readonly record struct Source(bool Sweep, ulong VertexProgramHash, ulong PixelProgramHash, ulong ComputeProgramHash);
    internal readonly record struct UploadKey(Source Source, ulong Address, ulong Size);
    internal readonly record struct SlowSample(UploadKey Key, int Copies, ulong Bytes, ulong HotBytes, long Ticks,
        long Timestamp = 0);

    internal sealed class Statistics
    {
        public long Sweeps;
        public long Checks;
        public long Uploads;
        public long Copies;
        public ulong Bytes;
        public ulong HotBytes;
        public long Ticks;

        public void Add(int copies, ulong bytes, ulong hotBytes, long ticks)
        {
            Checks++;
            Uploads += copies == 0 ? 0 : 1;
            Copies += copies;
            Bytes += bytes;
            HotBytes += hotBytes;
            Ticks += ticks;
        }

        public void Merge(Statistics other)
        {
            Sweeps += other.Sweeps;
            Checks += other.Checks;
            Uploads += other.Uploads;
            Copies += other.Copies;
            Bytes += other.Bytes;
            HotBytes += other.HotBytes;
            Ticks += other.Ticks;
        }
    }

    internal sealed class Counters
    {
        internal const int Capacity = 128;
        internal readonly Dictionary<Source, Statistics> Sources = [];
        internal readonly Dictionary<UploadKey, Statistics> Uploads = [];
        internal Statistics OtherSources = new();
        internal Statistics OtherUploads = new();
        internal const int SlowSampleCapacity = 8;
        internal readonly SlowSample[] SlowSamples = new SlowSample[SlowSampleCapacity];
        internal int SlowSampleCount;

        internal Statistics FindSource(Source source)
        {
            if (Sources.TryGetValue(source, out var statistics)) return statistics;
            if (Sources.Count == Capacity) return OtherSources;
            statistics = new Statistics();
            Sources.Add(source, statistics);
            return statistics;
        }

        internal void Record(Source source, ulong address, ulong size, int copies, ulong bytes, ulong hotBytes, long ticks,
            bool recordSlowSample = false)
        {
            FindSource(source).Add(copies, bytes, hotBytes, ticks);
            if (recordSlowSample) RecordSlowSample(new(new(source, address, size), copies, bytes, hotBytes, ticks));
            if (copies == 0) return;
            var key = new UploadKey(source, address, size);
            if (!Uploads.TryGetValue(key, out var statistics))
            {
                statistics = Uploads.Count == Capacity ? OtherUploads : new Statistics();
                if (Uploads.Count < Capacity) Uploads.Add(key, statistics);
            }

            statistics.Add(copies, bytes, hotBytes, ticks);
        }

        // Keep slow calls even when the range table is full or no upload is required.
        private void RecordSlowSample(SlowSample sample)
        {
            var position = SlowSampleCount;
            if (position == SlowSampleCapacity)
            {
                if (sample.Ticks <= SlowSamples[position - 1].Ticks) return;
                position--;
            }
            else
                SlowSampleCount++;

            while (position > 0 && sample.Ticks > SlowSamples[position - 1].Ticks)
            {
                SlowSamples[position] = SlowSamples[position - 1];
                position--;
            }
            SlowSamples[position] = sample with { Timestamp = Stopwatch.GetTimestamp() };
        }
    }

    [ThreadStatic] private static Counters? _counters;
    [ThreadStatic] private static Source _source;
    internal static bool Enabled => RenderPhaseProfile.FrameTraceEnabled;

    internal readonly struct Scope : IDisposable
    {
        private readonly Source _previousSource;
        private readonly bool _enabled;

        internal Scope(Source source)
        {
            _enabled = Enabled;
            _previousSource = _source;
            if (!_enabled) return;
            _source = source;
            (_counters ??= new Counters()).FindSource(source).Sweeps++;
        }

        public void Dispose()
        {
            if (_enabled) _source = _previousSource;
        }
    }

    internal static Scope BeginSweep(ulong vertexProgramHash, ulong pixelProgramHash, ulong computeProgramHash) =>
        new(new Source(true, vertexProgramHash, pixelProgramHash, computeProgramHash));

    internal static void Record(ulong address, ulong size, int copies, ulong bytes, ulong hotBytes, long ticks)
    {
        if (!Enabled) return;
        (_counters ??= new Counters()).Record(_source, address, size, copies, bytes, hotBytes, ticks,
            recordSlowSample: true);
    }

    private static string FormatSource(Source source) =>
        $"source={(source.Sweep ? "device-address" : "other")} vertex=0x{source.VertexProgramHash:X16} pixel=0x{source.PixelProgramHash:X16} compute=0x{source.ComputeProgramHash:X16}";

    private static string FormatStatistics(Statistics statistics) =>
        FormattableString.Invariant($"sweeps={statistics.Sweeps} checks={statistics.Checks} uploads={statistics.Uploads} copies={statistics.Copies} bytes={statistics.Bytes} hot_bytes={statistics.HotBytes} sync_ms={statistics.Ticks * 1000.0 / Stopwatch.Frequency:F2}");

    internal static void Report() => Report(Console.Error);

    internal static void Report(TextWriter writer)
    {
        if (!Enabled || _counters is null) return;
        for (var index = 0; index < _counters.SlowSampleCount; index++)
        {
            var sample = _counters.SlowSamples[index];
            writer.WriteLine(FormattableString.Invariant(
                $"[PERF][BUFFER_SYNC_SLOW] timestamp={sample.Timestamp} {FormatSource(sample.Key.Source)} address=0x{sample.Key.Address:X16} size={sample.Key.Size} copies={sample.Copies} bytes={sample.Bytes} hot_bytes={sample.HotBytes} sync_ms={sample.Ticks * 1000.0 / Stopwatch.Frequency:F3}"));
        }
        foreach (var (source, statistics) in _counters.Sources.OrderByDescending(pair => pair.Value.Ticks))
        {
            writer.WriteLine($"[PERF][BUFFER_SYNC] {FormatSource(source)} {FormatStatistics(statistics)}");
        }

        var uploads = _counters.Uploads.OrderByDescending(pair => pair.Value.Bytes).ToArray();
        foreach (var (key, statistics) in uploads.Take(8))
        {
            writer.WriteLine($"[PERF][BUFFER_UPLOAD] {FormatSource(key.Source)} address=0x{key.Address:X16} size={key.Size} {FormatStatistics(statistics)}");
        }

        if (_counters.OtherSources.Checks != 0 || _counters.OtherSources.Sweeps != 0)
            writer.WriteLine($"[PERF][BUFFER_SYNC] source=overflow {FormatStatistics(_counters.OtherSources)}");
        foreach (var (_, statistics) in uploads.Skip(8))
        {
            _counters.OtherUploads.Merge(statistics);
        }
        if (_counters.OtherUploads.Bytes != 0)
            writer.WriteLine($"[PERF][BUFFER_UPLOAD] source=other {FormatStatistics(_counters.OtherUploads)}");
        _counters = null;
    }
}

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

    private static long _totalUploads;
    private static long _totalCopies;
    private static long _totalBytes;
    private static long _totalRangeBytes;

    internal static void NoteUpload(int copies, ulong bytes, ulong rangeBytes)
    {
        if (!RenderPhaseProfile.Enabled) return;
        Interlocked.Increment(ref _totalUploads);
        Interlocked.Add(ref _totalCopies, copies);
        Interlocked.Add(ref _totalBytes, (long)bytes);
        Interlocked.Add(ref _totalRangeBytes, (long)rangeBytes);
    }

    private static readonly long[] _shapeUploads = new long[5];
    private static readonly long[] _shapeBytes = new long[5];
    private static readonly string[] ShapeNames = ["le4k", "le16k", "le64k", "le256k", "gt256k"];
    private static readonly Dictionary<(ulong, ulong), long> _lastUploadAt = new();
    private static long _repeatUploads;
    private static long _trackingTicks;
    private static long _recordTicks;

    internal static void NoteUploadShape(ulong address, ulong rangeSize, ulong bytes, long trackingTicks, long recordTicks)
    {
        if (!RenderPhaseProfile.Enabled) return;
        var bucket = bytes <= 4096 ? 0 : bytes <= 16384 ? 1 : bytes <= 65536 ? 2 : bytes <= 262144 ? 3 : 4;
        _shapeUploads[bucket]++;
        _shapeBytes[bucket] += (long)bytes;
        _trackingTicks += trackingTicks;
        _recordTicks += recordTicks;
        var now = Environment.TickCount64;
        if (_lastUploadAt.TryGetValue((address, rangeSize), out var last) && now - last <= 100)
        {
            _repeatUploads++;
        }

        if (_lastUploadAt.Count >= 65536)
        {
            _lastUploadAt.Clear();
        }

        _lastUploadAt[(address, rangeSize)] = now;
    }

    private static string TakeShapeReport()
    {
        var parts = new List<string>();
        for (var index = 0; index < ShapeNames.Length; index++)
        {
            parts.Add(ShapeNames[index] + "=" + FormattableString.Invariant($"{_shapeUploads[index]}/{_shapeBytes[index] / 1048576.0:F1}MB"));
            _shapeUploads[index] = 0;
            _shapeBytes[index] = 0;
        }

        var report = "shapes=" + string.Join(",", parts) + " " +
            FormattableString.Invariant($"repeat_100ms={_repeatUploads} tracking_ms={_trackingTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} record_ms={_recordTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}");
        _repeatUploads = 0;
        _trackingTicks = 0;
        _recordTicks = 0;
        return report;
    }

    internal static string TakeDirtyTrackingReport() =>
        "[PERF][DIRTY_TRACKING] " + Gpu.Buffers.GuestBufferCache.TakeDirtyTrackingReport() + " " + SharpEmu.HLE.GpuMemory.ProtectionCauseCounts.TakeReport() + " " +
        Gpu.Buffers.GpuBuffer.CopyRun.TakeReport() + " " + SharpEmu.ShaderCompiler.Resources.DeviceAddressRangePlanner.TakeUnboundedReport();

    internal static string TakeSyncPointReport() =>
        "[PERF][SYNC_POINTS] " + Gpu.Scheduling.GpuWorkerRelay.TakeRelayWaitReport() + " " + Gpu.GpuCommands.GpuCommandInterpreter.TakeAcquireMemoryReport();

    internal static string TakeTotalsReport() =>
        FormattableString.Invariant($"[PERF][BUFFER_SYNC_TOTAL] uploads={Interlocked.Exchange(ref _totalUploads, 0)} copies={Interlocked.Exchange(ref _totalCopies, 0)} bytes={Interlocked.Exchange(ref _totalBytes, 0)} range_bytes={Interlocked.Exchange(ref _totalRangeBytes, 0)} ") +
        TakeShapeReport() + " " +
        FormattableString.Invariant($"widened={Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.WidenedUploads, 0)} widened_bytes={Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.WidenedUploadBytes, 0)} ") +
        FormattableString.Invariant($"hot_skipped={SharpEmu.HLE.GpuMemory.TrackedRegion.TakeHotUploadsSkipped()} ") +
        FormattableString.Invariant($"readbacks=async:{Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.ReadbacksStarted, 0)},sync:{Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.ReadbacksSynchronous, 0)},no_writer:{Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.ReadbacksNoWriter, 0)},too_large:{Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.ReadbacksTooLarge, 0)},busy:{Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.ReadbacksBusy, 0)},slot_waits:{Interlocked.Exchange(ref Gpu.Buffers.GuestBufferCache.ReadbacksSlotWaits, 0)},") +
        TakeDirectReadReport() + " " +
        FormattableString.Invariant($"relay_hop_us=begin:{AverageMicroseconds(Gpu.Buffers.GuestBufferCache.RelayHopBeginTicks, Gpu.Buffers.GuestBufferCache.RelayHopBegin):F1},complete:{AverageMicroseconds(Gpu.Buffers.GuestBufferCache.RelayHopCompleteTicks, Gpu.Buffers.GuestBufferCache.RelayHopComplete):F1} ") +
        TakeGuestReadbackReport();

    private static string TakeDirectReadReport() =>
        FormattableString.Invariant($"promoted={Gpu.Buffers.GuestBufferCache.Promotions.Take()}/{Gpu.Buffers.GuestBufferCache.PromotedBufferBytes.Take()},mapped_at_create:{Gpu.Buffers.GuestBufferCache.MappedCreations.Take()},budget_full:{Gpu.Buffers.GuestBufferCache.PromotionsBudgetFull.Take()},live_bytes:{Interlocked.Read(ref Gpu.Buffers.GuestBufferCache.PromotedLiveBytes)}");

    private static readonly SharpEmu.HLE.Diagnostics.ThreadCounter[] _guestFrameLead = [new(), new(), new(), new(), new()];

    internal static void NoteGuestFrameLead(long lead) => _guestFrameLead[(int)Math.Clamp(lead, 0, _guestFrameLead.Length - 1)].Increment();

    private static string TakeGuestReadbackReport()
    {
        var reads = Gpu.Buffers.GuestBufferCache.GuestReadbacks.Take();
        var behind = Gpu.Buffers.GuestBufferCache.GuestReadbacksBehind.Take();
        var ticksBehind = Gpu.Buffers.GuestBufferCache.GuestReadbackTicksBehind.Take();
        var age = Gpu.Buffers.GuestBufferCache.GuestReadbackFrameAge;
        var lead = _guestFrameLead;
        return FormattableString.Invariant($"guest_reads={reads} read_us=queue:{PerSample(Gpu.Buffers.GuestBufferCache.GuestReadbackQueueTicks, reads):F1},begin:{PerSample(Gpu.Buffers.GuestBufferCache.GuestReadbackBeginTicks, reads):F1},gpu_wait:{PerSample(Gpu.Buffers.GuestBufferCache.GuestReadbackGpuWaitTicks, reads):F1} ") +
            FormattableString.Invariant($"read_behind={behind}/ticks:{(behind == 0 ? 0 : (double)ticksBehind / behind):F1} read_frame_age=0:{age[0].Take()},1:{age[1].Take()},2:{age[2].Take()},3+:{age[3].Take()} ") +
            FormattableString.Invariant($"guest_lead=0:{lead[0].Take()},1:{lead[1].Take()},2:{lead[2].Take()},3:{lead[3].Take()},4+:{lead[4].Take()} ") +
            TakeReadWriterReport() + " " + Gpu.Scheduling.SubmissionScheduler.TakeTickLedgerReport();
    }

    private static string TakeReadWriterReport()
    {
        var timed = Gpu.Buffers.GuestBufferCache.WriterTimedReads.Take();
        var held = Gpu.Buffers.GuestBufferCache.WriterHeldReads.Take();
        return FormattableString.Invariant($"read_writer=timed:{timed},writer_age_us:{PerSample(Gpu.Buffers.GuestBufferCache.WriterAgeTicks, timed):F1},since_submit_us:{PerSample(Gpu.Buffers.GuestBufferCache.WriterSinceSubmitTicks, timed):F1},") +
            FormattableString.Invariant($"held:{held}/us:{PerSample(Gpu.Buffers.GuestBufferCache.WriterHeldTicks, held):F1},submitted_by_read:{Gpu.Buffers.GuestBufferCache.WriterSubmittedByRead.Take()}");
    }

    private static double PerSample(SharpEmu.HLE.Diagnostics.ThreadCounter ticks, long samples)
    {
        var total = ticks.Take();
        return samples == 0 ? 0 : total * 1_000_000.0 / Stopwatch.Frequency / samples;
    }

    private static double AverageMicroseconds(SharpEmu.HLE.Diagnostics.ThreadCounter ticks, SharpEmu.HLE.Diagnostics.ThreadCounter count)
    {
        var total = ticks.Take();
        var samples = count.Take();
        return samples == 0 ? 0 : total * 1_000_000.0 / Stopwatch.Frequency / samples;
    }

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

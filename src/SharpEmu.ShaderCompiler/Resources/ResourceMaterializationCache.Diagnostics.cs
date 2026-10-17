// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceMaterializationCache
{
    private const int MaximumProfileKeys = 4096;
    private readonly record struct TransactionBuildKey(ulong Hash, ShaderStage Stage, bool TableOnly);
    private static readonly Dictionary<TransactionBuildKey, string> TransactionBuilds = [];

    internal static void RecordTransactionBuild(ShaderResourcePlan plan, bool tableOnly, string reason)
    {
        if (!MissDiagnosticsEnabled) return;
        lock (MissReportLock)
        {
            if (TransactionBuilds.Count < MaximumProfileKeys)
                TransactionBuilds[new(plan.Hash, plan.Stage, tableOnly)] = reason;
        }
    }
    private static readonly List<string> ChangedWordSamples = [];
    private static readonly Dictionary<(ulong Hash, ShaderStage Stage), int> ChangedWordCounts = [];
    private static readonly Dictionary<(ulong Hash, ShaderStage Stage, string Path), long> EvaluationPaths = [];
    private static int _changedWordSampleCount;
    private static readonly Dictionary<(ulong Hash, ShaderStage Stage, ulong Address), byte> PendingConsumers = new();
    private static int _pendingConsumerCount;
    private static readonly List<string> ConsumerSamples = [];

    internal static void QueueReadConsumer(ulong hash, ShaderStage stage, ulong address)
    {
        if (!MissDiagnosticsEnabled) return;
        lock (MissReportLock)
        {
            if (PendingConsumers.Count < 256) PendingConsumers.TryAdd((hash, stage, address), 0);
            Volatile.Write(ref _pendingConsumerCount, PendingConsumers.Count);
        }
    }

    internal static bool TakeReadConsumer(ulong hash, ShaderStage stage, ulong address)
    {
        if (!MissDiagnosticsEnabled || Volatile.Read(ref _pendingConsumerCount) == 0) return false;
        using var profile = ResourceMaterializationProfile.MeasureSampled(ResourceMaterializationProfile.Phase.ConsumerLookup);
        lock (MissReportLock)
        {
            if (!PendingConsumers.Remove((hash, stage, address))) return false;
            Volatile.Write(ref _pendingConsumerCount, PendingConsumers.Count);
            return true;
        }
    }

    internal static void RecordReadConsumer(ShaderResourcePlan plan, ScalarValue node, ulong address)
    {
        if (!TakeReadConsumer(plan.Hash, plan.Stage, address)) return;
        using var profile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.ConsumerGraph);
        var consumers = new List<string>();
        bool DependsOn(ScalarValue root)
        {
            var pending = new Stack<ScalarValue>();
            var visited = new HashSet<ScalarValue>();
            pending.Push(root);
            while (pending.TryPop(out var value))
            {
                if (ReferenceEquals(value, node)) return true;
                if (!visited.Add(value)) continue;
                foreach (var operand in value.Operands) pending.Push(operand);
                if (value.Kind == ScalarValueKind.ResourceTableWord && value.Payload < (ulong)plan.TableReads.Count)
                    pending.Push(plan.TableReads[(int)value.Payload].Value);
            }
            return false;
        }
        foreach (var source in plan.MaterializationSources)
        {
            var words = plan.DescriptorSources[(int)source].Dwords;
            for (var word = 0; word < words.Length && consumers.Count < 32; word++)
                if (DependsOn(words[word])) consumers.Add($"descriptor:{source}:{word}");
        }
        foreach (var read in plan.TableReads)
            if (consumers.Count < 32 && DependsOn(read.Value)) consumers.Add($"table:{read.FlatOffset}");
        foreach (var range in plan.DeviceAddressRanges)
            if (consumers.Count < 32 && (DependsOn(range.BaseLow) || DependsOn(range.BaseHigh)))
                consumers.Add($"range:{range.Handle}:written={range.Written}");
        lock (MissReportLock)
        {
            if (ConsumerSamples.Count < 256)
                ConsumerSamples.Add($"\n[PERF][RESOURCE_READ_CONSUMER] hash={plan.Hash:X16} stage={plan.Stage} address=0x{address:X16} node={node.Id} memory_index={node.MemoryIndex} pc=0x{plan.Memory[node.MemoryIndex].Pc:X} consumers={string.Join(',', consumers)} consumer_limit=32");
        }
    }

    private static void RecordChangedWords(Entry entry, int range, ReadOnlySpan<byte> current)
    {
        if (!MissDiagnosticsEnabled || Volatile.Read(ref _changedWordSampleCount) >= 256) return;
        lock (MissReportLock)
        {
            var shader = (entry.Plan.Hash, entry.Plan.Stage);
            ChangedWordCounts.TryGetValue(shader, out var count);
            if (count >= 4 || ChangedWordSamples.Count >= 256) return;
            var previous = entry.Bytes.AsSpan(entry.RangeOffsets[range], current.Length);
            for (var offset = 0; offset + sizeof(uint) <= current.Length && count < 4 && ChangedWordSamples.Count < 256; offset += sizeof(uint))
            {
                var oldWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(previous[offset..]);
                var newWord = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current[offset..]);
                if (oldWord == newWord) continue;
                QueueReadConsumer(shader.Hash, shader.Stage, entry.RangeAddresses[range] + (ulong)offset);
                var role = entry.WordTableOnly[(entry.RangeOffsets[range] + offset) / sizeof(uint)] ? "table_only" : "non_table_or_mixed";
                ChangedWordSamples.Add(FormattableString.Invariant(
                    $"\n[PERF][RESOURCE_CHANGED_WORD] hash={shader.Hash:X16} stage={shader.Stage} address=0x{entry.RangeAddresses[range] + (ulong)offset:X16} old=0x{oldWord:X8} new=0x{newWord:X8} role={role} clean={entry.RangeClean[range]} range_bytes={current.Length}"));
                count++;
            }
            ChangedWordCounts[shader] = count;
            Volatile.Write(ref _changedWordSampleCount, ChangedWordSamples.Count);
        }
    }

    internal static void RecordEvaluationPath(ShaderResourcePlan plan, string path)
    {
        if (!MissDiagnosticsEnabled) return;
        lock (MissReportLock)
        {
            var key = (plan.Hash, plan.Stage, path);
            if (EvaluationPaths.TryGetValue(key, out var count)) EvaluationPaths[key] = count + 1;
            else if (EvaluationPaths.Count < MaximumProfileKeys) EvaluationPaths.Add(key, 1);
        }
    }

    private enum CacheDetail { ValidateRead, ValidateCompare, RefreshRead, RefreshCompare, RefreshEvaluate, RefreshVerify, Count }
    private static readonly long[] DetailTicks = new long[(int)CacheDetail.Count];
    private static readonly long[] DetailCalls = new long[(int)CacheDetail.Count];
    private static readonly long[] DetailBytes = new long[(int)CacheDetail.Count];

    private readonly struct DetailScope(CacheDetail phase, long started, int bytes) : IDisposable
    {
        public void Dispose()
        {
            if (started == 0) return;
            Interlocked.Add(ref DetailTicks[(int)phase], System.Diagnostics.Stopwatch.GetTimestamp() - started);
            Interlocked.Increment(ref DetailCalls[(int)phase]);
            if (bytes != 0) Interlocked.Add(ref DetailBytes[(int)phase], bytes);
        }
    }

    private static DetailScope MeasureDetail(CacheDetail phase, int bytes = 0) =>
        new(phase, MissDiagnosticsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0, bytes);

    private static bool ReadWithProfile(ResidentGuestBytesReader reader, ulong address, Span<byte> destination,
        bool clean, CacheDetail phase)
    {
        using var profile = MeasureDetail(phase, destination.Length);
        return reader(address, destination, clean);
    }

    private static string TakeDetailReport()
    {
        if (!MissDiagnosticsEnabled) return string.Empty;
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < (int)CacheDetail.Count; index++)
        {
            var ticks = Interlocked.Exchange(ref DetailTicks[index], 0);
            var calls = Interlocked.Exchange(ref DetailCalls[index], 0);
            var bytes = Interlocked.Exchange(ref DetailBytes[index], 0);
            result.Append(FormattableString.Invariant($"\n[PERF][RESOURCE_CACHE_DETAIL] phase={(CacheDetail)index} calls={calls} attempted_bytes={bytes} inclusive_ms={ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F3}"));
        }
        return result.ToString();
    }

    private static readonly bool MissDiagnosticsEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    private static readonly object MissReportLock = new();
    private static readonly Dictionary<(ulong Hash, ShaderStage Stage), long[]> MissReports = new();
    private static long _missReportOverflow;
    private static long _diagnosticRotations;
    private readonly HashSet<ulong> _recentEvictedKeys = new();
    private static readonly Dictionary<(ulong Hash, ShaderStage Stage), long[]> TimingReports = new();
    private static readonly long[] OverflowTiming = new long[5];

    private enum MaterializationPhase { LookupValidation, TableRefresh, Materialization, CacheStore }

    private struct MaterializationTiming
    {
        private readonly bool _enabled;
        private long _start;
        private MaterializationPhase _phase;
        private long _lookup;
        private long _refresh;
        private long _materialize;
        private long _store;

        public MaterializationTiming(bool enabled)
        {
            this = default;
            _enabled = enabled;
            _start = enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        }

        public void ChangePhase(MaterializationPhase phase)
        {
            if (!_enabled) return;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsed = now - _start;
            switch (_phase)
            {
                case MaterializationPhase.LookupValidation: _lookup += elapsed; break;
                case MaterializationPhase.TableRefresh: _refresh += elapsed; break;
                case MaterializationPhase.Materialization: _materialize += elapsed; break;
                case MaterializationPhase.CacheStore: _store += elapsed; break;
            }
            _start = now;
            _phase = phase;
        }

        public void Complete(ShaderResourcePlan plan)
        {
            if (!_enabled) return;
            ChangePhase(_phase);
            lock (MissReportLock)
            {
                var shader = (plan.Hash, plan.Stage);
                if (!TimingReports.TryGetValue(shader, out var counts))
                {
                    counts = TimingReports.Count < MaximumProfileKeys ? new long[5] : OverflowTiming;
                    if (!ReferenceEquals(counts, OverflowTiming)) TimingReports.Add(shader, counts);
                }
                counts[0]++;
                counts[1] += _lookup;
                counts[2] += _refresh;
                counts[3] += _materialize;
                counts[4] += _store;
            }
        }
    }

    private static string FormatTiming(string identity, long[] counts)
    {
        var milliseconds = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return FormattableString.Invariant($"\n[PERF][RESOURCE_CACHE_TIME] {identity} calls={counts[0]} total_ms={(counts[1] + counts[2] + counts[3] + counts[4]) * milliseconds:F3} lookup_validate_ms={counts[1] * milliseconds:F3} refresh_ms={counts[2] * milliseconds:F3} materialize_ms={counts[3] * milliseconds:F3} cache_store_ms={counts[4] * milliseconds:F3}");
    }

    private void RecordEvictions(IEnumerable<ulong> keys)
    {
        if (!MissDiagnosticsEnabled) return;
        Interlocked.Increment(ref _diagnosticRotations);
        foreach (var key in keys)
        {
            if (_recentEvictedKeys.Count >= 65536) _recentEvictedKeys.Clear();
            _recentEvictedKeys.Add(key);
        }
    }

    private void RecordMiss(ShaderResourcePlan plan, ulong key, bool stale)
    {
        if (!MissDiagnosticsEnabled) return;
        var evictedKey = !stale && _recentEvictedKeys.Contains(key);
        lock (MissReportLock)
        {
            var shader = (plan.Hash, plan.Stage);
            if (!MissReports.TryGetValue(shader, out var counts))
            {
                if (MissReports.Count >= MaximumProfileKeys)
                {
                    _missReportOverflow++;
                    return;
                }
                MissReports.Add(shader, counts = new long[3]);
            }
            counts[stale ? 0 : evictedKey ? 1 : 2]++;
        }
    }

    private static string TakeMissReport()
    {
        if (!MissDiagnosticsEnabled) return string.Empty;
        lock (MissReportLock)
        {
            var result = new System.Text.StringBuilder();
            foreach (var (key, reason) in TransactionBuilds)
                result.Append($"\n[PERF][RESOURCE_TRANSACTION_REJECTED] hash={key.Hash:X16} stage={key.Stage} table_only={key.TableOnly} reason={reason}");
            TransactionBuilds.Clear();
            foreach (var sample in ChangedWordSamples) result.Append(sample);
            foreach (var sample in ConsumerSamples) result.Append(sample);
            ConsumerSamples.Clear();
            result.Append($"\n[PERF][RESOURCE_CONSUMER_PENDING] unmatched={PendingConsumers.Count}");
            PendingConsumers.Clear();
            Volatile.Write(ref _pendingConsumerCount, 0);
            foreach (var (key, count) in EvaluationPaths)
                result.Append($"\n[PERF][RESOURCE_EVALUATION_PATH] hash={key.Hash:X16} stage={key.Stage} path={key.Path} calls={count}");
            ChangedWordSamples.Clear();
            ChangedWordCounts.Clear();
            EvaluationPaths.Clear();
            Volatile.Write(ref _changedWordSampleCount, 0);
            result.Append($"\n[PERF][RESOURCE_CACHE_MISS] rotations={Interlocked.Exchange(ref _diagnosticRotations, 0)} overflow={_missReportOverflow}");
            foreach (var (shader, counts) in MissReports.OrderByDescending(pair => pair.Value.Sum()).Take(24))
                result.Append($"\n[PERF][RESOURCE_CACHE_SHADER] hash={shader.Hash:X16} stage={shader.Stage} stale={counts[0]} evicted_key_seen={counts[1]} other_key_miss={counts[2]}");
            MissReports.Clear();
            _missReportOverflow = 0;
            var totals = new long[5];
            foreach (var counts in TimingReports.Values.Append(OverflowTiming))
                for (var index = 0; index < totals.Length; index++) totals[index] += counts[index];
            result.Append(FormatTiming("scope=all", totals));
            var unprinted = new long[5];
            var printed = 0;
            foreach (var (shader, counts) in TimingReports.OrderByDescending(pair => pair.Value.Skip(1).Sum()))
            {
                if (printed++ < 24)
                    result.Append(FormatTiming($"hash={shader.Hash:X16} stage={shader.Stage}", counts));
                else
                    for (var index = 0; index < unprinted.Length; index++) unprinted[index] += counts[index];
            }
            result.Append(FormatTiming("scope=unprinted", unprinted));
            result.Append(FormatTiming("scope=overflow", OverflowTiming));
            TimingReports.Clear();
            Array.Clear(OverflowTiming);
            return result.ToString();
        }
    }
}

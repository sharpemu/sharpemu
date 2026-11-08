// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

internal sealed class RawReadGroup
{
    public required ScalarValue Handle { get; init; }
    public required ScalarValueKind Kind { get; init; }
    public required ScalarValue[] Reads { get; init; }
    public required long[] Immediates { get; init; }
    public required uint[] Offsets { get; init; }

    public CompiledResourceEvaluator? IndexedFor;
    public int[] HandleIndices = [];
    public int[] ReadIndices = [];
}

internal sealed class RawReadPrefetchPlan
{
    public required RawReadGroup[][] Table { get; init; }
    public required RawReadGroup[][] CleanTable { get; init; }
    public required RawReadGroup[][][] Sources { get; init; }
    public required int SourceLevels { get; init; }
}

internal static class RawReadPrefetch
{
    private const int MinimumRunWords = 2;
    private const int MaximumRunBytes = 64 * 1024;

    [ThreadStatic]
    private static Scratch? _scratch;

    private static long _runs;
    private static long _words;
    private static long _refused;

    public static string TakeReport() => FormattableString.Invariant(
        $"prefetch_runs={Interlocked.Exchange(ref _runs, 0)} prefetch_words={Interlocked.Exchange(ref _words, 0)} prefetch_refused={Interlocked.Exchange(ref _refused, 0)}");

    private sealed class Scratch
    {
        public ulong[] Addresses = new ulong[256];
        public int[] Order = new int[256];
        public ScalarValue[] Values = new ScalarValue[256];
        public int[] Indices = new int[256];
        public byte[] Bytes = new byte[1024];
        public int Count;

        public void Add(ulong address, ScalarValue value, int index)
        {
            if (Count == Addresses.Length)
            {
                Array.Resize(ref Addresses, Count * 2);
                Array.Resize(ref Order, Count * 2);
                Array.Resize(ref Values, Count * 2);
                Array.Resize(ref Indices, Count * 2);
            }

            Addresses[Count] = address;
            Order[Count] = Count;
            Values[Count] = value;
            Indices[Count] = index;
            Count++;
        }
    }

    public static RawReadPrefetchPlan For(ShaderResourcePlan plan)
    {
        if (plan.RawReadPrefetch is { } existing)
        {
            return existing;
        }

        var table = new List<ScalarValue>();
        var cleanTable = new List<ScalarValue>();
        foreach (var read in plan.TableReads)
        {
            var clean = read.FlatOffset < plan.CleanFlatSlots.Count && plan.CleanFlatSlots[(int)read.FlatOffset] != 0;
            (clean ? cleanTable : table).Add(read.Value);
        }

        var sources = new RawReadGroup[plan.DescriptorSources.Count][][];
        var sourceLevels = 0;
        for (var index = 0; index < sources.Length; index++)
        {
            sources[index] = Group(plan, plan.DescriptorSources[index].Dwords);
            sourceLevels = Math.Max(sourceLevels, sources[index].Length);
        }

        var built = new RawReadPrefetchPlan
        {
            Table = Group(plan, table),
            CleanTable = Group(plan, cleanTable),
            Sources = sources,
            SourceLevels = sourceLevels,
        };
        plan.RawReadPrefetch = built;
        return built;
    }

    private const int MaximumChainDepth = 64;

    private sealed class Chains
    {
        public readonly Dictionary<ScalarValue, int> Depths = new(ReferenceEqualityComparer.Instance);
        public readonly List<Dictionary<(ScalarValue Handle, ScalarValueKind Kind), List<ScalarValue>>> Levels = new();
    }

    private static RawReadGroup[][] Group(ShaderResourcePlan plan, IEnumerable<ScalarValue> values)
    {
        var chains = new Chains();
        foreach (var value in values)
        {
            Depth(plan, value, chains, MaximumChainDepth);
        }

        var levels = new RawReadGroup[chains.Levels.Count][];
        for (var level = 0; level < levels.Length; level++)
        {
            levels[level] = Build(plan, chains.Levels[level]);
        }

        return levels;
    }

    private static int Depth(ShaderResourcePlan plan, ScalarValue value, Chains chains, int budget)
    {
        if (chains.Depths.TryGetValue(value, out var known))
        {
            return known;
        }

        chains.Depths[value] = 0;
        if (budget == 0)
        {
            return 0;
        }

        var depth = 0;
        switch (value.Kind)
        {
            case ScalarValueKind.Operation:
            case ScalarValueKind.Select:
                foreach (var operand in value.Operands)
                {
                    depth = Math.Max(depth, Depth(plan, operand, chains, budget - 1));
                }

                break;
            case ScalarValueKind.Phi:
                if (plan.Graph.ResolveInvariantPhi(value) is { } invariant)
                {
                    depth = Depth(plan, invariant, chains, budget - 1);
                }

                break;
            case ScalarValueKind.ResourceTableWord:
                if (value.Payload < (ulong)plan.TableReads.Count)
                {
                    depth = Depth(plan, plan.TableReads[(int)value.Payload].Value, chains, budget - 1);
                }

                break;
            case ScalarValueKind.ScalarAddressWord:
            case ScalarValueKind.ScalarBufferWord:
            {
                if (value.Operands.Length < 2)
                {
                    break;
                }

                var handle = value.Operands[0];
                var handleOperands = value.Kind == ScalarValueKind.ScalarBufferWord ? 4 : 2;
                if (handle.Operands.Length < handleOperands)
                {
                    break;
                }

                for (var operand = 0; operand < handleOperands; operand++)
                {
                    depth = Math.Max(depth, Depth(plan, handle.Operands[operand], chains, budget - 1));
                }

                depth = Math.Max(depth, Depth(plan, value.Operands[1], chains, budget - 1));
                if (value.MemoryIndex < plan.Memory.Count && value.Operands[1].IsConstant &&
                    (value.Kind != ScalarValueKind.ScalarBufferWord || handle.Operands.Length == 4))
                {
                    while (chains.Levels.Count <= depth)
                    {
                        chains.Levels.Add(new(HandleComparer.Instance));
                    }

                    var groups = chains.Levels[depth];
                    if (!groups.TryGetValue((handle, value.Kind), out var reads))
                    {
                        groups[(handle, value.Kind)] = reads = [];
                    }

                    reads.Add(value);
                }

                depth++;
                break;
            }
        }

        chains.Depths[value] = depth;
        return depth;
    }

    private static RawReadGroup[] Build(ShaderResourcePlan plan, Dictionary<(ScalarValue Handle, ScalarValueKind Kind), List<ScalarValue>> groups)
    {
        var result = new List<RawReadGroup>();
        foreach (var ((handle, kind), reads) in groups)
        {
            var immediates = new long[reads.Count];
            var offsets = new uint[reads.Count];
            for (var index = 0; index < reads.Count; index++)
            {
                immediates[index] = (int)plan.Memory[reads[index].MemoryIndex].Offset;
                offsets[index] = (uint)reads[index].Operands[1].Payload;
            }

            result.Add(new RawReadGroup
            {
                Handle = handle,
                Kind = kind,
                Reads = [.. reads],
                Immediates = immediates,
                Offsets = offsets,
            });
        }

        return [.. result];
    }

    public static void PrefetchSources(ShaderResourcePlan plan, IReadOnlyList<uint> sources, bool[] activeSources, RuntimeValueEvaluator evaluator)
    {
        if (evaluator.Inputs.ReadResidentMemory is null || evaluator.Inputs.ReadMemory is null)
        {
            return;
        }

        var prefetch = For(plan);
        for (var level = 0; level < prefetch.SourceLevels; level++)
        {
            var scratch = Begin();
            foreach (var source in sources)
            {
                if (source >= prefetch.Sources.Length || (activeSources.Length != 0 && !activeSources[source]) ||
                    level >= prefetch.Sources[source].Length)
                {
                    continue;
                }

                Collect(prefetch.Sources[source][level], evaluator, scratch);
            }

            ReadRuns(evaluator, scratch);
        }
    }

    public static void PrefetchTable(ShaderResourcePlan plan, RuntimeValueEvaluator evaluator, RuntimeValueEvaluator cleanEvaluator,
        IReadOnlyList<byte> cleanFlatSlots)
    {
        if (evaluator.Inputs.ReadResidentMemory is null || !ReferenceEquals(cleanFlatSlots, plan.CleanFlatSlots))
        {
            return;
        }

        var prefetch = For(plan);
        if (evaluator.Inputs.ReadMemory is not null)
        {
            foreach (var level in prefetch.Table)
            {
                var scratch = Begin();
                Collect(level, evaluator, scratch);
                ReadRuns(evaluator, scratch);
            }
        }

        if (cleanEvaluator.Inputs.ReadMemory is not null)
        {
            foreach (var level in prefetch.CleanTable)
            {
                var scratch = Begin();
                Collect(level, cleanEvaluator, scratch);
                ReadRuns(cleanEvaluator, scratch);
            }
        }
    }

    private static Scratch Begin()
    {
        var scratch = _scratch ??= new Scratch();
        scratch.Count = 0;
        return scratch;
    }

    private static void Collect(RawReadGroup[] groups, RuntimeValueEvaluator evaluator, Scratch scratch)
    {
        var compiled = evaluator.IndexedEvaluator;
        foreach (var group in groups)
        {
            int[]? readIndices = null;
            bool based;
            ulong baseAddress;
            ulong high;
            ulong records;
            if (compiled is not null)
            {
                if (!ReferenceEquals(Volatile.Read(ref group.IndexedFor), compiled))
                {
                    Index(group, compiled);
                }

                readIndices = group.ReadIndices;
                based = evaluator.TryEvaluateRawBase(group.Handle, group.Kind, group.HandleIndices, out baseAddress, out high, out records);
            }
            else
            {
                based = evaluator.TryEvaluateRawBase(group.Handle, group.Kind, out baseAddress, out high, out records);
            }

            if (!based || (baseAddress & ~3ul) == 0)
            {
                continue;
            }

            for (var index = 0; index < group.Reads.Length; index++)
            {
                var readIndex = readIndices is null ? -1 : readIndices[index];
                var evaluated = readIndex >= 0 ? evaluator.IsEvaluatedAt(readIndex) : evaluator.IsEvaluated(group.Reads[index]);
                if (!evaluated &&
                    RuntimeValueEvaluator.ResolveRawAddress(group.Kind, baseAddress, high, records, group.Immediates[index], group.Offsets[index],
                        out var address) == RuntimeValueEvaluator.RawAddress.Read)
                {
                    scratch.Add(address, group.Reads[index], readIndex);
                }
            }
        }
    }

    private static void Index(RawReadGroup group, CompiledResourceEvaluator compiled)
    {
        static int IndexOf(CompiledResourceEvaluator compiled, ScalarValue value) =>
            !value.IsConstant && compiled.TryGetIndex(value, out var index) ? index : -1;

        var handle = new int[group.Handle.Operands.Length];
        for (var operand = 0; operand < handle.Length; operand++)
        {
            handle[operand] = IndexOf(compiled, group.Handle.Operands[operand]);
        }

        var reads = new int[group.Reads.Length];
        for (var read = 0; read < reads.Length; read++)
        {
            reads[read] = IndexOf(compiled, group.Reads[read]);
        }

        group.HandleIndices = handle;
        group.ReadIndices = reads;
        Volatile.Write(ref group.IndexedFor, compiled);
    }

    private static void ReadRuns(RuntimeValueEvaluator evaluator, Scratch scratch)
    {
        var count = scratch.Count;
        if (count < MinimumRunWords)
        {
            Array.Clear(scratch.Values, 0, count);
            return;
        }

        var reader = evaluator.Inputs.ReadResidentMemory!;
        var clean = evaluator.Inputs.ReadsClean;
        var addresses = scratch.Addresses;
        var values = scratch.Values;
        Array.Sort(addresses, scratch.Order, 0, count);
        var start = 0;
        while (start < count)
        {
            var end = start + 1;
            var words = 1;
            while (end < count && addresses[end] - addresses[end - 1] <= sizeof(uint) &&
                   addresses[end] - addresses[start] < MaximumRunBytes)
            {
                if (addresses[end] != addresses[end - 1])
                {
                    words++;
                }

                end++;
            }

            if (words >= MinimumRunWords)
            {
                ReadRun(evaluator, reader, clean, scratch, start, end);
            }

            start = end;
        }

        Array.Clear(values, 0, count);
    }

    private static void ReadRun(RuntimeValueEvaluator evaluator, ResidentGuestBytesReader reader, bool clean, Scratch scratch, int start, int end)
    {
        var addresses = scratch.Addresses;
        var first = addresses[start];
        var length = checked((int)(addresses[end - 1] - first) + sizeof(uint));
        if (scratch.Bytes.Length < length)
        {
            scratch.Bytes = new byte[Math.Max(length, scratch.Bytes.Length * 2)];
        }

        var bytes = scratch.Bytes.AsSpan(0, length);
        if (!reader(first, bytes, clean))
        {
            Interlocked.Increment(ref _refused);
            return;
        }

        Interlocked.Increment(ref _runs);
        Interlocked.Add(ref _words, end - start);
        for (var index = start; index < end; index++)
        {
            var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(addresses[index] - first)..]);
            var added = scratch.Order[index];
            var compiledIndex = scratch.Indices[added];
            if (compiledIndex >= 0)
            {
                evaluator.SeedAt(compiledIndex, word);
            }
            else
            {
                evaluator.Seed(scratch.Values[added], word);
            }

            evaluator.RecordPrefetchedConsumer(scratch.Values[added], addresses[index]);
        }
    }

    private sealed class HandleComparer : IEqualityComparer<(ScalarValue Handle, ScalarValueKind Kind)>
    {
        public static readonly HandleComparer Instance = new();

        public bool Equals((ScalarValue Handle, ScalarValueKind Kind) left, (ScalarValue Handle, ScalarValueKind Kind) right) =>
            ReferenceEquals(left.Handle, right.Handle) && left.Kind == right.Kind;

        public int GetHashCode((ScalarValue Handle, ScalarValueKind Kind) value) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Handle), value.Kind);
    }
}

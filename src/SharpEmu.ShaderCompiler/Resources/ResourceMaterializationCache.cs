// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.ShaderCompiler.Resources;

// Copies guest bytes only when they are already resident on the CPU side. It never
// synchronizes: a range the GPU may still own, or that a clean read would refuse,
// returns false so the caller falls back to a full materialization.
public delegate bool ResidentGuestBytesReader(ulong address, Span<byte> destination, bool clean);

// Materialization is a pure function of the plan, the draw's user data, the shader
// base, the compute state and the guest words it reads. This cache records exactly
// those words and reuses the result while every one of them is unchanged, so a draw
// that re-binds the same resources skips the descriptor walk. Any failed read, any
// GPU-owned range and any byte difference make the entry miss; nothing is guessed.
public sealed class ResourceMaterializationCache
{
    // Two generations approximate LRU: a full young generation becomes the old one,
    // and an old entry that is used again is promoted back.
    private readonly int _generationCapacity;
    private Dictionary<ulong, Entry> _young = new();
    private Dictionary<ulong, Entry> _old = new();
    private byte[] _scratch = new byte[256];

    public ResourceMaterializationCache(int generationCapacity = 16384)
    {
        _generationCapacity = Math.Max(1, generationCapacity);
    }

    private static long _totalHits;
    private static long _totalMisses;
    private static long _totalUncacheable;

    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long Uncacheable { get; private set; }

    // Process-wide counters since the previous call, for the periodic render report.
    public static string TakeReport()
    {
        var hits = Interlocked.Exchange(ref _totalHits, 0);
        var misses = Interlocked.Exchange(ref _totalMisses, 0);
        var uncacheable = Interlocked.Exchange(ref _totalUncacheable, 0);
        var total = hits + misses;
        return FormattableString.Invariant(
            $"[PERF][RESOURCE_CACHE] hits={hits} misses={misses} uncacheable={uncacheable} hit_rate={(total == 0 ? 0 : hits * 100.0 / total):F1}%");
    }

    public bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure)
    {
        var key = KeyOf(plan, inputs);
        if (TryFind(key, plan, inputs, out var cached) && Validate(cached, residentReader))
        {
            Hits++;
            Interlocked.Increment(ref _totalHits);
            snapshot = cached.Snapshot;
            specialization = cached.Specialization;
            failure = default;
            return true;
        }

        Misses++;
        Interlocked.Increment(ref _totalMisses);
        var recorder = new ReadRecorder();
        var recording = new ResourceRuntimeInputs
        {
            UserData = inputs.UserData,
            ShaderBase = inputs.ShaderBase,
            ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
            ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
            ComputeState = inputs.ComputeState,
        };
        if (!ResourceMaterializer.Materialize(plan, recording, ref snapshot, ref specialization, out failure))
            return false;

        if (recorder.Failed)
        {
            Uncacheable++;
            Interlocked.Increment(ref _totalUncacheable);
            return true;
        }

        Store(key, recorder.Build(plan, inputs, snapshot, specialization));
        return true;
    }

    private static ulong KeyOf(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(plan));
        hash.Add(inputs.ShaderBase);
        hash.Add(inputs.ComputeState);
        var userData = inputs.UserData;
        hash.Add(userData.Count);
        for (var index = 0; index < userData.Count; index++)
            hash.Add(userData[index]);
        var low = (uint)hash.ToHashCode();
        // A second, independent mix keeps accidental collisions out of the 64-bit key.
        var high = 0x9E3779B9u;
        for (var index = 0; index < userData.Count; index++)
            high = (high ^ userData[index]) * 0x01000193u;
        return ((ulong)high << 32) | low;
    }

    private bool TryFind(ulong key, ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out Entry entry)
    {
        if (_young.TryGetValue(key, out entry!))
            return entry.Matches(plan, inputs);
        if (!_old.TryGetValue(key, out entry!) || !entry.Matches(plan, inputs))
            return false;
        _old.Remove(key);
        Store(key, entry);
        return true;
    }

    private void Store(ulong key, Entry entry)
    {
        if (_young.Count >= _generationCapacity && !_young.ContainsKey(key))
        {
            _old = _young;
            _young = new Dictionary<ulong, Entry>(_generationCapacity);
        }

        _young[key] = entry;
    }

    private bool Validate(Entry entry, ResidentGuestBytesReader residentReader)
    {
        for (var index = 0; index < entry.RangeAddresses.Length; index++)
        {
            var length = entry.RangeLengths[index];
            if (_scratch.Length < length)
                _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
            var current = _scratch.AsSpan(0, length);
            if (!residentReader(entry.RangeAddresses[index], current, entry.RangeClean[index]) ||
                !current.SequenceEqual(entry.Bytes.AsSpan(entry.RangeOffsets[index], length)))
                return false;
        }

        return true;
    }

    private sealed class Entry
    {
        public required ShaderResourcePlan Plan { get; init; }
        public required uint[] UserData { get; init; }
        public required ulong ShaderBase { get; init; }
        public required ComputeSelectorState? ComputeState { get; init; }
        public required ulong[] RangeAddresses { get; init; }
        public required int[] RangeOffsets { get; init; }
        public required int[] RangeLengths { get; init; }
        public required bool[] RangeClean { get; init; }
        public required byte[] Bytes { get; init; }
        public required ResourceSnapshot Snapshot { get; init; }
        public required ResourceSpecialization Specialization { get; init; }

        public bool Matches(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
        {
            if (!ReferenceEquals(Plan, plan) || ShaderBase != inputs.ShaderBase ||
                !Nullable.Equals(ComputeState, inputs.ComputeState) || UserData.Length != inputs.UserData.Count)
                return false;
            for (var index = 0; index < UserData.Length; index++)
                if (UserData[index] != inputs.UserData[index])
                    return false;
            return true;
        }
    }

    private sealed class ReadRecorder
    {
        private readonly List<(ulong Address, uint Word, bool Clean)> _reads = new();

        public bool Failed { get; private set; }

        public GuestWordReader? Wrap(GuestWordReader? inner, bool clean)
        {
            if (inner is null)
                return null;
            return (ulong address, out uint word) =>
            {
                if (!inner(address, out word))
                {
                    Failed = true;
                    return false;
                }

                _reads.Add((address, word, clean));
                return true;
            };
        }

        public Entry Build(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, ResourceSnapshot snapshot,
            ResourceSpecialization specialization)
        {
            _reads.Sort((left, right) => left.Address.CompareTo(right.Address));
            var addresses = new List<ulong>();
            var offsets = new List<int>();
            var lengths = new List<int>();
            var clean = new List<bool>();
            var bytes = new List<byte>(_reads.Count * sizeof(uint));
            ulong end = 0;
            var previous = ulong.MaxValue;
            foreach (var (address, word, wordClean) in _reads)
            {
                if (address == previous)
                {
                    clean[^1] |= wordClean;
                    continue;
                }

                previous = address;
                if (addresses.Count == 0 || address != end)
                {
                    addresses.Add(address);
                    offsets.Add(bytes.Count);
                    lengths.Add(0);
                    clean.Add(false);
                }

                lengths[^1] += sizeof(uint);
                clean[^1] |= wordClean;
                bytes.Add((byte)word);
                bytes.Add((byte)(word >> 8));
                bytes.Add((byte)(word >> 16));
                bytes.Add((byte)(word >> 24));
                end = address + sizeof(uint);
            }

            var userData = new uint[inputs.UserData.Count];
            for (var index = 0; index < userData.Length; index++)
                userData[index] = inputs.UserData[index];
            return new Entry
            {
                Plan = plan,
                UserData = userData,
                ShaderBase = inputs.ShaderBase,
                ComputeState = inputs.ComputeState,
                RangeAddresses = [.. addresses],
                RangeOffsets = [.. offsets],
                RangeLengths = [.. lengths],
                RangeClean = [.. clean],
                Bytes = [.. bytes],
                Snapshot = snapshot,
                Specialization = specialization,
            };
        }
    }
}

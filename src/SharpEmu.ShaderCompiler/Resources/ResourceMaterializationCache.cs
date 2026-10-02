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
    public long TableRefreshes { get; private set; }

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
        if (TryFind(key, plan, inputs, out var cached))
        {
            if (Validate(cached, residentReader))
            {
                Hits++;
                Interlocked.Increment(ref _totalHits);
                snapshot = cached.Snapshot;
                specialization = cached.Specialization;
                failure = default;
                return true;
            }

            if (TryRefreshTable(key, cached, plan, inputs, residentReader, out var refreshed))
            {
                TableRefreshes++;
                snapshot = refreshed.Snapshot;
                specialization = refreshed.Specialization;
                failure = default;
                return true;
            }
        }

        Misses++;
        Interlocked.Increment(ref _totalMisses);
        var recorder = new ReadRecorder();
        var recording = inputs with
        {
            ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
            ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
            ReadCleanWords = recorder.Wrap(inputs.ReadCleanWords),
            TablePhase = recorder.SetTablePhase,
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

    // Most stale entries in Demon's Souls differ only in words the shader reads through scalar
    // loads (inline constants rewritten every frame); the descriptors, device ranges and
    // specialization are unchanged. When every changed word was read only while evaluating the
    // flattened table, only the table is evaluated again. It is refused when the plan's
    // specialization reads or extends the table (indirect or candidate tables), when the table
    // now reads a word the entry did not validate, or when a word moved between the two reads.
    private bool TryRefreshTable(ulong key, Entry cached, ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        ResidentGuestBytesReader residentReader, out Entry refreshed)
    {
        refreshed = null!;
        if (!cached.TableRefreshable)
            return false;

        var current = new byte[cached.Bytes.Length];
        for (var index = 0; index < cached.RangeAddresses.Length; index++)
        {
            if (!residentReader(cached.RangeAddresses[index], current.AsSpan(cached.RangeOffsets[index], cached.RangeLengths[index]), cached.RangeClean[index]))
                return false;
        }

        var changed = false;
        for (var offset = 0; offset < current.Length; offset += sizeof(uint))
        {
            if (current.AsSpan(offset, sizeof(uint)).SequenceEqual(cached.Bytes.AsSpan(offset, sizeof(uint))))
                continue;
            if (!cached.WordTableOnly[offset / sizeof(uint)])
                return false;
            changed = true;
        }

        if (!changed)
            return false;

        var recorder = new ReadRecorder();
        var recording = inputs with
        {
            ReadMemory = recorder.Wrap(inputs.ReadMemory, clean: false),
            ReadCleanMemory = recorder.Wrap(inputs.ReadCleanMemory, clean: true),
            ReadCleanWords = recorder.Wrap(inputs.ReadCleanWords),
        };
        var cachedTable = cached.Snapshot.FlattenedResourceTable;
        if (!ResourceMaterializer.TryEvaluateTable(plan, recording, out var table) || recorder.Failed || table.Length != cachedTable.Length)
            return false;

        foreach (var (address, word, _, _) in recorder.Reads)
        {
            if (!TryFindWord(cached, address, out var offset) ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(current.AsSpan(offset, sizeof(uint))) != word)
                return false;
        }

        // The written device-address slots come from reads validated unchanged above.
        foreach (var slot in plan.WrittenRangeSlotByHandle.Values)
            cachedTable.AsSpan((int)slot, ShaderResourcePlan.WrittenRangeDwordCount).CopyTo(table.AsSpan((int)slot));

        var previous = cached.Snapshot;
        refreshed = new Entry
        {
            Plan = cached.Plan,
            UserData = cached.UserData,
            ShaderBase = cached.ShaderBase,
            ComputeState = cached.ComputeState,
            RangeAddresses = cached.RangeAddresses,
            RangeOffsets = cached.RangeOffsets,
            RangeLengths = cached.RangeLengths,
            RangeClean = cached.RangeClean,
            WordTableOnly = cached.WordTableOnly,
            TableRefreshable = true,
            Bytes = current,
            Snapshot = new ResourceSnapshot
            {
                Buffers = previous.Buffers,
                Images = previous.Images,
                Samplers = previous.Samplers,
                FlattenedResourceTable = table,
                UserData = previous.UserData,
                DeviceAddressRanges = previous.DeviceAddressRanges,
            },
            Specialization = cached.Specialization,
        };
        Store(key, refreshed);
        return true;
    }

    // The byte offset of a recorded dword in the entry's bytes, found by its address.
    private static bool TryFindWord(Entry entry, ulong address, out int offset)
    {
        offset = 0;
        var addresses = entry.RangeAddresses;
        int low = 0, high = addresses.Length - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) >>> 1;
            if (addresses[middle] <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
            return false;
        var delta = address - addresses[found];
        if (delta % sizeof(uint) != 0 || delta >= (ulong)entry.RangeLengths[found])
            return false;
        offset = entry.RangeOffsets[found] + (int)delta;
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
        // Per recorded dword: read only while the flattened table was evaluated.
        public required bool[] WordTableOnly { get; init; }
        // The table has the plan's plain layout, so no specialization read or extended it.
        public required bool TableRefreshable { get; init; }
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
        private readonly List<(ulong Address, uint Word, bool Clean, bool Table)> _reads = new();
        private readonly Dictionary<ulong, int> _readIndex = new();
        private bool _inTable;

        public bool Failed { get; private set; }

        public List<(ulong Address, uint Word, bool Clean, bool Table)> Reads => _reads;

        public void SetTablePhase(bool inTable) => _inTable = inTable;

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

                Record(address, word, clean);
                return true;
            };
        }

        public GuestWordsReader? Wrap(GuestWordsReader? inner)
        {
            if (inner is null) return null;
            return (ulong address, Span<uint> words) =>
            {
                // A refused range falls back to individual reads; only those
                // determine whether the materialization is uncacheable.
                if (!inner(address, words)) return false;
                for (var index = 0; index < words.Length; index++)
                    Record(address + (ulong)index * sizeof(uint), words[index], true);
                return true;
            };
        }

        private void Record(ulong address, uint word, bool clean)
        {
            if (_readIndex.TryGetValue(address, out var index))
            {
                var previous = _reads[index];
                // A changing input during one materialization cannot be represented
                // by a cache entry that validates only one value for this address.
                if (previous.Word != word) Failed = true;
                _reads[index] = (address, previous.Word, previous.Clean || clean, previous.Table && _inTable);
                return;
            }
            _readIndex.Add(address, _reads.Count);
            _reads.Add((address, word, clean, _inTable));
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
            var tableOnly = new List<bool>(_reads.Count);
            ulong end = 0;
            var previous = ulong.MaxValue;
            foreach (var (address, word, wordClean, table) in _reads)
            {
                if (address == previous)
                {
                    clean[^1] |= wordClean;
                    tableOnly[^1] &= table;
                    continue;
                }

                tableOnly.Add(table);

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
                WordTableOnly = [.. tableOnly],
                TableRefreshable = snapshot.FlattenedResourceTable.Length ==
                    plan.TableReads.Count + plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount,
                Bytes = [.. bytes],
                Snapshot = snapshot,
                Specialization = specialization,
            };
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.ShaderCompiler.Resources;

// Resolves a plan against one draw: descriptor sources, indirect image tables and the
// specialization. Both outputs change only when the whole materialisation succeeds.
public static partial class ResourceMaterializer
{
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;
    private const ulong MaxIndirectImageProbes = 65536;

    // The smallest and largest layouts of a table selected by a loop counter; see FitSharedImageTables.
    private const int MinSharedCandidates = 8;
    private const int MaxSharedCandidates = 256;
    private const int MinSharedKeyCapacity = 32;
    private const int MaxSharedKeyCapacity = 1024;

    // Written to standard error like every specialization refusal; the host turns it
    // into its fatal.
    public static Action<string> SpecializationFailed { get; set; } = message => Console.Error.WriteLine($"shader resource specialization failed: {message}");

    private sealed class IndirectImageTable
    {
        public uint Resource;
        public List<uint> Keys = [];
        public List<uint> Candidates = [];
        public List<DescriptorWords> Descriptors = [];
        public uint[] MaterialDescriptor = [];
        public uint[] HeapDescriptor = [];
        public uint SelectorStride;
        public uint SelectorOffset;
        public bool Shared;
        public int KeyCapacity;
        public IndirectSelectorDiagnostic? SelectorDiagnostic;
    }

    private sealed class MaterializedSnapshot
    {
        public uint[][] Buffers = [];
        public uint[][] Images = [];
        public uint[][] Samplers = [];
        public uint[] FlattenedTable = [];
        public uint[] UserData = [];
        public List<IndirectImageTable> IndirectImages = [];
        public List<BufferCandidateTable> BufferCandidateTables = [];
    }

    // One bounded runtime V# table resolved for this draw: the distinct candidate descriptors
    // and the run-time offset -> candidate mapping the emitter searches.
    private sealed class BufferCandidateTable
    {
        public int MemoryIndex;
        public List<uint> Keys = [];
        public List<uint> Candidates = [];
        public List<DescriptorWords> Descriptors = [];
    }

    public static bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        Action<IndirectImageFailure>? captureIndirectImageFailure = null)
        => Materialize(plan, inputs, ref snapshot, ref specialization, out _, captureIndirectImageFailure);

    public static bool Materialize(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure,
        Action<IndirectImageFailure>? captureIndirectImageFailure = null)
    {
        using var totalProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Total);
        if (!MaterializeSnapshot(plan, inputs, captureIndirectImageFailure is not null, out var materialized, out var programRanges, out failure))
        {
            return false;
        }
        if (plan.NormalizeBufferDescriptor is { } normalize)
        {
            for (var index = 0; index < materialized.Buffers.Length; index++)
                materialized.Buffers[index] = normalize(materialized.Buffers[index]);
            foreach (var candidates in materialized.BufferCandidateTables)
                for (var index = 0; index < candidates.Descriptors.Count; index++)
                    candidates.Descriptors[index] = new DescriptorWords(normalize(candidates.Descriptors[index].Dwords));
        }

        // The written ranges follow the table reads so every store can check its own.
        DeviceAddressRange[] ranges;
        using (ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.DeviceAddressRanges))
            ranges = programRanges ?? DeviceAddressRangePlanner.Evaluate(plan, inputs);
        foreach (var range in ranges)
        {
            if (!plan.WrittenRangeSlotByHandle.TryGetValue(range.Handle, out var slot))
            {
                continue;
            }

            var offset = (int)slot;
            materialized.FlattenedTable[offset] = (uint)range.Base;
            materialized.FlattenedTable[offset + 1] = (uint)(range.Base >> 32);
            materialized.FlattenedTable[offset + 2] = (uint)Math.Min(range.Size, uint.MaxValue);
        }

        var guard = plan.NormalizeImageDescriptor is null ? Volatile.Read(ref plan.SpecializationGuard) : null;
        MaterializedSnapshot nextSnapshot;
        ResourceSpecialization nextSpecialization;
        if (guard is not null && MatchesGuard(plan, materialized, guard))
        {
            using var guardProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.SpecializationGuardHit);
            ApplyGuard(materialized, guard);
            nextSnapshot = materialized;
            nextSpecialization = guard.Specialization.Clone();
        }
        else
        {
            var candidate = plan.NormalizeImageDescriptor is null ? CaptureGuard(plan, materialized) : null;
            var originalSamplers = materialized.Samplers;
            if (!BuildSpecialization(plan, materialized, out nextSnapshot, out nextSpecialization, out failure, captureIndirectImageFailure))
                return false;
            if (candidate is not null && MatchesGuard(plan, materialized, candidate, checkSamplers: false))
            {
                candidate.SamplerSources = materialized.Samplers.Select(words => Array.FindIndex(originalSamplers,
                    original => ReferenceEquals(original, words))).ToArray();
                if (candidate.SamplerSources.All(index => index >= 0))
                {
                    candidate.Specialization = nextSpecialization.Clone();
                    Volatile.Write(ref plan.SpecializationGuard, candidate);
                }
            }
        }

        using var outputProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.OutputAssembly);
        snapshot = new ResourceSnapshot
        {
            Buffers = nextSnapshot.Buffers,
            Images = nextSnapshot.Images,
            Samplers = nextSnapshot.Samplers,
            FlattenedResourceTable = nextSnapshot.FlattenedTable,
            UserData = nextSnapshot.UserData,
            DeviceAddressRanges = ranges,
        };
        specialization = nextSpecialization;
        failure = ResourceMaterializationFailure.None;
        MaterializedCheck?.Invoke(plan, inputs, snapshot, specialization);
        return true;
    }

    // The words BuildSpecialization reads, with the specialization and the specialized image and
    // sampler arrays they produced; a draw whose words match takes all three unchanged.
    private sealed class SpecializationMemo
    {
        public required uint[][] KeyImages;
        public required uint[][] KeySamplers;
        public required uint[] KeyBuffers;
        public required uint[][] Images;
        public required uint[][] Samplers;
        public required ResourceSpecialization Specialization;
    }

    private const int SpecializationMemoEntries = 8;

    internal static Action<ShaderResourcePlan, ResourceRuntimeInputs, ResourceSnapshot, ResourceSpecialization>? MaterializedCheck;

    private static readonly ThreadCounter _fastMaterializations = new();
    private static readonly ThreadCounter _fastSpecializationHits = new();
    private static readonly ThreadCounter _fastStatisticsHits = new();

    public static string TakeFastReport() => FormattableString.Invariant(
        $"fast_materializations={_fastMaterializations.Take()} fast_specialization_hits={_fastSpecializationHits.Take()} fast_statistics_hits={_fastStatisticsHits.Take()}");

    private sealed record FastOrigin(ShaderResourcePlan Plan, ResourceRuntimeInputs Inputs);

    [ThreadStatic]
    private static MaterializationTally.Outcome _lastFastOutcome;

    [ThreadStatic]
    private static bool _lastRevalidateRefused;

    public static MaterializationTally.Outcome LastFastOutcome => _lastFastOutcome;

    public static bool LastRevalidateRefused => _lastRevalidateRefused;

    // A fast snapshot is still current when its plan evaluates to the same words again; null when
    // the snapshot did not come from the fast path.
    public static bool? RevalidateFast(ResourceSnapshot snapshot)
    {
        if (snapshot.Origin is not FastOrigin origin)
        {
            return null;
        }

        var plan = origin.Plan;
        _lastRevalidateRefused = origin.Inputs.TablePhase is not null;
        if (_lastRevalidateRefused)
        {
            return false;
        }

        var output = TakeOutput();
        try
        {
            return EvaluateInto(plan, origin.Inputs, output) && SameAsOutput(plan, snapshot, output);
        }
        finally
        {
            _programOutput = output;
        }
    }

    [ThreadStatic]
    private static MaterializationProgram.Output? _programOutput;

    // Taken out of its slot while in use: a guest read can materialize another draw on this thread.
    private static MaterializationProgram.Output TakeOutput()
    {
        var output = _programOutput ?? new MaterializationProgram.Output();
        _programOutput = null;
        return output;
    }

    // The program's words as a full materialization would read them: written ranges in the table
    // and unusable image descriptors as zeros.
    private static bool EvaluateInto(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, MaterializationProgram.Output output)
    {
        if (!plan.MaterializationProgram!.TryRunInto(plan, inputs, checked(plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount), output))
        {
            return false;
        }

        var table = output.TableWords;
        foreach (var range in output.RangeValues)
        {
            if (!plan.WrittenRangeSlotByHandle.TryGetValue(range.Handle, out var slot))
            {
                continue;
            }

            var offset = (int)slot;
            table[offset] = (uint)range.Base;
            table[offset + 1] = (uint)(range.Base >> 32);
            table[offset + 2] = (uint)Math.Min(range.Size, uint.MaxValue);
        }

        var info = plan.Info;
        if (plan.NormalizeBufferDescriptor is { } normalizeBuffer)
        {
            for (var index = 0; index < info.Buffers.Count; index++)
            {
                var words = output.Source(index);
                var normalized = normalizeBuffer(words);
                if (normalized.Length != words.Length) return false;
                normalized.CopyTo(words);
            }
        }

        for (var index = 0; index < info.Images.Count; index++)
        {
            var words = output.Source(info.Buffers.Count + index);
            if (plan.NormalizeImageDescriptor is { } normalizeImage)
            {
                var normalized = normalizeImage(words, plan.Stage, plan.Hash, "direct");
                if (normalized.Length != words.Length) return false;
                normalized.CopyTo(words);
            }

            if (!ValidImageDescriptor(words, info.Images[index].R128))
            {
                words.Clear();
            }
        }

        return true;
    }

    private static bool SameAsOutput(ShaderResourcePlan plan, ResourceSnapshot snapshot, MaterializationProgram.Output output)
    {
        var info = plan.Info;
        if (snapshot.Buffers.Length != info.Buffers.Count || snapshot.Images.Length != info.Images.Count || snapshot.Samplers.Length < info.Samplers.Count ||
            !snapshot.FlattenedResourceTable.AsSpan().SequenceEqual(output.TableWords) || !snapshot.DeviceAddressRanges.AsSpan().SequenceEqual(output.RangeValues))
        {
            return false;
        }

        for (var index = 0; index < info.Buffers.Count; index++)
        {
            var words = output.Source(index);
            var cleared = words.Length == 4 && (words[3] >> 30) != 0;
            if (cleared ? snapshot.Buffers[index].AsSpan().ContainsAnyExcept(0u) : !snapshot.Buffers[index].AsSpan().SequenceEqual(words))
            {
                return false;
            }
        }

        var statistics = snapshot.MipStatistics;
        for (var index = 0; index < info.Images.Count; index++)
        {
            var words = output.Source(info.Buffers.Count + index);
            var known = snapshot.Images[index];
            if (statistics is null)
            {
                if (!known.AsSpan().SequenceEqual(words))
                {
                    return false;
                }

                continue;
            }

            if (known.Length != words.Length || !SameUnlessStatistics(known, words) ||
                (words.Length >= 7 && statistics[index] != ResourceSnapshot.MipStatisticsOf(words[5], words[6])))
            {
                return false;
            }
        }

        for (var index = 0; index < info.Samplers.Count; index++)
        {
            if (!snapshot.Samplers[index].AsSpan().SequenceEqual(output.Source(info.Buffers.Count + info.Images.Count + index)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameUnlessStatistics(ReadOnlySpan<uint> key, ReadOnlySpan<uint> current)
    {
        for (var word = 0; word < current.Length; word++)
        {
            var mask = word == 5 ? ResourceSnapshot.MipStatisticsEnableWordMask : word == 6 ? ResourceSnapshot.MipStatisticsSlotWordMask : 0u;
            if (((current[word] ^ key[word]) & ~mask) != 0)
            {
                return false;
            }
        }

        return true;
    }

    public static bool CanMaterializeFast(ShaderResourcePlan plan) =>
        plan.IndirectImages.Count == 0 && plan.BufferCandidateTables.Count == 0 && plan.MaterializationProgram is not null;

    // Materializes every draw from the plan's program: no cached snapshot to prove. The
    // specialization is a function of descriptor words only, so equal words reuse it.
    public static bool MaterializeFast(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure)
    {
        using var totalProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Total);
        failure = ResourceMaterializationFailure.Other;
        _lastFastOutcome = MaterializationTally.Outcome.FastFallback;
        if (plan.RequiresSpecializationMemory && inputs.ReadCleanMemory is null)
        {
            return false;
        }

        if (inputs.TablePhase is not null)
        {
            return MaterializeFastFromSnapshot(plan, inputs, ref snapshot, ref specialization, out failure);
        }

        var output = TakeOutput();
        try
        {
            if (!EvaluateInto(plan, inputs, output))
            {
                _programFallbacks.Increment();
                return MaterializeFastFromSnapshot(plan, inputs, ref snapshot, ref specialization, out failure);
            }

            _programRuns.Increment();
            _fastMaterializations.Increment();
            _lastFastOutcome = MaterializationTally.Outcome.FastMiss;
            return MaterializeFromOutput(plan, inputs, output, ref snapshot, ref specialization, out failure);
        }
        finally
        {
            _programOutput = output;
        }
    }

    private static bool MaterializeFromOutput(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, MaterializationProgram.Output output,
        ref ResourceSnapshot snapshot, ref ResourceSpecialization specialization, out ResourceMaterializationFailure failure)
    {
        var info = plan.Info;
        var bufferCount = info.Buffers.Count;
        var imageCount = info.Images.Count;
        var samplerCount = info.Samplers.Count;
        var memos = Volatile.Read(ref plan.SpecializationMemos);
        SpecializationMemo? memo = null;
        var statisticsDiffer = false;
        if (memos is not null)
        {
            foreach (var candidate in memos)
            {
                if (candidate is SpecializationMemo entry && MatchesOutput(entry, output, bufferCount, imageCount, samplerCount, out statisticsDiffer) &&
                    (!statisticsDiffer || entry.Specialization.ImageDescriptorGroups.SequenceEqual(OutputImageGroups(output, bufferCount, imageCount))))
                {
                    memo = entry;
                    break;
                }
            }
        }

        var buffers = new uint[bufferCount][];
        for (var index = 0; index < bufferCount; index++)
        {
            var words = output.Source(index);
            buffers[index] = memo is not null && (words[3] >> 30) != 0 ? new uint[words.Length] : words.ToArray();
        }

        var table = output.TableWords.ToArray();
        var ranges = output.RangeValues.ToArray();
        var userData = inputs.UserData is uint[] array ? array : inputs.UserData.ToArray();
        if (memo is not null)
        {
            _fastSpecializationHits.Increment();
            _lastFastOutcome = MaterializationTally.Outcome.FastHit;
            uint[]? statistics = null;
            if (statisticsDiffer)
            {
                _fastStatisticsHits.Increment();
                statistics = new uint[imageCount];
                for (var index = 0; index < imageCount; index++)
                {
                    var words = output.Source(bufferCount + index);
                    statistics[index] = words.Length >= 7 ? ResourceSnapshot.MipStatisticsOf(words[5], words[6]) : ResourceSnapshot.NoMipStatistics;
                }
            }

            snapshot = new ResourceSnapshot
            {
                Buffers = buffers,
                Images = memo.Images,
                Samplers = memo.Samplers,
                FlattenedResourceTable = table,
                UserData = userData,
                DeviceAddressRanges = ranges,
                MipStatistics = statistics,
                Origin = new FastOrigin(plan, inputs),
            };
            specialization = memo.Specialization;
            failure = ResourceMaterializationFailure.None;
            return true;
        }

        var images = new uint[imageCount][];
        for (var index = 0; index < imageCount; index++)
        {
            images[index] = output.Source(bufferCount + index).ToArray();
        }

        var samplers = new uint[samplerCount][];
        for (var index = 0; index < samplerCount; index++)
        {
            samplers[index] = output.Source(bufferCount + imageCount + index).ToArray();
        }

        var materialized = new MaterializedSnapshot { Buffers = buffers, Images = images, Samplers = samplers, FlattenedTable = table, UserData = userData };
        return SpecializeAndRemember(plan, inputs, materialized, ranges, memos, ref snapshot, ref specialization, out failure);
    }

    private static List<uint> OutputImageGroups(MaterializationProgram.Output output, int bufferCount, int imageCount)
    {
        var images = new uint[imageCount][];
        for (var index = 0; index < imageCount; index++)
        {
            images[index] = output.Source(bufferCount + index).ToArray();
        }

        return DescriptorBindingAliases.Group(images);
    }

    private static bool MatchesOutput(SpecializationMemo memo, MaterializationProgram.Output output, int bufferCount, int imageCount, int samplerCount,
        out bool statisticsDiffer)
    {
        statisticsDiffer = false;
        if (memo.KeyImages.Length != imageCount || memo.KeySamplers.Length != samplerCount || memo.KeyBuffers.Length != bufferCount * 2)
        {
            return false;
        }

        for (var index = 0; index < bufferCount; index++)
        {
            var words = output.Source(index);
            if (words.Length != 4 || words[1] >> 16 != memo.KeyBuffers[index * 2] || words[3] != memo.KeyBuffers[index * 2 + 1])
            {
                return false;
            }
        }

        for (var index = 0; index < samplerCount; index++)
        {
            if (!output.Source(bufferCount + imageCount + index).SequenceEqual(memo.KeySamplers[index]))
            {
                return false;
            }
        }

        for (var index = 0; index < imageCount; index++)
        {
            var current = output.Source(bufferCount + index);
            var key = memo.KeyImages[index];
            if (current.Length != key.Length || !SameUnlessStatistics(key, current))
            {
                return false;
            }

            statisticsDiffer |= !current.SequenceEqual(key);
        }

        return true;
    }

    // The program failed for this draw: the general evaluator materializes it, still remembered.
    private static bool MaterializeFastFromSnapshot(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        ref ResourceSnapshot snapshot,
        ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure)
    {
        if (!MaterializeSnapshot(plan, inputs, captureSelectorDiagnostic: false, out var materialized, out var programRanges, out failure))
        {
            return false;
        }

        if (plan.NormalizeBufferDescriptor is { } normalize)
        {
            for (var index = 0; index < materialized.Buffers.Length; index++)
                materialized.Buffers[index] = normalize(materialized.Buffers[index]);
        }

        _fastMaterializations.Increment();
        var ranges = programRanges ?? DeviceAddressRangePlanner.Evaluate(plan, inputs);
        foreach (var range in ranges)
        {
            if (!plan.WrittenRangeSlotByHandle.TryGetValue(range.Handle, out var slot))
            {
                continue;
            }

            var offset = (int)slot;
            materialized.FlattenedTable[offset] = (uint)range.Base;
            materialized.FlattenedTable[offset + 1] = (uint)(range.Base >> 32);
            materialized.FlattenedTable[offset + 2] = (uint)Math.Min(range.Size, uint.MaxValue);
        }

        var memos = Volatile.Read(ref plan.SpecializationMemos);
        SpecializationMemo? memo = null;
        var statisticsDiffer = false;
        if (memos is not null)
        {
            foreach (var candidate in memos)
            {
                if (candidate is SpecializationMemo entry && MatchesMemo(entry, materialized, out statisticsDiffer) &&
                    (!statisticsDiffer || entry.Specialization.ImageDescriptorGroups.SequenceEqual(DescriptorBindingAliases.Group(materialized.Images))))
                {
                    memo = entry;
                    break;
                }
            }
        }

        if (memo is not null)
        {
            foreach (var words in materialized.Buffers)
            {
                if ((words[3] >> 30) != 0)
                {
                    Array.Clear(words);
                }
            }

            _fastSpecializationHits.Increment();
            uint[]? statistics = null;
            if (statisticsDiffer)
            {
                _fastStatisticsHits.Increment();
                statistics = new uint[materialized.Images.Length];
                for (var index = 0; index < statistics.Length; index++)
                {
                    var words = materialized.Images[index];
                    statistics[index] = words.Length >= 7 ? ResourceSnapshot.MipStatisticsOf(words[5], words[6]) : ResourceSnapshot.NoMipStatistics;
                }
            }

            snapshot = new ResourceSnapshot
            {
                Buffers = materialized.Buffers,
                Images = memo.Images,
                Samplers = memo.Samplers,
                FlattenedResourceTable = materialized.FlattenedTable,
                UserData = materialized.UserData,
                DeviceAddressRanges = ranges,
                MipStatistics = statistics,
                Origin = new FastOrigin(plan, inputs),
            };
            specialization = memo.Specialization;
            failure = ResourceMaterializationFailure.None;
            return true;
        }

        return SpecializeAndRemember(plan, inputs, materialized, ranges, memos, ref snapshot, ref specialization, out failure);
    }

    private static bool SpecializeAndRemember(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, MaterializedSnapshot materialized,
        DeviceAddressRange[] ranges, object?[]? memos, ref ResourceSnapshot snapshot, ref ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure)
    {
        var keyImages = new uint[materialized.Images.Length][];
        for (var index = 0; index < keyImages.Length; index++)
        {
            keyImages[index] = [.. materialized.Images[index]];
        }

        var keySamplers = new uint[materialized.Samplers.Length][];
        for (var index = 0; index < keySamplers.Length; index++)
        {
            keySamplers[index] = [.. materialized.Samplers[index]];
        }

        var keyBuffers = new uint[materialized.Buffers.Length * 2];
        var keyable = true;
        for (var index = 0; index < materialized.Buffers.Length; index++)
        {
            var words = materialized.Buffers[index];
            if (words.Length != 4)
            {
                keyable = false;
                break;
            }

            keyBuffers[index * 2] = words[1] >> 16;
            keyBuffers[index * 2 + 1] = words[3];
        }

        if (!BuildSpecialization(plan, materialized, out var nextSnapshot, out var nextSpecialization, out failure, null))
        {
            return false;
        }

        if (keyable)
        {
            memos ??= Interlocked.CompareExchange(ref plan.SpecializationMemos, new object?[SpecializationMemoEntries], null) ?? plan.SpecializationMemos!;
            var slot = (int)((uint)Interlocked.Increment(ref plan.NextSpecializationMemo) % SpecializationMemoEntries);
            Volatile.Write(ref memos[slot], new SpecializationMemo
            {
                KeyImages = keyImages,
                KeySamplers = keySamplers,
                KeyBuffers = keyBuffers,
                Images = nextSnapshot.Images,
                Samplers = nextSnapshot.Samplers,
                Specialization = nextSpecialization,
            });
        }

        snapshot = new ResourceSnapshot
        {
            Buffers = nextSnapshot.Buffers,
            Images = nextSnapshot.Images,
            Samplers = nextSnapshot.Samplers,
            FlattenedResourceTable = nextSnapshot.FlattenedTable,
            UserData = nextSnapshot.UserData,
            DeviceAddressRanges = ranges,
            Origin = new FastOrigin(plan, inputs),
        };
        specialization = nextSpecialization;
        failure = ResourceMaterializationFailure.None;
        return true;
    }

    private static bool MatchesMemo(SpecializationMemo memo, MaterializedSnapshot snapshot, out bool statisticsDiffer)
    {
        statisticsDiffer = false;
        if (memo.KeyImages.Length != snapshot.Images.Length || memo.KeySamplers.Length != snapshot.Samplers.Length ||
            memo.KeyBuffers.Length != snapshot.Buffers.Length * 2)
        {
            return false;
        }

        for (var index = 0; index < snapshot.Buffers.Length; index++)
        {
            var words = snapshot.Buffers[index];
            if (words.Length != 4 || words[1] >> 16 != memo.KeyBuffers[index * 2] || words[3] != memo.KeyBuffers[index * 2 + 1])
            {
                return false;
            }
        }

        for (var index = 0; index < snapshot.Samplers.Length; index++)
        {
            if (!snapshot.Samplers[index].AsSpan().SequenceEqual(memo.KeySamplers[index]))
            {
                return false;
            }
        }

        for (var index = 0; index < snapshot.Images.Length; index++)
        {
            var current = snapshot.Images[index];
            var key = memo.KeyImages[index];
            if (current.Length != key.Length)
            {
                return false;
            }

            for (var word = 0; word < current.Length; word++)
            {
                if (current[word] == key[word])
                {
                    continue;
                }

                var mask = word == 5 ? ResourceSnapshot.MipStatisticsEnableWordMask : word == 6 ? ResourceSnapshot.MipStatisticsSlotWordMask : 0u;
                if (((current[word] ^ key[word]) & ~mask) != 0)
                {
                    return false;
                }

                statisticsDiffer = true;
            }
        }

        return true;
    }

    // Evaluates only the flattened table, laid out as a full materialization lays it out
    // before specialization; the written device-address slots are left zero for the caller.
    public static bool TryEvaluateTable(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] table)
    {
        var additionalTableWords = checked(plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount);
        // Only this table-only caller can omit descriptor branch activity.
        if (RuntimeValueEvaluator.HasResourceStructure(plan) && plan.CompiledTableTransaction is { } transaction)
        {
            ResourceMaterializationCache.RecordEvaluationPath(plan, "compiled_table");
            return transaction.Evaluate(inputs, additionalTableWords, out _, out table);
        }
        return RuntimeValueEvaluator.EvaluateSources(plan, [], inputs, plan.CleanFlatSlots, evaluateTable: true,
            out _, out table, out _, additionalTableWords: additionalTableWords);
    }

    // ---- snapshot ----

    internal static Action<ShaderResourcePlan, ResourceRuntimeInputs, List<DescriptorWords>, uint[], DeviceAddressRange[]>? ProgramCheck;

    private static readonly ThreadCounter _programRuns = new();
    private static readonly ThreadCounter _programFallbacks = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long[]> _programIneligible = new();

    public static string TakeProgramReport()
    {
        var parts = new List<string>();
        foreach (var (reason, count) in _programIneligible)
        {
            var taken = Interlocked.Exchange(ref count[0], 0);
            if (taken != 0)
            {
                parts.Add(reason + ":" + taken.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return FormattableString.Invariant($"program_runs={_programRuns.Take()} program_fallbacks={_programFallbacks.Take()} ") +
            "program_ineligible=" + string.Join(",", parts);
    }

    private static void NoteProgramIneligible(string reason) =>
        Interlocked.Increment(ref _programIneligible.GetOrAdd(reason, static _ => new long[1])[0]);

    private static bool MaterializeSnapshot(ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        bool captureSelectorDiagnostic, out MaterializedSnapshot snapshot, out DeviceAddressRange[]? ranges, out ResourceMaterializationFailure failure)
    {
        using var snapshotProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Snapshot);
        failure = ResourceMaterializationFailure.Other;
        snapshot = new MaterializedSnapshot();
        ranges = null;
        if (plan.RequiresSpecializationMemory && inputs.ReadCleanMemory is null)
        {
            return false;
        }

        var additionalTableWords = checked(plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount);
        List<DescriptorWords> values;
        uint[] table;
        bool[] activeSources = [];
        var program = inputs.TablePhase is null && !captureSelectorDiagnostic ? plan.MaterializationProgram : null;
        if (program is not null && program.TryRun(plan, inputs, additionalTableWords, out values, out table, out var programRanges, out activeSources))
        {
            _programRuns.Increment();
            ProgramCheck?.Invoke(plan, inputs, values, table, programRanges);
            ranges = programRanges;
        }
        else
        {
            if (program is not null)
            {
                _programFallbacks.Increment();
            }
            else
            {
                NoteProgramIneligible(inputs.TablePhase is not null ? "table_phase" : captureSelectorDiagnostic ? "diagnostic" : plan.MaterializationProgramIneligibility);
            }
            if (!RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources, inputs, plan.CleanFlatSlots,
                evaluateTable: true, out values, out table, out activeSources, additionalTableWords))
            {
                SpecializationFailed(DiagnoseSnapshotEvaluationFailure(plan, inputs, activeSources));
                return false;
            }
        }

        var cursor = 0;
        snapshot.Buffers = new uint[plan.Info.Buffers.Count][];
        for (var index = 0; index < snapshot.Buffers.Length; index++)
            snapshot.Buffers[index] = values[cursor++].Dwords;
        snapshot.FlattenedTable = table;
        snapshot.Images = new uint[plan.Info.Images.Count][];
        for (var imageIndex = 0; imageIndex < plan.Info.Images.Count; imageIndex++)
        {
            var image = plan.Info.Images[imageIndex];
            var source = plan.DescriptorSources[(int)image.Source];
            if (source.IndirectImage is { } indirect)
            {
                if (activeSources.Length != 0 && !activeSources[image.Source])
                {
                    snapshot.Images[imageIndex] = new uint[8];
                    continue;
                }
                var cleanInputs = inputs.WithReader(inputs.ReadCleanMemory);
                if (indirect.DirectCandidates is { } directCandidates)
                {
                    if (!RuntimeValueEvaluator.EvaluateSources(plan, directCandidates.Select(candidate => candidate.Source).ToArray(),
                        cleanInputs, [], evaluateTable: false, out var descriptors, out _))
                    {
                        return false;
                    }
                    var directTable = new IndirectImageTable { Resource = (uint)imageIndex };
                    for (var candidateIndex = 0; candidateIndex < descriptors.Count; candidateIndex++)
                    {
                        var descriptor = new DescriptorWords(NormalizeImage(plan, descriptors[candidateIndex].Dwords, "direct-candidate"));
                        if (!UsableImageCandidate(descriptor.Dwords, image.R128))
                            descriptor = DescriptorWords.Empty(8);
                        var existing = directTable.Descriptors.FindIndex(candidate => candidate.SameAs(descriptor));
                        if (existing < 0)
                        {
                            existing = directTable.Descriptors.Count;
                            directTable.Descriptors.Add(descriptor);
                        }
                        directTable.Keys.Add(directCandidates[candidateIndex].Offset);
                        directTable.Candidates.Add((uint)existing);
                    }
                    snapshot.Images[imageIndex] = directTable.Descriptors[(int)directTable.Candidates[0]].Dwords;
                    if (directTable.Descriptors.Count > 1) snapshot.IndirectImages.Add(directTable);
                    continue;
                }
                if (indirect.WaveIndexed is { } waveIndexed)
                {
                    if (!RuntimeValueEvaluator.EvaluateSources(plan, [indirect.HeapSource], cleanInputs, [], evaluateTable: false,
                        out var waveSources, out _))
                        return false;
                    if (!MaterializeWaveIndexedImage(plan, indirect, waveIndexed, waveSources[0], image, image.R128, inputs, out var waveTable, out failure))
                        return false;
                    snapshot.Images[imageIndex] = waveTable.Descriptors[(int)waveTable.Candidates[0]].Dwords;
                    if (waveTable.Descriptors.Count > 1)
                    {
                        waveTable.Resource = (uint)imageIndex;
                        snapshot.IndirectImages.Add(waveTable);
                    }
                    continue;
                }
                if (indirect.Dense)
                {
                    if (!RuntimeValueEvaluator.EvaluateSources(plan, [indirect.HeapSource], cleanInputs, [], evaluateTable: false,
                        out var denseSources, out _))
                        return false;
                    if (!MaterializeDenseIndirectImage(plan, indirect, denseSources[0], image, image.R128, inputs, out var denseTable, out failure))
                        return false;
                    snapshot.Images[imageIndex] = denseTable.Descriptors[(int)denseTable.Candidates[0]].Dwords;
                    if (denseTable.Descriptors.Count > 1)
                    {
                        denseTable.Resource = (uint)imageIndex;
                        snapshot.IndirectImages.Add(denseTable);
                    }
                    continue;
                }
                IndirectImageTable indirectTable;
                if (!RuntimeValueEvaluator.EvaluateSources(plan, [indirect.MaterialSource, indirect.HeapSource], cleanInputs, [], evaluateTable: false, out var tables, out _) ||
                    !MaterializeIndirectImage(plan, indirect, tables[0], tables[1], image, inputs, captureSelectorDiagnostic, out indirectTable, out failure))
                {
                    if (!indirect.SharedSelector)
                    {
                        return false;
                    }

                    // The table is unreadable now: keep the null image this access read as before it had a table.
                    snapshot.Images[imageIndex] = new uint[8];
                    failure = ResourceMaterializationFailure.None;
                    continue;
                }

                snapshot.Images[imageIndex] = indirectTable.Descriptors[(int)indirectTable.Candidates[0]].Dwords;
                if (indirectTable.Descriptors.Count > 1)
                {
                    indirectTable.Resource = (uint)imageIndex;
                    indirectTable.Shared = indirect.SharedSelector;
                    snapshot.IndirectImages.Add(indirectTable);
                }
            }
            else
            {
                var descriptor = new DescriptorWords(NormalizeImage(plan, values[cursor++].Dwords, "direct"));
                if (!ValidImageDescriptor(descriptor.Dwords, image.R128))
                {
                    descriptor = DescriptorWords.Empty(descriptor.DwordCount);
                }

                snapshot.Images[imageIndex] = descriptor.Dwords;
            }
        }

        FitSharedImageTables(plan, snapshot);
        snapshot.Samplers = new uint[plan.Info.Samplers.Count][];
        for (var index = 0; index < snapshot.Samplers.Length; index++)
            snapshot.Samplers[index] = values[cursor++].Dwords;

        // Bounded runtime V# tables: the whole table is read and validated before it is
        // published, so a single unreadable candidate leaves the previous snapshot intact.
        foreach (var candidatePlan in plan.BufferCandidateTables)
        {
            if (candidatePlan.SourceSrtResource == DescriptorConstants.NoIndex ||
                !RuntimeValueEvaluator.EvaluateSources(plan, [candidatePlan.SourceSrtResource], inputs, [], evaluateTable: false,
                    out var srtSources, out _) || srtSources.Count == 0)
            {
                return false;
            }

            if (!MaterializeBufferCandidateTable(plan, candidatePlan, srtSources[0], inputs, out var candidateTable))
            {
                return false;
            }

            snapshot.BufferCandidateTables.Add(candidateTable);
        }

        snapshot.UserData = inputs.UserData.ToArray();
        return true;
    }

    // Reads every candidate the loop guard can select from the guest SRT, validates it and
    // records the run-time probe key (its base-address low dword) -> candidate mapping.
    // Distinct keys are required so the mapping is unambiguous; identical descriptors share
    // one candidate.
    private static bool MaterializeBufferCandidateTable(
        ShaderResourcePlan plan,
        BufferCandidateTablePlan table,
        DescriptorWords srt,
        ResourceRuntimeInputs inputs,
        out BufferCandidateTable result)
    {
        result = new BufferCandidateTable { MemoryIndex = table.MemoryIndices.Count != 0 ? table.MemoryIndices[0] : -1 };
        if (srt.DwordCount != 4)
        {
            return false;
        }

        int count;
        if (table.IsStaticallyBounded)
        {
            count = table.Count;
        }
        else
        {
            if (table.Limit is null)
            {
                return false;
            }

            using var scratch = RuntimeEvaluationScratch.Rent();
            var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs);
            if (!evaluator.Evaluate(table.Limit, out var limit) || !table.TryResolveCount(limit, out count))
            {
                return false;
            }
        }

        if (count <= 0 || count > table.Cap)
        {
            return false;
        }

        var keyToCandidate = new Dictionary<uint, uint>();
        for (var index = 0; index < count; index++)
        {
            var offset = unchecked(table.MinOffset + (uint)index * table.CandidateSpacing);
            var words = new uint[4];
            for (uint dword = 0; dword < 4; dword++)
            {
                if (!ReadScalarBufferWord(srt.Dwords, offset, dword * sizeof(uint), inputs, out words[dword]))
                {
                    return false;
                }
            }

            // A descriptor whose reserved bits are set is unbound and reads as zero.
            if ((words[3] >> 30) != 0)
            {
                Array.Clear(words);
            }

            var descriptor = new DescriptorWords(words);
            if (keyToCandidate.TryGetValue(words[0], out var existing))
            {
                // Two different descriptors sharing one probe key cannot be told apart at
                // run time; reject precisely instead of guessing.
                if (!result.Descriptors[(int)existing].SameAs(descriptor))
                {
                    return false;
                }

                continue;
            }

            var candidate = (uint)result.Descriptors.Count;
            result.Descriptors.Add(descriptor);
            keyToCandidate[words[0]] = candidate;
        }

        result.Keys = [.. keyToCandidate.Keys];
        result.Candidates = result.Keys.Select(key => keyToCandidate[key]).ToList();
        return true;
    }

    private static string DiagnoseSnapshotEvaluationFailure(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<bool> activeSources)
    {
        var cleanEvaluator = new RuntimeValueEvaluator(
            plan,
            inputs.WithReader(inputs.ReadCleanMemory));
        var evaluator = new RuntimeValueEvaluator(
            plan,
            inputs,
            plan.CleanFlatSlots,
            cleanEvaluator);

        foreach (var sourceIndex in plan.MaterializationSources)
        {
            if (sourceIndex >= plan.DescriptorSources.Count)
            {
                return $"descriptor source {sourceIndex} is outside the source table";
            }

            if (activeSources.Count != 0 && !activeSources[(int)sourceIndex])
            {
                continue;
            }

            var source = plan.DescriptorSources[(int)sourceIndex];
            for (var dword = 0; dword < source.Dwords.Length; dword++)
            {
                if (!evaluator.Evaluate(source.Dwords[dword], out _))
                {
                    return $"descriptor source {sourceIndex} dword {dword} cannot be evaluated: " +
                        DescribeEvaluationValue(plan, inputs, evaluator, source.Dwords[dword]);
                }
            }
        }

        foreach (var read in plan.TableReads)
        {
            var clean = read.FlatOffset < plan.CleanFlatSlots.Count &&
                plan.CleanFlatSlots[(int)read.FlatOffset] != 0;
            var selected = clean ? cleanEvaluator : evaluator;
            if (read.FlatOffset >= plan.TableReads.Count || !selected.Evaluate(read.Value, out _))
            {
                return $"resource table read {read.FlatOffset} cannot be evaluated: " +
                    DescribeEvaluationValue(plan, inputs, selected, read.Value);
            }
        }

        return "descriptor snapshot evaluation failed without an isolated source";
    }

    private static string DescribeEvaluationValue(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        RuntimeValueEvaluator evaluator,
        ScalarValue value,
        int depth = 0)
    {
        if (depth >= 6)
        {
            return value.ToString();
        }

        if (value.Kind == ScalarValueKind.ResourceTableWord)
        {
            var slot = (int)value.Payload;
            if ((uint)slot < plan.TableReads.Count)
            {
                var read = plan.TableReads[slot];
                return $"table-slot={slot} flat={read.FlatOffset} value=" +
                    DescribeEvaluationValue(plan, inputs, evaluator, read.Value, depth + 1);
            }

            return $"table-slot={slot} (outside table)";
        }

        if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord &&
            value.MemoryIndex >= 0 && value.MemoryIndex < plan.Memory.Count)
        {
            var memory = plan.Memory[value.MemoryIndex];
            var detail = $"{value.Kind} memory={value.MemoryIndex} pc=0x{memory.Pc:X8} " +
                $"opcode={memory.Opcode} offset={memory.Offset} operands=[{string.Join(", ", value.Operands.Select(operand =>
                    DescribeEvaluationValue(plan, inputs, evaluator, operand, depth + 1)))}]";
            if (value.Operands.Length >= 2 && value.Operands[0].Operands.Length >= 2 &&
                evaluator.EvaluateWide(value.Operands[0].Operands[0], out var low) &&
                evaluator.EvaluateWide(value.Operands[0].Operands[1], out var high) &&
                evaluator.EvaluateWide(value.Operands[1], out var dynamicOffset))
            {
                var baseAddress = ((high << 32) | (uint)low) & AddressMask;
                var relative = (long)(int)memory.Offset + (uint)dynamicOffset;
                if (relative >= 0 && baseAddress <= AddressMask - (ulong)relative)
                {
                    var address = (baseAddress + (ulong)relative) & ~3ul;
                    var regular = inputs.ReadMemory is not null && inputs.ReadMemory(address, out _);
                    var clean = inputs.ReadCleanMemory is not null && inputs.ReadCleanMemory(address, out _);
                    detail += $" base=0x{baseAddress:X16} dynamic=0x{dynamicOffset:X} " +
                        $"address=0x{address:X16} readable={regular} clean={clean}";
                }
            }

            return detail;
        }

        if (value.Kind is ScalarValueKind.AddressHandle or ScalarValueKind.BufferHandle or
            ScalarValueKind.ImageHandle or ScalarValueKind.SamplerHandle)
        {
            return $"{value.Kind}[{string.Join(", ", value.Operands.Select(operand =>
                DescribeEvaluationValue(plan, inputs, evaluator, operand, depth + 1)))}]";
        }

        if (value.Kind is ScalarValueKind.Phi or ScalarValueKind.Select or ScalarValueKind.Operation)
        {
            return $"{value} operands=[{string.Join(", ", value.Operands.Select(operand =>
                DescribeEvaluationValue(plan, inputs, evaluator, operand, depth + 1)))}]";
        }

        return value.ToString();
    }

    private static readonly HashSet<uint> NulledSampledFormats = new();

    private static void ReportNulledSampledFormat(uint format)
    {
        lock (NulledSampledFormats)
        {
            if (NulledSampledFormats.Add(format))
            {
                Console.Error.WriteLine($"[GPU][WARN] A sampled image descriptor uses unsupported format {format}; it is bound as a null texture.");
            }
        }
    }

    // Frontend-specific image conversion must precede validation, including indirect
    // candidates. AGC frontends have no hook and keep their exact existing words.
    private static uint[] NormalizeImage(ShaderResourcePlan plan, uint[] words, string source) =>
        plan.NormalizeImageDescriptor is { } normalize ? normalize(words, plan.Stage, plan.Hash, source) : words;

    private static bool NullImageDescriptor(ReadOnlySpan<uint> descriptor) =>
        descriptor[0] == 0 && (descriptor[1] & 0xFF) == 0;

    private static bool ReservedImageBitsClear(ReadOnlySpan<uint> descriptor)
    {
        ReadOnlySpan<uint> reserved =
        [
            0x00000000u, 0x20000000u, 0xf0003000u, 0x00000000u,
            0xe000e000u, 0xf9000000u, 0x00007b00u, 0x00000000u,
        ];
        for (var dword = 0; dword < reserved.Length; dword++)
        {
            if ((descriptor[dword] & reserved[dword]) != 0)
                return false;
        }
        return true;
    }

    private static bool ValidImageDescriptor(ReadOnlySpan<uint> descriptor, bool r128)
    {
        var type = GuestImageFormat.ImageTypeOf(descriptor);
        var format = GuestImageFormat.FormatOf(descriptor);
        if (type < GuestImageFormat.ImageType1D || format == GuestImageFormat.Invalid || format > GuestImageFormat.MaxFormat)
        {
            return false;
        }

        if (r128 && type is not (GuestImageFormat.ImageType1D or GuestImageFormat.ImageType2D or GuestImageFormat.ImageType2DMsaa))
        {
            return false;
        }

        if (type is GuestImageFormat.ImageTypeCube or
            GuestImageFormat.ImageType1DArray or
            GuestImageFormat.ImageType2DArray or
            GuestImageFormat.ImageType2DMsaaArray)
        {
            var lastSlice = descriptor[4] & 0x1FFF;
            var baseSlice = (descriptor[4] >> 16) & 0x1FFF;
            if (baseSlice > lastSlice)
            {
                return false;
            }

        }

        if (type is GuestImageFormat.ImageType2DMsaa or GuestImageFormat.ImageType2DMsaaArray)
        {
            var baseLevel = (descriptor[3] >> 12) & 0xF;
            var fragments = (descriptor[3] >> 16) & 0xF;
            var maxMip = (descriptor[5] >> 4) & 0xF;
            return baseLevel == 0 && fragments is >= 1 and <= 3 && (r128 || maxMip == fragments);
        }

        return true;
    }

    // The instruction samples one kind of image. A descriptor of another kind (the default heap entry, or a key read from some
    // other field of the record) is not something it can sample, so a shared selector reads it as null.
    private static bool SamplesAs(ReadOnlySpan<uint> descriptor, ImageResource image)
    {
        var dimension = DescriptorDimension(descriptor, image.Dimension);
        return dimension == image.Dimension ||
            image.Dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray &&
            dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray;
    }

    private static bool CompatibleCandidates(ImageSpecialization image, ImageSpecialization exemplar, bool separateSampledDimensions)
    {
        // Each sampled candidate has its own binding and coordinate width.
        var compatibleDimensions = image.Dimension == exemplar.Dimension ||
            separateSampledDimensions && !image.Cube && !exemplar.Cube &&
            image.Dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray &&
            exemplar.Dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray;
        return image.NumericClass == exemplar.NumericClass && compatibleDimensions &&
            image.MipCount == exemplar.MipCount && image.ConversionFormat == exemplar.ConversionFormat &&
            image.ShaderSwizzle == exemplar.ShaderSwizzle && image.Cube == exemplar.Cube;
    }

    private static bool UsableImageCandidate(ReadOnlySpan<uint> candidate, bool r128) =>
        !NullImageDescriptor(candidate) && ValidImageDescriptor(candidate, r128) && ReservedImageBitsClear(candidate) &&
        GuestImageFormat.SampledNumericClass(GuestImageFormat.FormatOf(candidate)) != ImageNumericClass.Unsupported;

    // A call record's image the host can give a float view without a conversion in the shader: a valid sampled T# of a
    // float format. A freed T# (0xFEFEFEFE words) fails the reserved-bit check.
    public static bool IsFloatHeapImage(ReadOnlySpan<uint> words)
    {
        if (words.Length < 8 || !UsableImageCandidate(words, r128: false))
        {
            return false;
        }

        var format = GuestImageFormat.FormatOf(words);
        return GuestImageFormat.SampledNumericClass(format) == ImageNumericClass.Float &&
            ImageConversionFormat(format) == GuestImageFormat.Invalid;
    }

    private static ulong ScalarBufferSize(ReadOnlySpan<uint> descriptor)
    {
        var stride = (descriptor[1] >> 16) & 0x3FFF;
        return stride == 0 ? descriptor[2] : (ulong)stride * descriptor[2];
    }

    private static bool ReadScalarBufferWord(ReadOnlySpan<uint> descriptor, uint dynamicOffset, uint immediateOffset, ResourceRuntimeInputs inputs, out uint word)
    {
        word = 0;
        var byteOffset = (ulong)dynamicOffset + immediateOffset;
        var aligned = byteOffset & ~3ul;
        var size = ScalarBufferSize(descriptor);
        if (aligned > size || size - aligned < sizeof(uint))
        {
            return true;
        }

        var baseAddress = ((descriptor[0] | ((ulong)descriptor[1] << 32)) & AddressMask) & ~3ul;
        if (aligned > AddressMask - baseAddress)
        {
            return false;
        }

        var address = baseAddress + aligned;
        if (inputs.ReadCleanMemory is not null && inputs.ReadCleanMemory(address, out word))
        {
            return true;
        }

        return inputs.ReadMemory is not null && inputs.ReadMemory(address, out word);
    }

    // Enumerates every material key that can pass the table's bounds and reads the
    // heap descriptor each selects; stale or invalid descriptors become null.
    private static bool MaterializeIndirectImage(
        ShaderResourcePlan plan,
        IndirectImageSelector indirect,
        DescriptorWords material,
        DescriptorWords heap,
        ImageResource image,
        ResourceRuntimeInputs inputs,
        bool captureSelectorDiagnostic,
        out IndirectImageTable result,
        out ResourceMaterializationFailure failure)
    {
        failure = ResourceMaterializationFailure.Other;
        result = new IndirectImageTable();
        if (material.DwordCount != 4 || heap.DwordCount != 4)
        {
            return false;
        }

        var materialStride = (material.Dwords[1] >> 16) & 0x3FFF;
        if (materialStride != indirect.SelectorStride)
        {
            return false;
        }

        var period = 1ul << 32;
        var step = (ulong)BigInteger.GreatestCommonDivisor(indirect.SelectorStride, period);
        var residue = indirect.SelectorOffset % step;
        var size = ScalarBufferSize(material.Dwords);
        var limit = Math.Min(uint.MaxValue, size + 3);
        var probeCount = residue <= limit ? (limit - residue) / step + 1 : 0;
        uint[]? provenOffsets = null;
        var diagnostic = captureSelectorDiagnostic ? new IndirectSelectorDiagnostic() : null;
        if (indirect.SelectorValues is { } selectorValues)
        {
            var evaluated = selectorValues.TryEvaluate(plan, inputs, out var selectors, diagnostic);
            if (evaluated)
                provenOffsets = selectors.Select(selector => unchecked(selector * indirect.SelectorStride + indirect.SelectorOffset)).Distinct().ToArray();
            if (diagnostic is not null)
            {
                diagnostic.SelectionMode = evaluated ? "bounded" : "full_domain_evaluation_failed";
                diagnostic.SelectorIndices = selectors;
                diagnostic.ProvenOffsets = provenOffsets ?? [];
            }
        }
        var records = (ulong)material.Dwords[2];
        if (provenOffsets is null && (indirect.SharedSelector ? records : probeCount) > MaxIndirectImageProbes)
        {
            return false;
        }

        var keys = new List<uint> { 0 };
        var seen = new HashSet<uint> { 0 };
        // A loop counter walks the table record by record: the keys are the ones its records hold, not every dword
        // the stride lattice reaches. Anything past the last record reads as zero, which is key 0.
        var offsets = provenOffsets is not null ? provenOffsets.Select(offset => (ulong)offset) :
            indirect.SharedSelector ?
                Enumerable.Range(0, (int)records).Select(index => (ulong)unchecked(indirect.SelectorOffset + (uint)index * materialStride)) :
                Enumerable.Range(0, (int)probeCount).Select(index => residue + (ulong)index * step);
        foreach (var offset in offsets)
        {
            if (!ReadScalarBufferWord(material.Dwords, (uint)offset, indirect.MaterialImmediate, inputs, out var key))
            {
                return false;
            }

            if (seen.Add(key))
            {
                keys.Add(key);
                diagnostic?.KeyProbes.Add(new((uint)offset, key));
            }

        }

        var probed = new List<uint[]>(keys.Count);
        foreach (var key in keys)
        {
            var candidate = new uint[8];
            var heapOffset = key << 5;
            for (uint dword = 0; dword < 8; dword++)
            {
                if (!ReadScalarBufferWord(heap.Dwords, heapOffset, dword * sizeof(uint), inputs, out candidate[dword]))
                {
                    return false;
                }
            }

            candidate = NormalizeImage(plan, candidate, "indirect-heap");
            if (!UsableImageCandidate(candidate, image.R128) || (indirect.SharedSelector && !SamplesAs(candidate, image)))
            {
                Array.Clear(candidate);
            }
            probed.Add(candidate);
        }

        if (!FinishIndirectImage(
                probed,
                keys,
                out var table,
                out failure,
                keepFirstCandidates: indirect.SharedSelector))
        {
            return false;
        }

        table.MaterialDescriptor = material.Dwords;
        table.HeapDescriptor = heap.Dwords;
        table.SelectorStride = indirect.SelectorStride;
        table.SelectorOffset = indirect.SelectorOffset;
        table.SelectorDiagnostic = diagnostic;
        result = table;
        return true;
    }

    private static bool MaterializeDenseIndirectImage(
        ShaderResourcePlan plan,
        IndirectImageSelector indirect,
        DescriptorWords heap,
        ImageResource image,
        bool r128,
        ResourceRuntimeInputs inputs,
        out IndirectImageTable result,
        out ResourceMaterializationFailure failure)
    {
        failure = ResourceMaterializationFailure.Other;
        result = new IndirectImageTable();
        if (heap.DwordCount != 2 || indirect.KeyBound == 0 || indirect.KeyBound > MaxIndirectImageProbes || inputs.ReadCleanMemory is null)
            return false;

        var baseAddress = (((ulong)heap.Dwords[1] << 32) | heap.Dwords[0]) & AddressMask;
        var probed = new List<uint[]>((int)indirect.KeyBound);
        for (uint key = 0; key < indirect.KeyBound; key++)
        {
            var candidate = new uint[8];
            var entry = (ulong)indirect.TableOffset + ((ulong)key << 5);
            for (uint dword = 0; dword < 8; dword++)
            {
                var relative = entry + dword * sizeof(uint);
                if (relative > AddressMask || baseAddress > AddressMask - relative ||
                    !inputs.ReadCleanMemory(baseAddress + relative, out candidate[dword]))
                    return false;
            }

            inputs.TraceImageCandidate?.Invoke(image.Source, key, baseAddress, baseAddress + entry, candidate);
            candidate = NormalizeImage(plan, candidate, "dense-heap");
            if (!UsableImageCandidate(candidate, r128))
                Array.Clear(candidate);
            probed.Add(candidate);
        }

        return FinishIndirectImage(
            probed,
            Enumerable.Range(0, (int)indirect.KeyBound)
                .Select(index => unchecked(indirect.DynamicOffsetBase + ((uint)index << 5))),
            out result,
            out failure);
    }

    private static bool MaterializeWaveIndexedImage(
        ShaderResourcePlan plan,
        IndirectImageSelector indirect,
        WaveIndexedImageSelector wave,
        DescriptorWords heap,
        ImageResource image,
        bool r128,
        ResourceRuntimeInputs inputs,
        out IndirectImageTable result,
        out ResourceMaterializationFailure failure)
    {
        failure = ResourceMaterializationFailure.Other;
        result = new IndirectImageTable();
        if (heap.DwordCount != 2 || inputs.ReadCleanMemory is null || wave.IndexStride == 0)
            return false;

        var baseAddress = (((ulong)heap.Dwords[1] << 32) | heap.Dwords[0]) & AddressMask;
        if (!TryReadCleanWord(baseAddress, wave.MaskOffset, inputs, out var activeMask))
            return false;

        var keys = new SortedSet<uint>();
        for (uint bit = 0; bit < 32; bit++)
        {
            if ((activeMask & (1u << (int)bit)) == 0)
                continue;
            var indexOffset = (ulong)wave.IndexTableOffset + (ulong)bit * wave.IndexStride;
            if (!TryReadCleanWord(baseAddress, indexOffset, inputs, out var key))
                return false;
            // VCmpxLeI32 0, key suppresses signed-negative indices before ReadFirstLane.
            if ((key & 0x8000_0000u) == 0)
                keys.Add(key);
        }

        var probed = new List<uint[]>(Math.Max(1, keys.Count));
        var offsets = new List<uint>(Math.Max(1, keys.Count));
        foreach (var key in keys)
        {
            var candidate = new uint[8];
            var descriptorOffset = (ulong)indirect.TableOffset + ((ulong)key << 5);
            for (uint dword = 0; dword < candidate.Length; dword++)
            {
                if (!TryReadCleanWord(baseAddress, descriptorOffset + dword * sizeof(uint), inputs, out candidate[dword]))
                    return false;
            }

            candidate = NormalizeImage(plan, candidate, "wave-heap");
            if (!UsableImageCandidate(candidate, r128))
                Array.Clear(candidate);
            probed.Add(candidate);
            offsets.Add(unchecked(indirect.DynamicOffsetBase + (key << 5)));
        }

        if (probed.Count == 0)
        {
            probed.Add(new uint[8]);
            offsets.Add(0);
        }

        return FinishIndirectImage(probed, offsets, out result, out failure);
    }

    private static bool TryReadCleanWord(ulong baseAddress, ulong offset, ResourceRuntimeInputs inputs, out uint word)
    {
        word = 0;
        return offset <= AddressMask && baseAddress <= AddressMask - offset &&
            inputs.ReadCleanMemory is not null && inputs.ReadCleanMemory(baseAddress + offset, out word);
    }

    private static bool FinishIndirectImage(
        IReadOnlyList<uint[]> probed,
        IEnumerable<uint> keys,
        out IndirectImageTable result,
        out ResourceMaterializationFailure failure,
        bool keepFirstCandidates = false)
    {
        failure = ResourceMaterializationFailure.Other;
        result = new IndirectImageTable();
        using var keyEnumerator = keys.GetEnumerator();
        foreach (var candidate in probed)
        {
            keyEnumerator.MoveNext();
            var words = new DescriptorWords(candidate);
            var found = result.Descriptors.FindIndex(existing => existing.SameAs(words));
            if (found < 0)
            {
                // A table selected by a loop counter may name up to its largest layout (see FitSharedImageTables).
                if (result.Descriptors.Count >= (keepFirstCandidates ? MaxSharedCandidates + 1 : ShaderResourceInfo.MaxIndirectImageCandidates))
                {
                    if (keepFirstCandidates)
                    {
                        // The key is left out of the mapping and reads the first candidate, like any key the table lacks.
                        continue;
                    }

                    failure = ResourceMaterializationFailure.ImageCapacityExceeded;
                    return false;
                }
                found = result.Descriptors.Count;
                result.Descriptors.Add(words);
            }
            result.Keys.Add(keyEnumerator.Current);
            result.Candidates.Add((uint)found);
        }
        return true;
    }

    // A table selected by a loop counter keeps every volume its records name: a volume left out reads the first candidate,
    // and its light goes missing wherever it covers the view. The plan's loop-selected tables share one layout, the most
    // candidates and keys any of them has needed so far, rounded up to powers of two and never shrinking: a view that
    // names another set of volumes does not compile another pipeline. A table that names more than the largest layout
    // keeps the volumes of its first records, and one that names fewer pads with null images; a key whose candidate was
    // dropped is left out of the mapping and reads the table's first candidate, like any key the table lacks.
    private static void FitSharedImageTables(ShaderResourcePlan plan, MaterializedSnapshot snapshot)
    {
        var shared = new List<int>();
        for (var index = 0; index < plan.Info.Images.Count; index++)
        {
            if (plan.DescriptorSources[(int)plan.Info.Images[index].Source].IndirectImage is { SharedSelector: true })
            {
                shared.Add(index);
            }
        }

        if (shared.Count == 0)
        {
            return;
        }

        var neededCandidates = 0;
        var neededKeys = 0;
        foreach (var table in snapshot.IndirectImages.Where(table => table.Shared))
        {
            neededCandidates = Math.Max(neededCandidates, table.Descriptors.Count - 1);
            neededKeys = Math.Max(neededKeys, table.Keys.Count);
        }

        var candidateCapacity = Grow(ref plan.SharedImageCandidates, SizeClass(neededCandidates, MinSharedCandidates, MaxSharedCandidates));
        var keyCapacity = Grow(ref plan.SharedImageKeys, SizeClass(neededKeys, MinSharedKeyCapacity, MaxSharedKeyCapacity));
        foreach (var resource in shared)
        {
            var table = snapshot.IndirectImages.Find(entry => entry.Resource == resource);
            if (table is null)
            {
                continue;
            }

            var extra = candidateCapacity;
            // Whatever reads the first candidate is the same as a key the mapping lacks, so only the others are kept.
            table.KeyCapacity = keyCapacity;
            var keys = new List<uint> { 0 };
            var candidates = new List<uint> { 0 };
            for (var index = 0; index < table.Keys.Count && keys.Count < keyCapacity; index++)
            {
                var candidate = table.Candidates[index];
                if (candidate != 0 && candidate <= extra)
                {
                    keys.Add(table.Keys[index]);
                    candidates.Add(candidate);
                }
            }

            table.Keys = keys;
            table.Candidates = candidates;
            if (table.Descriptors.Count > extra + 1)
            {
                table.Descriptors.RemoveRange(extra + 1, table.Descriptors.Count - extra - 1);
            }

            while (table.Descriptors.Count < extra + 1)
            {
                table.Descriptors.Add(DescriptorWords.Empty(8));
            }
        }
    }

    // The power of two that holds count, between minimum and maximum.
    private static int SizeClass(int count, int minimum, int maximum) =>
        Math.Clamp((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, 1)), minimum, maximum);

    // Raises a plan's layout to size and returns the layout in force, which never shrinks.
    private static int Grow(ref int capacity, int size)
    {
        var current = Volatile.Read(ref capacity);
        while (current < size)
        {
            var seen = Interlocked.CompareExchange(ref capacity, size, current);
            if (seen == current)
            {
                return size;
            }

            current = seen;
        }

        return current;
    }

    // The words a table's key mapping reserves: a loop-selected table reserves its size class, so neither its search
    // depth nor the offsets of the tables after it move with each key.
    private static int MappingCapacity(IndirectImageTable table) =>
        table.Shared ? Math.Max(table.KeyCapacity, table.Keys.Count) : table.Keys.Count;

    // ---- specialization ----

    private static ImageDimension DescriptorDimension(ReadOnlySpan<uint> descriptor, ImageDimension requested)
    {
        var isArray = requested is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or ImageDimension.Dim2DMsaaArray;
        return GuestImageFormat.ImageTypeOf(descriptor) switch
        {
            GuestImageFormat.ImageType1D => ImageDimension.Dim1D,
            GuestImageFormat.ImageType1DArray => isArray ? ImageDimension.Dim1DArray : ImageDimension.Dim1D,
            GuestImageFormat.ImageType3D => ImageDimension.Dim3D,
            GuestImageFormat.ImageTypeCube => ImageDimension.Dim2DArray,
            GuestImageFormat.ImageType2DArray => isArray ? ImageDimension.Dim2DArray : ImageDimension.Dim2D,
            GuestImageFormat.ImageType2DMsaaArray => isArray ? ImageDimension.Dim2DMsaaArray : ImageDimension.Dim2DMsaa,
            GuestImageFormat.ImageType2D => ImageDimension.Dim2D,
            GuestImageFormat.ImageType2DMsaa => ImageDimension.Dim2DMsaa,
            _ => ImageDimension.Unknown,
        };
    }

    private static uint ImageConversionFormat(uint format) =>
        GuestImageFormat.Remap(format) != format ? format : GuestImageFormat.Invalid;

    private static bool RequiresPointSampler(ImageNumericClass numericClass, uint conversionFormat) =>
        numericClass == ImageNumericClass.Sint || conversionFormat != GuestImageFormat.Invalid;

    // The depth compare function (sampler word 0, bits 12..14) of the sampler paired
    // with an image; an image sampled with comparison always has one.
    private static int SamplerCompareFunction(ShaderResourceInfo info, MaterializedSnapshot snapshot, uint image)
    {
        foreach (var pair in info.SampledPairs)
        {
            if (pair.Image == image && pair.Sampler < snapshot.Samplers.Length && snapshot.Samplers[pair.Sampler].Length != 0)
            {
                return (int)((snapshot.Samplers[pair.Sampler][0] >> 12) & 0x7);
            }
        }

        return 0;
    }

    private static uint StorageMipCount(ImageResource image, ReadOnlySpan<uint> descriptor)
    {
        if (image.MipMode != ImageMipMode.DynamicStorage || NullImageDescriptor(descriptor))
        {
            return 1;
        }

        var baseLevel = (descriptor[3] >> 12) & 0xF;
        var last = (descriptor[3] >> 16) & 0xF;
        return baseLevel <= last ? last - baseLevel + 1 : 0;
    }

    private static bool Fail(string message)
    {
        SpecializationFailed(message);
        return false;
    }

    private static bool BuildSpecialization(
        ShaderResourcePlan plan,
        MaterializedSnapshot snapshot,
        out MaterializedSnapshot specializedSnapshot,
        out ResourceSpecialization specialization,
        out ResourceMaterializationFailure failure,
        Action<IndirectImageFailure>? captureIndirectImageFailure)
    {
        using var specializationProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Specialization);
        failure = ResourceMaterializationFailure.Other;
        specializedSnapshot = snapshot;
        specialization = new ResourceSpecialization();
        var info = plan.Info;
        var imageCount = info.Images.Count;
        var mappingWordCount = 0;
        foreach (var table in snapshot.IndirectImages)
        {
            if (table.Resource >= info.Images.Count || table.Descriptors.Count < 2)
            {
                return Fail("indirect image table has an invalid root or candidate count");
            }

            imageCount = checked(imageCount + table.Descriptors.Count - 1);
            mappingWordCount = checked(mappingWordCount + 1 + MappingCapacity(table) * 2);
        }

        foreach (var table in snapshot.BufferCandidateTables)
        {
            mappingWordCount = checked(mappingWordCount + 1 + table.Keys.Count * 2);
        }

        // Each draw owns these arrays. Only indirect candidates require a larger table.
        Array.Resize(ref snapshot.Images, imageCount);
        var mappingCursor = snapshot.FlattenedTable.Length;
        Array.Resize(ref snapshot.FlattenedTable, checked(mappingCursor + mappingWordCount));
        var imageCursor = info.Images.Count;
        var images = new List<ImageSpecialization>(imageCount);
        foreach (var image in info.Images)
        {
            images.Add(new ImageSpecialization(
                image.NumericClass, image.Dimension, image.MipCount, image.ConversionFormat, image.ShaderSwizzle,
                image.IndirectRoot, image.IndirectMappingOffset, image.IndirectSearchIterations, image.Cube));
        }

        foreach (var table in snapshot.IndirectImages)
        {
            var rootImage = images[(int)table.Resource];
            for (var candidate = 1; candidate < table.Descriptors.Count; candidate++)
            {
                images.Add(rootImage with { IndirectRoot = table.Resource });
                snapshot.Images[imageCursor++] = table.Descriptors[candidate].Dwords;
            }

            var mappingOffset = (uint)mappingCursor;
            images[(int)table.Resource] = rootImage with
            {
                IndirectRoot = table.Resource,
                IndirectMappingOffset = mappingOffset,
                IndirectSearchIterations = (uint)BitOperations.Log2((uint)MappingCapacity(table)) + 1,
            };
            mappingCursor += 1 + MappingCapacity(table) * 2;
            var order = Enumerable.Range(0, table.Keys.Count).OrderBy(index => table.Keys[index]).ToArray();
            snapshot.FlattenedTable[(int)mappingOffset] = (uint)table.Keys.Count;
            for (var entry = 0; entry < order.Length; entry++)
            {
                var source = order[entry];
                var offset = (int)mappingOffset + 1 + entry * 2;
                snapshot.FlattenedTable[offset] = table.Keys[source];
                snapshot.FlattenedTable[offset + 1] = table.Candidates[source];
            }

            snapshot.Images[(int)table.Resource] = table.Descriptors[0].Dwords;
        }

        var buffers = new List<BufferSpecialization>(info.Buffers.Count);
        for (var index = 0; index < info.Buffers.Count; index++)
        {
            var descriptor = snapshot.Buffers[index];
            if (descriptor.Length != 4)
            {
                return Fail($"buffer descriptor {index} has invalid width");
            }

            var words = descriptor;
            if ((words[3] >> 30) != 0)
            {
                Array.Clear(words);
            }

            var stride = (words[1] >> 16) & 0x3FFF;
            var swizzleEnabled = (words[1] >> 31) != 0;
            var indexStride = (words[3] >> 21) & 0x3;
            var addThreadId = (words[3] >> 23) & 0x1;
            var packedStride = stride | ((swizzleEnabled ? 1u : 0u) << 14) | (indexStride << 16) | (addThreadId << 20);
            var swizzle = stride != 0 && ((packedStride >> 14) & 1) != 0;
            if (stride == 0)
            {
                packedStride &= ~((1u << 14) | (3u << 16));
            }
            else if (!swizzle)
            {
                packedStride &= ~(3u << 16);
            }

            var formatted = info.Buffers[index].Formatted;
            buffers.Add(new BufferSpecialization(
                packedStride,
                formatted ? (words[3] >> 12) & 0x7F : DescriptorConstants.InvalidFormat,
                formatted ? words[3] & 0xFFF : DescriptorConstants.IdentityDestinationSelect));
        }

        for (var index = 0; index < images.Count; index++)
        {
            var descriptor = snapshot.Images[index];
            var image = images[index];
            var baseIndex = index < info.Images.Count ? (uint)index : image.IndirectRoot;
            if (baseIndex >= info.Images.Count)
            {
                return Fail($"image resource {index} has an invalid root");
            }

            var baseImage = info.Images[(int)baseIndex];
            if (baseImage.ResourceClass == ImageResourceClass.None || (baseImage.Atomic && baseImage.ResourceClass != ImageResourceClass.Storage))
            {
                return Fail($"image resource {index} has an invalid class");
            }

            var mipCount = StorageMipCount(baseImage, descriptor);
            if (mipCount == 0)
            {
                return Fail($"storage image descriptor {index} has an invalid mip range");
            }

            image = image with { MipCount = mipCount };
            if (index < info.Images.Count && plan.DescriptorSources[(int)baseImage.Source].CallRecord is { } callRecord)
            {
                // A call record's image is resolved for each record by the host: the instruction names its class.
                images[index] = image with
                {
                    NumericClass = ImageNumericClass.Float,
                    Dimension = baseImage.Dimension,
                    Cube = callRecord.Cube,
                };
                continue;
            }

            if (NullImageDescriptor(descriptor))
            {
                images[index] = image with
                {
                    // A null-bound atomic keeps its declared numeric class
                    // (Uint or, since float image atomics were added, Float)
                    // instead of assuming every atomic image is Uint.
                    NumericClass = baseImage.Atomic64
                        ? ImageNumericClass.Uint64
                        : baseImage.Atomic ? baseImage.NumericClass : ImageNumericClass.Float,
                    Dimension = ImageDimension.Dim2D,
                    Cube = false,
                };
                continue;
            }

            var dimension = DescriptorDimension(descriptor, baseImage.Dimension);
            if (dimension == ImageDimension.Unknown)
            {
                return Fail(
                    $"image descriptor {index} has unsupported type {GuestImageFormat.ImageTypeOf(descriptor)}: " +
                    string.Join(",", descriptor.Select(word => $"{word:x8}")));
            }

            var format = GuestImageFormat.FormatOf(descriptor);
            // Integer image atomics (Add/Smax/And/...) only ever operate on a
            // 32-bit uint UAV. The float image atomics (Fmin/Fmax/Fcmpswap,
            // MIMG op 0x1D-0x1F) are lowered as a compare-and-swap loop on the
            // raw bit pattern (Gen5SpirvTranslator) and legitimately target a
            // 32-bit float-format UAV instead - reject only formats that are
            // neither. An atomic on two data dwords works on a 64-bit texel,
            // which the 32_32 UINT format holds.
            if (baseImage.Atomic64
                ? format != GuestImageFormat.Format32x2Uint
                : baseImage.Atomic && format != GuestImageFormat.Format32Uint &&
                    GuestImageFormat.SampledNumericClass(format) != ImageNumericClass.Float)
            {
                return Fail($"atomic image descriptor {index} uses unsupported format {format}");
            }

            var storage = baseImage.ResourceClass == ImageResourceClass.Storage;
            image = image with
            {
                MipStatistics = RecordMipStatistics && plan.Stage == ShaderStage.Pixel && !storage && ((descriptor[5] >> 25) & 1) != 0,
            };
            var conversionFormat = ImageConversionFormat(format);
            var shaderSwizzle = storage || conversionFormat != GuestImageFormat.Invalid ? descriptor[3] & 0xFFF : image.ShaderSwizzle;
            var rawSintStorage = storage && format == GuestImageFormat.Format32Sint && baseImage.Written && !baseImage.Read && !baseImage.Atomic;
            var numericClass = GuestImageFormat.SampledNumericClass(format);
            if (storage)
            {
                if ((!rawSintStorage && numericClass == ImageNumericClass.Sint) || numericClass == ImageNumericClass.Unsupported)
                {
                    return Fail($"storage image descriptor {index} uses unsupported format {format}");
                }

                // Atomics use the uint view; float min/max compare-exchange its bits.
                if (rawSintStorage || (baseImage.Atomic && numericClass == ImageNumericClass.Float))
                {
                    numericClass = ImageNumericClass.Uint;
                }

                if (baseImage.Atomic64)
                {
                    numericClass = ImageNumericClass.Uint64;
                }
            }
            else if (numericClass == ImageNumericClass.Unsupported)
            {
                ReportNulledSampledFormat(format);
                Array.Clear(descriptor);
                images[index] = image with
                {
                    NumericClass = ImageNumericClass.Float,
                    Dimension = ImageDimension.Dim2D,
                    Cube = false,
                };
                continue;
            }
            else if (baseImage.DepthCompare && numericClass != ImageNumericClass.Float)
            {
                return Fail($"sampled image descriptor {index} uses unsupported format {format}");
            }

            images[index] = image with
            {
                Dimension = dimension,
                Cube = GuestImageFormat.ImageTypeOf(descriptor) == GuestImageFormat.ImageTypeCube,
                ConversionFormat = conversionFormat,
                ShaderSwizzle = shaderSwizzle,
                NumericClass = numericClass,
                EmulatedCompareFunction = !storage && baseImage.DepthCompare && !GuestImageFormat.HasDepthEquivalent(format)
                    ? SamplerCompareFunction(info, snapshot, baseIndex)
                    : -1,
            };
        }

        for (var rootIndex = 0; rootIndex < images.Count; rootIndex++)
        {
            var root = images[rootIndex];
            if (root.IndirectRoot != rootIndex)
            {
                continue;
            }

            var keyCount = root.IndirectMappingOffset < snapshot.FlattenedTable.Length ? snapshot.FlattenedTable[(int)root.IndirectMappingOffset] : 0;
            if (root.IndirectSearchIterations == 0 || keyCount < 2 ||
                (ulong)root.IndirectMappingOffset + 1 + (ulong)keyCount * 2 > (ulong)snapshot.FlattenedTable.Length)
            {
                return Fail("indirect image specialization has an invalid key mapping");
            }

            var exemplar = DescriptorConstants.NoIndex;
            var resourceCount = 0;
            for (var resource = 0; resource < images.Count; resource++)
            {
                if (images[resource].IndirectRoot != rootIndex)
                {
                    continue;
                }

                resourceCount++;
                if (exemplar == DescriptorConstants.NoIndex && !NullImageDescriptor(snapshot.Images[resource]))
                {
                    exemplar = (uint)resource;
                }
            }

            if (resourceCount < 2 || exemplar == DescriptorConstants.NoIndex)
            {
                return Fail("indirect image specialization has no typed candidate");
            }

            var imageClass = images[(int)exemplar];
            var separateSampledDimensions = info.Images[rootIndex].ResourceClass == ImageResourceClass.Sampled;
            var sharedSelector = plan.DescriptorSources[(int)info.Images[rootIndex].Source].IndirectImage is { SharedSelector: true };
            for (var candidate = 0; candidate < images.Count; candidate++)
            {
                var image = images[candidate];
                if (image.IndirectRoot != rootIndex)
                {
                    continue;
                }

                // A shared selector read such a table as null before it had one: a candidate of another class reads as null too.
                if (sharedSelector && !NullImageDescriptor(snapshot.Images[candidate]) &&
                    !CompatibleCandidates(image, imageClass, separateSampledDimensions))
                {
                    Array.Clear(snapshot.Images[candidate]);
                }

                if (NullImageDescriptor(snapshot.Images[candidate]))
                {
                    image = image with
                    {
                        NumericClass = imageClass.NumericClass,
                        Dimension = imageClass.Dimension,
                        MipCount = imageClass.MipCount,
                        ConversionFormat = imageClass.ConversionFormat,
                        ShaderSwizzle = imageClass.ShaderSwizzle,
                        Cube = imageClass.Cube,
                    };
                    images[candidate] = image;
                }

                if (!CompatibleCandidates(image, imageClass, separateSampledDimensions))
                {
                    failure = ResourceMaterializationFailure.IncompatibleImageCandidates;
                    if (captureIndirectImageFailure is not null)
                    {
                        var table = snapshot.IndirectImages.Single(table => table.Resource == rootIndex);
                        captureIndirectImageFailure(new IndirectImageFailure(
                            info.Images[rootIndex].FirstUsePc, (uint)rootIndex, exemplar, (uint)candidate,
                            table.SelectorStride, table.SelectorOffset,
                            [.. table.MaterialDescriptor], [.. table.HeapDescriptor],
                            [.. table.Keys], [.. table.Candidates],
                            table.Descriptors.Select(words => words.Dwords.ToArray()).ToArray(),
                            snapshot.Images.Select(words => words.ToArray()).ToArray(),
                            [.. images], [.. snapshot.UserData])
                        {
                            SelectorDiagnostic = table.SelectorDiagnostic,
                            ScalarReads = plan.TableReads.Select(read =>
                            {
                                var access = plan.Memory[read.Value.MemoryIndex];
                                return new MaterializedScalarRead(access.Pc, access.ComponentIndex, read.FlatOffset,
                                    snapshot.FlattenedTable[(int)read.FlatOffset]);
                            }).ToArray(),
                        });
                    }
                    return Fail($"indirect image table at pc 0x{info.Images[rootIndex].FirstUsePc:x8} has incompatible candidates: exemplar={exemplar} candidate={candidate}");
                }
            }
        }

        if (!BuildSamplerPlan(info, images, out var samplerPlan))
        {
            return Fail("specialized sampler layout exceeds its resource limit");
        }

        Array.Resize(ref snapshot.Samplers, checked((int)samplerPlan.SamplerCount));
        for (var index = 0; index < info.Samplers.Count; index++)
        {
            var target = samplerPlan.PointSampler[index];
            if (target != DescriptorConstants.NoIndex && target >= info.Samplers.Count)
            {
                snapshot.Samplers[target] = snapshot.Samplers[index];
            }
        }

        // ApplyTo appends a depth-compare copy of every sampler shared by ordinary and
        // depth-reference sampling, after the point samplers; the snapshot needs the same words there.
        var compareUsage = new byte[snapshot.Samplers.Length];
        foreach (var pair in info.SampledPairs)
        {
            var image = info.Images[(int)pair.Image];
            var specialized = images[(int)pair.Image];
            var sampler = RequiresPointSampler(specialized.NumericClass, specialized.ConversionFormat)
                ? samplerPlan.PointSampler[pair.Sampler]
                : pair.Sampler;
            var depthCompare = image.DepthCompare && specialized.EmulatedCompareFunction < 0;
            compareUsage[sampler] |= depthCompare ? (byte)2 : (byte)1;
        }

        for (var index = 0; index < snapshot.Samplers.Length && index < compareUsage.Length; index++)
        {
            if (compareUsage[index] == 3)
            {
                Array.Resize(ref snapshot.Samplers, snapshot.Samplers.Length + 1);
                snapshot.Samplers[^1] = snapshot.Samplers[index];
            }
        }


        // Bounded runtime V# candidates are appended after the plan's buffers; each draw
        // owns their words and the run-time key mapping sits in the flattened table.
        var baseBufferCount = buffers.Count;
        var bufferCursor = snapshot.Buffers.Length;
        var candidateTables = new List<BufferCandidateTableSpecialization>(snapshot.BufferCandidateTables.Count);
        foreach (var table in snapshot.BufferCandidateTables)
        {
            if (table.Descriptors.Count == 0 || table.Keys.Count == 0)
            {
                return Fail("buffer candidate table has no candidates");
            }

            Array.Resize(ref snapshot.Buffers, bufferCursor + table.Descriptors.Count);
            for (var candidate = 0; candidate < table.Descriptors.Count; candidate++)
            {
                var words = table.Descriptors[candidate].Dwords;
                if ((words[3] >> 30) != 0)
                {
                    Array.Clear(words);
                }

                snapshot.Buffers[bufferCursor + candidate] = words;
                var stride = (words[1] >> 16) & 0x3FFF;
                var swizzleEnabled = (words[1] >> 31) != 0;
                var indexStride = (words[3] >> 21) & 0x3;
                var addThreadId = (words[3] >> 23) & 0x1;
                var packedStride = stride | ((swizzleEnabled ? 1u : 0u) << 14) | (indexStride << 16) | (addThreadId << 20);
                var swizzle = stride != 0 && ((packedStride >> 14) & 1) != 0;
                if (stride == 0)
                {
                    packedStride &= ~((1u << 14) | (3u << 16));
                }
                else if (!swizzle)
                {
                    packedStride &= ~(3u << 16);
                }

                buffers.Add(new BufferSpecialization(packedStride, (words[3] >> 12) & 0x7F, words[3] & 0xFFF));
            }

            var mappingOffset = (uint)mappingCursor;
            var order = Enumerable.Range(0, table.Keys.Count).OrderBy(index => table.Keys[index]).ToArray();
            snapshot.FlattenedTable[(int)mappingOffset] = (uint)table.Keys.Count;
            for (var entry = 0; entry < order.Length; entry++)
            {
                var source = order[entry];
                var offset = (int)mappingOffset + 1 + entry * 2;
                snapshot.FlattenedTable[offset] = table.Keys[source];
                snapshot.FlattenedTable[offset + 1] = table.Candidates[source];
            }

            mappingCursor += 1 + table.Keys.Count * 2;
            candidateTables.Add(new BufferCandidateTableSpecialization(
                (uint)bufferCursor, (uint)table.Descriptors.Count, mappingOffset,
                (uint)BitOperations.Log2((uint)table.Keys.Count) + 1));
            bufferCursor += table.Descriptors.Count;
        }

        specialization = new ResourceSpecialization
        {
            BaseBufferCount = baseBufferCount,
            Buffers = buffers,
            Images = images,
            BufferCandidateTables = candidateTables,
            ImageDescriptorGroups = DescriptorBindingAliases.Group(snapshot.Images, images),
            SamplerDescriptorGroups = DescriptorBindingAliases.Group(snapshot.Samplers.Take(info.Samplers.Count).ToArray()),
        };
        specializedSnapshot = snapshot;
        return true;
    }

    private sealed class SamplerPlan
    {
        public uint[] PointSampler = [];
        public uint SamplerCount;
    }

    // A sampler that some pair uses with a point-only image needs a point-filtering
    // copy; when every pair does, the sampler itself switches.
    private static bool BuildSamplerPlan(ShaderResourceInfo info, IReadOnlyList<ImageSpecialization> images, out SamplerPlan plan)
    {
        plan = new SamplerPlan { PointSampler = new uint[info.Samplers.Count] };

        Array.Fill(plan.PointSampler, DescriptorConstants.NoIndex);
        plan.SamplerCount = (uint)info.Samplers.Count;
        var usage = new byte[info.Samplers.Count];
        foreach (var pair in info.SampledPairs)
        {
            if (pair.Image >= images.Count || pair.Sampler >= info.Samplers.Count)
            {
                return false;
            }

            var image = images[(int)pair.Image];
            usage[pair.Sampler] |= RequiresPointSampler(image.NumericClass, image.ConversionFormat) ? (byte)2 : (byte)1;
        }

        for (var index = 0; index < info.Samplers.Count; index++)
        {
            if ((usage[index] & 2) == 0)
            {
                continue;
            }

            if ((usage[index] & 1) == 0)
            {
                plan.PointSampler[index] = (uint)index;
            }
            else
            {
                plan.PointSampler[index] = plan.SamplerCount++;
            }
        }

        return true;
    }

    // Applies a specialization to the plan's tables: buffer strides and formats, image
    // classes and indirect candidates, point samplers and the sampler each access uses.
    public static bool RecordMipStatistics { get; set; }

    public static SpecializedResourceInfo ApplyTo(ShaderResourcePlan plan, ResourceSpecialization specialization)
    {
        var source = plan.Info;
        if (source.Buffers.Count != specialization.BaseBufferCount || source.Images.Count > specialization.Images.Count)
        {
            throw new ResourcePlanException(
                $"shader resource specialization does not match the plan: hash=0x{plan.Hash:X16} stage={plan.Stage} " +
                $"buffers={source.Buffers.Count}/{specialization.BaseBufferCount} images={source.Images.Count}/{specialization.Images.Count}");
        }

        var info = source.Clone();
        for (var index = 0; index < info.Buffers.Count; index++)
        {
            info.Buffers[index].PackedStride = specialization.Buffers[index].PackedStride;
            info.Buffers[index].DescriptorFormat = specialization.Buffers[index].DescriptorFormat;
            info.Buffers[index].DescriptorSwizzle = specialization.Buffers[index].DescriptorSwizzle;
        }

        // Candidate buffers follow the plan's buffers, one native binding each.
        for (var index = source.Buffers.Count; index < specialization.Buffers.Count; index++)
        {
            var specialized = specialization.Buffers[index];
            info.Buffers.Add(new BufferResource
            {
                Source = DescriptorConstants.NoIndex,
                Read = true,
                PackedStride = specialized.PackedStride,
                DescriptorFormat = specialized.DescriptorFormat,
                DescriptorSwizzle = specialized.DescriptorSwizzle,
            });
        }

        for (var index = 0; index < specialization.BufferCandidateTables.Count; index++)
        {
            var table = specialization.BufferCandidateTables[index];
            info.BufferCandidateTables.Add(new BufferCandidateTableInfo
            {
                FirstCandidate = table.FirstCandidate,
                CandidateCount = table.CandidateCount,
                MappingOffset = table.MappingOffset,
                SearchIterations = table.SearchIterations,
            });
        }

        for (var index = 0; index < specialization.Images.Count; index++)
        {
            var specialized = specialization.Images[index];
            if (index >= info.Images.Count)
            {
                if (specialized.IndirectRoot >= source.Images.Count)
                {
                    throw new ResourcePlanException($"shader resource specialization names an invalid indirect root: hash=0x{plan.Hash:X16} image={index}");
                }

                info.Images.Add(source.Images[(int)specialized.IndirectRoot].Clone());
            }

            var image = info.Images[index];
            image.NumericClass = specialized.NumericClass;
            image.Dimension = specialized.Dimension;
            image.MipCount = specialized.MipCount;
            image.ConversionFormat = specialized.ConversionFormat;
            image.ShaderSwizzle = specialized.ShaderSwizzle;
            image.IndirectRoot = specialized.IndirectRoot;
            image.IndirectMappingOffset = specialized.IndirectMappingOffset;
            image.IndirectSearchIterations = specialized.IndirectSearchIterations;
            image.Cube = specialized.Cube;
            image.EmulatedCompareFunction = specialized.EmulatedCompareFunction;
            image.MipStatistics = specialized.MipStatistics;
            if (specialized.EmulatedCompareFunction >= 0)
            {
                // The shader compares instead; the host keeps a color view and a plain sampler.
                image.DepthCompare = false;
            }

            image.IndirectResources = [];
        }

        for (var index = 0; index < info.Images.Count; index++)
        {
            var root = info.Images[index].IndirectRoot;
            if (root != DescriptorConstants.NoIndex)
            {
                info.Images[(int)root].IndirectResources.Add((uint)index);
            }
        }

        if (!BuildSamplerPlan(source, specialization.Images, out var samplerPlan))
        {
            throw new ResourcePlanException($"shader resource specialization exceeds the sampler limit: hash=0x{plan.Hash:X16}");
        }

        for (var index = 0; index < source.Samplers.Count; index++)
        {
            var target = samplerPlan.PointSampler[index];
            if (target == DescriptorConstants.NoIndex)
            {
                continue;
            }

            if (target == index)
            {
                info.Samplers[index].ForcePointFiltering = true;
            }
            else
            {
                var sampler = source.Samplers[index].Clone();
                sampler.ForcePointFiltering = true;
                info.Samplers.Add(sampler);
            }
        }

        foreach (var pair in info.SampledPairs)
        {
            var image = info.Images[(int)pair.Image];
            if (RequiresPointSampler(image.NumericClass, image.ConversionFormat))
            {
                pair.Sampler = samplerPlan.PointSampler[pair.Sampler];
            }
        }

        // A guest sampler can be shared by ordinary sampling and depth-reference
        // sampling. Vulkan bakes compareEnable into VkSampler, so those uses cannot
        // share one host sampler. Split only the mixed cases; compare-only samplers
        // can use their existing slot.
        var compareUsage = new byte[info.Samplers.Count];
        foreach (var pair in info.SampledPairs)
        {
            var image = info.Images[(int)pair.Image];
            compareUsage[pair.Sampler] |= image.DepthCompare ? (byte)2 : (byte)1;
        }

        var compareSampler = new uint[info.Samplers.Count];
        Array.Fill(compareSampler, DescriptorConstants.NoIndex);
        for (var index = 0; index < compareUsage.Length; index++)
        {
            if ((compareUsage[index] & 2) == 0)
            {
                continue;
            }

            if ((compareUsage[index] & 1) == 0)
            {
                info.Samplers[index].DepthCompare = true;
                compareSampler[index] = (uint)index;
                continue;
            }

            var sampler = info.Samplers[index].Clone();
            sampler.DepthCompare = true;
            compareSampler[index] = (uint)info.Samplers.Count;
            info.Samplers.Add(sampler);
        }

        foreach (var pair in info.SampledPairs)
        {
            if (info.Images[(int)pair.Image].DepthCompare)
            {
                pair.Sampler = compareSampler[pair.Sampler];
            }
        }

        var samplerByMemory = new Dictionary<int, uint>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.Kind != MemoryResourceKind.Image || memory.Resource >= info.Images.Count)
            {
                continue;
            }

            var image = info.Images[(int)memory.Resource];
            if (!memory.NeedsSampler || memory.Sampler >= source.Samplers.Count)
            {
                continue;
            }

            var sampler = memory.Sampler;
            if (RequiresPointSampler(image.NumericClass, image.ConversionFormat))
            {
                sampler = samplerPlan.PointSampler[sampler];
            }

            if (image.DepthCompare)
            {
                sampler = compareSampler[sampler];
            }

            if (sampler != memory.Sampler)
            {
                samplerByMemory[index] = sampler;
            }
        }

        DescriptorBindingAliases.Apply(info, source, specialization);
        return new SpecializedResourceInfo { Info = info, SamplerByMemoryIndex = samplerByMemory };
    }
}

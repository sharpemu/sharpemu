// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SharpEmu.ShaderCompiler.Resources;

// Resolves a plan against one draw: descriptor sources, indirect image tables and the
// specialization. Both outputs change only when the whole materialisation succeeds.
public static class ResourceMaterializer
{
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;
    private const ulong MaxIndirectImageProbes = 65536;

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
        public List<RuntimeSamplerCandidate> RuntimeSamplers = [];
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
        if (!MaterializeSnapshot(plan, inputs, captureIndirectImageFailure is not null, out var materialized, out failure))
        {
            return false;
        }

        // The written ranges follow the table reads so every store can check its own.
        DeviceAddressRange[] ranges;
        using (ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.DeviceAddressRanges))
            ranges = DeviceAddressRangePlanner.Evaluate(plan, inputs);
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

        if (!BuildSpecialization(plan, materialized, out var nextSnapshot, out var nextSpecialization, out failure, captureIndirectImageFailure))
        {
            return false;
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
        return true;
    }

    // Evaluates only the flattened table, laid out as a full materialization lays it out
    // before specialization; the written device-address slots are left zero for the caller.
    public static bool TryEvaluateTable(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] table) =>
        RuntimeValueEvaluator.EvaluateSources(plan, [], inputs, plan.CleanFlatSlots, evaluateTable: true, out _, out table, out _,
            additionalTableWords: checked(plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount));

    // ---- snapshot ----

    private static bool MaterializeSnapshot(ShaderResourcePlan plan, ResourceRuntimeInputs inputs,
        bool captureSelectorDiagnostic, out MaterializedSnapshot snapshot, out ResourceMaterializationFailure failure)
    {
        using var snapshotProfile = ResourceMaterializationProfile.Measure(ResourceMaterializationProfile.Phase.Snapshot);
        failure = ResourceMaterializationFailure.Other;
        snapshot = new MaterializedSnapshot();
        // A clean host read does not cover writes made during this draw. These
        // proofs require stable descriptor inputs throughout shader execution.
        DescriptorWriteProof? writeProof = null;
        if (RequiresStableDescriptorInputs(plan))
        {
            if (inputs.OtherStageMayWriteMemory) return false;
            if (RequiresDescriptorWriteProof(plan))
            {
                if (inputs.ReadCleanMemory is null) return false;
                writeProof = new DescriptorWriteProof(inputs.ReadCleanMemory);
                inputs = new ResourceRuntimeInputs
                {
                    UserData = inputs.UserData, ShaderBase = inputs.ShaderBase,
                    ReadMemory = inputs.ReadMemory, ReadCleanMemory = writeProof.Read,
                    ReadResidentMemory = inputs.ReadResidentMemory, ReadsClean = inputs.ReadsClean,
                    ComputeState = inputs.ComputeState, TablePhase = inputs.TablePhase,
                    OtherStageMayWriteMemory = inputs.OtherStageMayWriteMemory,
                    ReadImageWriteRange = inputs.ReadImageWriteRange,
                    ReadPointSampledByteDomain = inputs.ReadPointSampledByteDomain,
                    ReadFlatParameterDomain = inputs.ReadFlatParameterDomain,
                };
            }
        }
        if (plan.RequiresSpecializationMemory && inputs.ReadCleanMemory is null)
        {
            return false;
        }

        if (!RuntimeValueEvaluator.EvaluateSources(plan, plan.MaterializationSources, inputs, plan.CleanFlatSlots,
            evaluateTable: true, out var values, out var table, out var activeSources,
            additionalTableWords: checked(plan.WrittenRangeCount * ShaderResourcePlan.WrittenRangeDwordCount)))
        {
            SpecializationFailed(DiagnoseSnapshotEvaluationFailure(plan, inputs, activeSources));
            return false;
        }

        foreach (var source in plan.DescriptorSources)
        {
            if (source.ZeroExtentBufferSource is not { } bufferSource) continue;
            if (!RuntimeValueEvaluator.EvaluateSources(plan, [bufferSource], inputs.WithReader(inputs.ReadCleanMemory), [],
                    evaluateTable: false, out var descriptors, out _) ||
                descriptors.Count != 1 || descriptors[0].DwordCount != 4 || ScalarBufferSize(descriptors[0].Dwords) != 0)
            {
                SpecializationFailed("a dynamically loaded descriptor requires an empty source buffer");
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
                if (indirect.Workgroup is { } workgroup)
                {
                    if (!workgroup.TryEvaluate(plan, inputs, out var keys, out var descriptors) ||
                        descriptors.Any(words => !UsableImageCandidate(words, image.R128)) ||
                        !FinishIndirectImage(descriptors, keys, out var workgroupTable, out failure)) return false;
                    snapshot.Images[imageIndex] = workgroupTable.Descriptors[(int)workgroupTable.Candidates[0]].Dwords;
                    if (workgroupTable.Descriptors.Count > 1)
                    {
                        workgroupTable.Resource = (uint)imageIndex;
                        snapshot.IndirectImages.Add(workgroupTable);
                    }
                    continue;
                }
                if (indirect.GatheredByteSelectorProof is { } byteProof && !byteProof.HasByteRange(plan, inputs))
                    return false;
                if (indirect.DirectCandidates is { } directCandidates)
                {
                    if (!TryCandidateCount(plan, indirect.CandidateCountSource, directCandidates.Count, cleanInputs, out var candidateCount))
                        return false;
                    directCandidates = directCandidates.Take(candidateCount).ToArray();
                    if (candidateCount == 0)
                    {
                        if (!ProvenUnusedSource(plan, image.Source)) return false;
                        // The guarded instruction cannot execute. Use the same
                        // backend null binding as other proven inactive resources.
                        snapshot.Images[imageIndex] = new uint[8];
                        continue;
                    }
                    if (indirect.PackedTextureDomain is { } packed && inputs.ReadPointSampledByteDomain is not null)
                    {
                        if (packed.TryFilterCandidates(plan, inputs, directCandidates, out var selected))
                        {
                            if (selected.Count == 0) return false;
                            directCandidates = selected;
                        }
                    }
                    if (!RuntimeValueEvaluator.EvaluateSources(plan, directCandidates.Select(candidate => candidate.Source).ToArray(),
                        cleanInputs, [], evaluateTable: false, out var descriptors, out _))
                    {
                        return false;
                    }
                    var directTable = new IndirectImageTable { Resource = (uint)imageIndex };
                    for (var candidateIndex = 0; candidateIndex < descriptors.Count; candidateIndex++)
                    {
                        var descriptor = descriptors[candidateIndex];
                        if (!UsableImageCandidate(descriptor.Dwords, image.R128))
                        {
                            if (indirect.CandidateCountSource is not null || indirect.GatheredByteSelectorProof is not null || indirect.PackedTextureDomain is not null)
                                return false;
                            descriptor = DescriptorWords.Empty(8);
                        }
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
                    if (!MaterializeWaveIndexedImage(indirect, waveIndexed, waveSources[0], image, image.R128, inputs, out var waveTable, out failure))
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
                    if (!MaterializeDenseIndirectImage(indirect, denseSources[0], image, image.R128, inputs, out var denseTable, out failure))
                        return false;
                    snapshot.Images[imageIndex] = denseTable.Descriptors[(int)denseTable.Candidates[0]].Dwords;
                    if (denseTable.Descriptors.Count > 1)
                    {
                        denseTable.Resource = (uint)imageIndex;
                        snapshot.IndirectImages.Add(denseTable);
                    }
                    continue;
                }
                if (!RuntimeValueEvaluator.EvaluateSources(plan, [indirect.MaterialSource, indirect.HeapSource], cleanInputs, [], evaluateTable: false, out var tables, out _))
                {
                    return false;
                }

                if (!MaterializeIndirectImage(plan, indirect, tables[0], tables[1], image, inputs,
                    captureSelectorDiagnostic, out var indirectTable, out failure))
                {
                    return false;
                }

                snapshot.Images[imageIndex] = indirectTable.Descriptors[(int)indirectTable.Candidates[0]].Dwords;
                if (indirectTable.Descriptors.Count > 1)
                {
                    indirectTable.Resource = (uint)imageIndex;
                    snapshot.IndirectImages.Add(indirectTable);
                }
            }
            else
            {
                var descriptor = values[cursor++];
                if (!ValidImageDescriptor(descriptor.Dwords, image.R128))
                {
                    descriptor = DescriptorWords.Empty(descriptor.DwordCount);
                }

                snapshot.Images[imageIndex] = descriptor.Dwords;
            }
        }

        snapshot.Samplers = new uint[plan.Info.Samplers.Count][];
        var extraSamplers = new List<uint[]>();
        var additionalSampledPairs = 0;
        for (var index = 0; index < snapshot.Samplers.Length; index++)
        {
            var sampler = plan.Info.Samplers[index];
            snapshot.Samplers[index] = values[cursor++].Dwords;
            if (plan.DescriptorSources[(int)sampler.Source].Workgroup is { LoopCounter: not null } workgroup &&
                (activeSources.Length == 0 || activeSources[sampler.Source]))
            {
                // These instructions use the native sampler's complete state.
                // Gather and comparison paths need additional specialization proofs.
                if (plan.Memory.Entries.Any(memory => memory.NeedsSampler && memory.Sampler == index &&
                    memory.Opcode is not ("ImageSampleD" or "ImageSampleLz")) ||
                    !workgroup.TryEvaluate(plan, inputs, out var keys, out var records) || records.Length == 0)
                    return false;
                var distinct = new List<(uint[] Words, uint Sampler)>();
                var mappingStart = snapshot.RuntimeSamplers.Count;
                for (var record = 0; record < records.Length; record++)
                {
                    var found = distinct.FindIndex(candidate => candidate.Words.AsSpan().SequenceEqual(records[record]));
                    uint target;
                    if (found >= 0) target = distinct[found].Sampler;
                    else
                    {
                        target = distinct.Count == 0 ? (uint)index : (uint)(snapshot.Samplers.Length + extraSamplers.Count);
                        if (target >= ShaderResourceInfo.MaxSamplers) return false;
                        if (distinct.Count != 0)
                        {
                            additionalSampledPairs += plan.Info.SampledPairs.Count(pair => pair.Sampler == index);
                            if (plan.Info.SampledPairs.Count + additionalSampledPairs > ShaderResourceInfo.MaxSampledPairs) return false;
                            extraSamplers.Add(records[record]);
                        }
                        distinct.Add((records[record], target));
                    }
                    var selectorRead = plan.Graph.ResolveInvariantPhi(workgroup.Handle.Operands[0]) ?? workgroup.Handle.Operands[0];
                    snapshot.RuntimeSamplers.Add(new((uint)index, selectorRead.MemoryIndex, keys[record], target));
                }
                snapshot.Samplers[index] = records[0];
                if (distinct.Count == 1)
                    snapshot.RuntimeSamplers.RemoveRange(mappingStart, snapshot.RuntimeSamplers.Count - mappingStart);
                continue;
            }
            if (plan.DescriptorSources[(int)sampler.Source].EquivalentSamplerSources is not { } candidates ||
                activeSources.Length != 0 && !activeSources[sampler.Source]) continue;
            var samplerSource = plan.DescriptorSources[(int)sampler.Source];
            if (samplerSource.RuntimeSamplerCountSource is { } countSource)
            {
                if (inputs.ReadCleanMemory is null ||
                    !TryCandidateCount(plan, countSource, candidates.Count, inputs.WithReader(inputs.ReadCleanMemory), out var count))
                    return false;
                if (count == 0)
                {
                    if (!ProvenUnusedSource(plan, sampler.Source)) return false;
                    continue;
                }
                candidates = candidates.Take(count).ToArray();
            }
            if (inputs.ReadCleanMemory is null ||
                !RuntimeValueEvaluator.EvaluateSources(plan, candidates.ToArray(), inputs.WithReader(inputs.ReadCleanMemory), [],
                    evaluateTable: false, out var descriptors, out _) || descriptors.Count == 0 ||
                descriptors.Any(descriptor => descriptor.DwordCount != 4 || !descriptor.SameAs(descriptors[0])))
            {
                SpecializationFailed($"finite sampler resource {index} requires identical readable candidates");
                return false;
            }
            snapshot.Samplers[index] = descriptors[0].Dwords;
        }
        snapshot.Samplers = [.. snapshot.Samplers, .. extraSamplers];

        foreach (var (memoryIndex, _) in plan.Info.DeviceStoreValidationSources)
        {
            var fields = values[cursor++].Dwords;
            var memory = plan.Memory[memoryIndex];
            var format = (fields[3] >> 12) & 0x7F;
            var valid = Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out _) &&
                memory.DataBits == 32 && !memory.Typed && (fields[1] & 0x80000000) == 0 &&
                (fields[3] & 0xF0800000) == 0;
            if (format != 0)
            {
                var count = Gfx10UnifiedFormat.ComponentCount(dataFormat);
                valid &= count != 0 && count == memory.DataDwords &&
                    ((fields[1] >> 16) & 0x3FFF) >= Gfx10UnifiedFormat.GetAccessByteSize(dataFormat, count);
                for (uint component = 0; component < count; component++)
                    valid &= ((fields[3] >> (int)(component * 3)) & 7) == component + 4;
            }
            if (!valid)
            {
                SpecializationFailed($"device formatted store at 0x{memory.Pc:X} requires a proven linear, structured descriptor with matching identity channels");
                return false;
            }
        }

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
        if (writeProof is not null && !writeProof.Validate(plan, inputs)) return false;
        return true;
    }

    private static bool ProvenUnusedSource(ShaderResourcePlan plan, uint source)
    {
        if (plan.DescriptorSources[(int)source].RuntimeZeroCountGuardPc is not { } pc) return false;
        var flow = plan.Graph.ControlFlow;
        var instruction = plan.Graph.Program.Instructions.First(instruction => instruction.Pc == pc);
        var guard = flow.BlockOf(pc);
        if (guard < 0 || !flow.BlockByStartPc.TryGetValue(pc + (uint)instruction.Words.Count * sizeof(uint), out var admitted))
            return false;
        var consumers = new HashSet<int>();
        foreach (var memory in plan.Memory.Entries)
        {
            if (memory.Kind != MemoryResourceKind.Image || memory.PlanningOnly) continue;
            var imageUses = memory.Resource < plan.Info.Images.Count && plan.Info.Images[(int)memory.Resource].Source == source;
            var samplerUses = memory.NeedsSampler && memory.Sampler < plan.Info.Samplers.Count &&
                plan.Info.Samplers[(int)memory.Sampler].Source == source;
            if (imageUses || samplerUses) consumers.Add(flow.BlockOf(memory.Pc));
        }
        if (consumers.Count == 0 || consumers.Contains(-1)) return false;
        var pending = new Stack<int>();
        var visited = new HashSet<int>();
        pending.Push(0);
        while (pending.TryPop(out var block))
        {
            if (!visited.Add(block)) continue;
            if (consumers.Contains(block)) return false;
            foreach (var successor in flow.Successors[block])
                if (block != guard || successor != admitted) pending.Push(successor);
        }
        return true;
    }

    private static bool RequiresStableDescriptorInputs(ShaderResourcePlan plan) =>
        plan.DescriptorSources.Any(source => source.RuntimeSamplerCountSource is not null ||
            source.IndirectImage is { } indirect &&
            (indirect.CandidateCountSource is not null || indirect.GatheredByteSelectorProof is not null || indirect.PackedTextureDomain is not null));

    internal static bool RequiresDescriptorWriteProof(ShaderResourcePlan plan) =>
        RequiresStableDescriptorInputs(plan) && plan.Memory.Entries.Any(memory =>
            memory.Kind is not (MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch or MemoryResourceKind.GlobalDataShare) &&
            memory.Access is MemoryAccess.Write or MemoryAccess.Atomic);

    private sealed class DescriptorWriteProof(GuestWordReader reader)
    {
        private readonly HashSet<ulong> _reads = [];

        public bool Read(ulong address, out uint word)
        {
            word = 0;
            if (address > (1ul << 48) - sizeof(uint) || !reader(address, out word)) return false;
            _reads.Add(address);
            return true;
        }

        public bool Validate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
        {
            var evaluator = new RuntimeValueEvaluator(plan, inputs.WithReader(inputs.ReadCleanMemory));
            var writes = new List<(ulong Address, ulong Size)>();
            for (var index = 0; index < plan.Memory.Count; index++)
            {
                var memory = plan.Memory[index];
                if (memory.Access is not (MemoryAccess.Write or MemoryAccess.Atomic) ||
                    memory.Kind is MemoryResourceKind.LocalDataShare or MemoryResourceKind.Scratch or MemoryResourceKind.GlobalDataShare) continue;
                if (memory.Kind is MemoryResourceKind.Buffer or MemoryResourceKind.ScalarBuffer)
                {
                    if (plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } buffer ||
                        buffer.Operands.Any(operand => !plan.ValidateRuntimeValue(operand)) ||
                        !IndirectSelectorValues.PackedPointerDescriptor.EvaluateHandle(buffer, evaluator, out var descriptor) ||
                        !IndirectSelectorValues.PackedPointerDescriptor.Range(descriptor, out var bufferAddress, out var length))
                        return false;
                    // Include the immediate offset and a full element past the final
                    // record, even when its stride is smaller than the store width.
                    var extra = (ulong)memory.Offset + Math.Max(16ul, (ulong)memory.DataBits * memory.DataDwords / 8);
                    if (length + extra > (1ul << 48) - bufferAddress) return false;
                    writes.Add((bufferAddress, length + extra));
                    continue;
                }
                if (memory.Kind != MemoryResourceKind.Image || inputs.ReadImageWriteRange is null ||
                    plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.ImageHandle, Operands.Length: 8 } handle)
                    return false;
                var words = new uint[8];
                for (var component = 0; component < words.Length; component++)
                    if (!plan.ValidateRuntimeValue(handle.Operands[component]) ||
                        !evaluator.Evaluate(handle.Operands[component], out words[component])) return false;
                if (!inputs.ReadImageWriteRange(words, out var address, out var size) || size == 0 ||
                    address >= 1ul << 48 || size > (1ul << 48) - address) return false;
                writes.Add((address, size));
            }
            // Include dependencies read while evaluating output descriptors before
            // comparing any ranges; outputs can alias each other's dependencies.
            return !writes.Any(write => _reads.Any(read =>
                read < write.Address + write.Size && write.Address < read + sizeof(uint)));
        }
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

    private static bool NullImageDescriptor(ReadOnlySpan<uint> descriptor) =>
        descriptor[0] == 0 && (descriptor[1] & 0xFF) == 0;

    private static bool ReservedImageBitsClear(ReadOnlySpan<uint> descriptor)
    {
        ReadOnlySpan<uint> reserved =
        [
            // GFX10/10.3 word 2 bit 31 is RESOURCE_LEVEL, not reserved.
            0x00000000u, 0x20000000u, 0x70003000u, 0x00000000u,
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

    private static bool UsableImageCandidate(ReadOnlySpan<uint> candidate, bool r128) =>
        !NullImageDescriptor(candidate) && ValidImageDescriptor(candidate, r128) && ReservedImageBitsClear(candidate) &&
        GuestImageFormat.SampledNumericClass(GuestImageFormat.FormatOf(candidate)) != ImageNumericClass.Unsupported;

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

        return inputs.ReadCleanMemory is not null && inputs.ReadCleanMemory(baseAddress + aligned, out word);
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
        if (provenOffsets is null && probeCount > MaxIndirectImageProbes)
        {
            return false;
        }

        var keys = new List<uint> { 0 };
        var seen = new HashSet<uint> { 0 };
        var offsets = provenOffsets is not null ? provenOffsets.Select(offset => (ulong)offset) :
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

            if (!UsableImageCandidate(candidate, image.R128))
            {
                Array.Clear(candidate);
            }
            probed.Add(candidate);
        }

        if (!FinishIndirectImage(
                probed,
                keys,
                out var table,
                out failure))
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
        out ResourceMaterializationFailure failure)
    {
        failure = ResourceMaterializationFailure.Other;
        result = new IndirectImageTable();
        result.Keys.AddRange(keys);
        foreach (var candidate in probed)
        {
            var words = new DescriptorWords(candidate);
            var found = result.Descriptors.FindIndex(existing => existing.SameAs(words));
            if (found < 0)
            {
                if (result.Descriptors.Count >= ShaderResourceInfo.MaxImages)
                {
                    failure = ResourceMaterializationFailure.ImageCapacityExceeded;
                    return false;
                }
                found = result.Descriptors.Count;
                result.Descriptors.Add(words);
            }
            result.Candidates.Add((uint)found);
        }
        return true;
    }

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
        if (image.MipMode == ImageMipMode.None || NullImageDescriptor(descriptor))
        {
            return 1;
        }

        var baseLevel = (descriptor[3] >> 12) & 0xF;
        var last = (descriptor[3] >> 16) & 0xF;
        if (image.MipMode == ImageMipMode.ExplicitLodGather && !image.R128)
            last = Math.Min(last, (descriptor[5] >> 4) & 0xF);
        return baseLevel <= last ? last - baseLevel + 1 : 0;
    }

    private static bool Fail(string message)
    {
        SpecializationFailed(message);
        return false;
    }

    private static bool TryCandidateCount(ShaderResourcePlan plan, uint? countSource, int maximum,
        ResourceRuntimeInputs inputs, out int count)
    {
        count = maximum;
        if (countSource is not { } source) return true;
        if (!RuntimeValueEvaluator.EvaluateSources(plan, [source], inputs, [], evaluateTable: false,
                out var descriptors, out _) || descriptors.Count != 1 || descriptors[0].DwordCount != 1 ||
            descriptors[0].Dwords[0] > maximum) return false;
        count = (int)descriptors[0].Dwords[0];
        return true;
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
        var info = WithRuntimeSamplers(plan.Info, snapshot.RuntimeSamplers);
        // Core Vulkan gather always addresses the view's base level. Explicit-LOD
        // gathers instead bind one view per accessible mip and select it in the shader.
        // Linear mip filtering and unnormalized gathers need separate semantics.
        foreach (var access in plan.Memory.Entries.Where(access => access.Opcode == "ImageGather4CL"))
        {
            if (access.Sampler >= snapshot.Samplers.Length || access.Resource >= info.Images.Count)
                return Fail("explicit LOD gather has no image or sampler");
            var sampler = snapshot.Samplers[access.Sampler];
            var descriptor = snapshot.Images[access.Resource];
            if (sampler.Length < 4 || ((sampler[2] >> 26) & 3) > 1 ||
                (sampler[0] & (1u << 15)) != 0 || descriptor.Length < 8 ||
                ((descriptor[1] >> 8) & 0xFFF) != 0 ||
                info.Images[(int)access.Resource].IndirectSearchIterations != 0)
                return Fail("explicit LOD gather requires normalized point mip selection without a resource min LOD or indirect candidates");
        }

        var denseImages = snapshot.Images.ToList();
        var owners = Enumerable.Range(0, info.Images.Count).Select(index => (uint)index).ToList();
        var sharedCandidates = new List<(uint Root, uint Candidate)>();
        var mappingWordCount = 0;
        foreach (var table in snapshot.IndirectImages)
        {
            if (table.Resource >= info.Images.Count || table.Descriptors.Count < 2)
            {
                return Fail("indirect image table has an invalid root or candidate count");
            }

            sharedCandidates.Add((table.Resource, table.Resource));
            for (var candidate = 1; candidate < table.Descriptors.Count; candidate++)
            {
                var words = table.Descriptors[candidate].Dwords;
                var existing = -1;
                if (!NullImageDescriptor(words))
                    for (var resource = 0; resource < denseImages.Count; resource++)
                        if (words.AsSpan().SequenceEqual(denseImages[resource]) &&
                            CanShareSampledImageUse(info.Images[(int)table.Resource], info.Images[(int)owners[resource]]))
                        { existing = resource; break; }
                if (existing < 0)
                {
                    existing = denseImages.Count;
                    denseImages.Add(words);
                    owners.Add(table.Resource);
                }
                sharedCandidates.Add((table.Resource, (uint)existing));
            }
            mappingWordCount = checked(mappingWordCount + 1 + table.Keys.Count * 2);
        }

        foreach (var table in snapshot.BufferCandidateTables)
        {
            mappingWordCount = checked(mappingWordCount + 1 + table.Keys.Count * 2);
        }

        var imageCount = denseImages.Count;
        // Each bounded indirect root can contribute its own candidate table.
        // MaxImages bounds the plan roots and each table, not their combined expansion.
        if (imageCount > checked(info.Images.Count * ShaderResourceInfo.MaxImages))
        {
            failure = ResourceMaterializationFailure.ImageCapacityExceeded;
            return Fail("indirect image candidates exceed the dense image resource limit");
        }
        snapshot.Images = denseImages.ToArray();
        var mappingCursor = snapshot.FlattenedTable.Length;
        Array.Resize(ref snapshot.FlattenedTable, checked(mappingCursor + mappingWordCount));
        var images = new List<ImageSpecialization>(imageCount);
        foreach (var image in info.Images)
        {
            images.Add(new ImageSpecialization(
                image.NumericClass, image.Dimension, image.MipCount, image.ConversionFormat, image.ShaderSwizzle,
                image.IndirectRoot, image.IndirectMappingOffset, image.IndirectSearchIterations, image.Cube));
        }

        for (var resource = info.Images.Count; resource < imageCount; resource++)
            images.Add(images[(int)owners[resource]] with { IndirectRoot = owners[resource] });

        foreach (var table in snapshot.IndirectImages)
        {
            var rootImage = images[(int)table.Resource];
            var mappingOffset = (uint)mappingCursor;
            images[(int)table.Resource] = rootImage with
            {
                IndirectRoot = table.Resource,
                IndirectMappingOffset = mappingOffset,
                IndirectSearchIterations = (uint)BitOperations.Log2((uint)table.Keys.Count) + 1,
            };
            mappingCursor += 1 + table.Keys.Count * 2;
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
            if (NullImageDescriptor(descriptor))
            {
                images[index] = image with
                {
                    // A null-bound atomic keeps its declared numeric class
                    // (Uint or, since float image atomics were added, Float)
                    // instead of assuming every atomic image is Uint.
                    NumericClass = baseImage.Atomic ? baseImage.NumericClass : ImageNumericClass.Float,
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
            // neither.
            if (baseImage.Atomic && format != GuestImageFormat.Format32Uint &&
                GuestImageFormat.SampledNumericClass(format) != ImageNumericClass.Float)
            {
                return Fail($"atomic image descriptor {index} uses unsupported format {format}");
            }

            var storage = baseImage.ResourceClass == ImageResourceClass.Storage;
            var conversionFormat = ImageConversionFormat(format);
            var shaderSwizzle = storage || conversionFormat != GuestImageFormat.Invalid ? descriptor[3] & 0xFFF : image.ShaderSwizzle;
            var rawSintStorage = storage && format == GuestImageFormat.Format32Sint && baseImage.Written && !baseImage.Read && !baseImage.Atomic;
            var numericClass = GuestImageFormat.SampledNumericClass(format);
            if (storage)
            {
                if (numericClass == ImageNumericClass.Unsupported)
                {
                    return Fail($"storage image descriptor {index} uses unsupported format {format}");
                }

                // Atomics use the uint view; float min/max compare-exchange its bits.
                if (rawSintStorage || (baseImage.Atomic && numericClass == ImageNumericClass.Float))
                {
                    numericClass = ImageNumericClass.Uint;
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

            var candidateSet = ImageCandidates(images, (uint)rootIndex, sharedCandidates).ToHashSet();
            var exemplar = DescriptorConstants.NoIndex;
            var resourceCount = 0;
            for (var resource = 0; resource < images.Count; resource++)
            {
                if (!candidateSet.Contains((uint)resource))
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
            for (var candidate = 0; candidate < images.Count; candidate++)
            {
                var image = images[candidate];
                if (!candidateSet.Contains((uint)candidate))
                {
                    continue;
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

                // Each sampled candidate has its own binding and coordinate width.
                var compatibleDimensions = image.Dimension == imageClass.Dimension ||
                    separateSampledDimensions && !image.Cube && !imageClass.Cube &&
                    image.Dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray &&
                    imageClass.Dimension is ImageDimension.Dim2D or ImageDimension.Dim2DArray;
                var separateSampledTypes = separateSampledDimensions && !info.Images[rootIndex].DepthCompare;
                if ((image.NumericClass != imageClass.NumericClass && !separateSampledTypes) || !compatibleDimensions ||
                    image.MipCount != imageClass.MipCount || image.ConversionFormat != imageClass.ConversionFormat ||
                    image.ShaderSwizzle != imageClass.ShaderSwizzle || image.Cube != imageClass.Cube)
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

        if (!BuildSamplerPlan(info, images, out var samplerPlan, sharedCandidates))
        {
            return Fail("specialized sampler layout exceeds its resource limit");
        }

        Array.Resize(ref snapshot.Samplers, checked((int)samplerPlan.SamplerCount));
        // Typed candidates use separate sampling instructions. Every statically used
        // UINT image/sampler pair must remain legal, including candidate combinations
        // whose runtime keys differ. Do not invent integer linear-filter semantics.
        foreach (var pair in info.SampledPairs)
        {
            if (images[(int)pair.Image].IndirectRoot != pair.Image) continue;
            if (!ImageCandidates(images, pair.Image, sharedCandidates).Any(candidate => images[(int)candidate].NumericClass == ImageNumericClass.Uint &&
                    images[(int)candidate].ConversionFormat == GuestImageFormat.Invalid)) continue;
            var words = snapshot.Samplers[pair.Sampler];
            if (words.Length < 4 || ((words[2] >> 20) & 0xF) != 0 ||
                ((words[2] >> 24) & 3) > 1 || ((words[2] >> 26) & 3) > 1)
                return Fail("indirect UINT sampled candidates require point filtering");
        }
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
        var compareUsage = new byte[ShaderResourceInfo.MaxSamplers];
        foreach (var pair in info.SampledPairs)
        {
            foreach (var candidate in ImageCandidates(images, pair.Image, sharedCandidates))
            {
                var image = info.Images[(int)pair.Image];
                var specialized = images[(int)candidate];
                var sampler = RequiresPointSampler(specialized.NumericClass, specialized.ConversionFormat)
                    ? samplerPlan.PointSampler[pair.Sampler]
                    : pair.Sampler;
                var depthCompare = image.DepthCompare && specialized.EmulatedCompareFunction < 0;
                compareUsage[sampler] |= depthCompare ? (byte)2 : (byte)1;
            }
        }

        for (var index = 0; index < snapshot.Samplers.Length && index < compareUsage.Length; index++)
        {
            if (compareUsage[index] == 3)
            {
                if (snapshot.Samplers.Length >= ShaderResourceInfo.MaxSamplers)
                {
                    return Fail("specialized sampler layout exceeds its resource limit");
                }

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
            IndirectImageCandidates = sharedCandidates,
            BufferCandidateTables = candidateTables,
            RuntimeSamplers = snapshot.RuntimeSamplers,
        };
        specializedSnapshot = snapshot;
        return true;
    }

    internal static bool CanShareSampledImageUse(ImageResource left, ImageResource right) =>
        left.ResourceClass == ImageResourceClass.Sampled && right.ResourceClass == ImageResourceClass.Sampled &&
        !left.Written && !right.Written && !left.Atomic && !right.Atomic && !left.DepthCompare && !right.DepthCompare &&
        left.Read == right.Read && left.NumericClass == right.NumericClass && left.Dimension == right.Dimension && left.MipMode == right.MipMode &&
        left.MipCount == right.MipCount && left.ConversionFormat == right.ConversionFormat &&
        left.ShaderSwizzle == right.ShaderSwizzle && left.Cube == right.Cube && left.R128 == right.R128 &&
        left.EmulatedCompareFunction == right.EmulatedCompareFunction;

    private static ShaderResourceInfo WithRuntimeSamplers(ShaderResourceInfo original,
        IReadOnlyList<RuntimeSamplerCandidate> candidates)
    {
        if (candidates.Count == 0) return original;
        var info = original.Clone();
        foreach (var group in candidates.GroupBy(candidate => candidate.Root))
        {
            var root = info.Samplers[(int)group.Key];
            root.SelectorMemoryIndex = group.First().SelectorMemoryIndex;
            root.Candidates = group.Select(candidate => new FiniteSamplerCandidate(candidate.Offset, candidate.Sampler)).ToArray();
            foreach (var target in group.Select(candidate => candidate.Sampler).Distinct().Where(target => target != group.Key))
            {
                if (target != info.Samplers.Count)
                    throw new ResourcePlanException("runtime sampler candidates are not contiguous");
                var sampler = original.Samplers[(int)group.Key].Clone();
                sampler.Candidates = null;
                sampler.SelectorMemoryIndex = -1;
                info.Samplers.Add(sampler);
                foreach (var pair in original.SampledPairs.Where(pair => pair.Sampler == group.Key))
                    info.SampledPairs.Add(new SampledImagePair { Image = pair.Image, Sampler = target, FirstUsePc = pair.FirstUsePc });
            }
        }
        return info;
    }

    private sealed class SamplerPlan
    {
        public uint[] PointSampler = new uint[ShaderResourceInfo.MaxSamplers];
        public uint SamplerCount;
    }

    private static IEnumerable<uint> ImageCandidates(IReadOnlyList<ImageSpecialization> images, uint root,
        IReadOnlyList<(uint Root, uint Candidate)>? sharedCandidates = null)
    {
        if (sharedCandidates is { Count: > 0 })
        {
            var found = false;
            foreach (var entry in sharedCandidates)
                if (entry.Root == root) { found = true; yield return entry.Candidate; }
            if (found) yield break;
        }
        yield return root;
        for (var index = 0; index < images.Count; index++)
            if (index != root && images[index].IndirectRoot == root) yield return (uint)index;
    }

    // A sampler that some pair uses with a point-only image needs a point-filtering
    // copy; when every pair does, the sampler itself switches.
    private static bool BuildSamplerPlan(ShaderResourceInfo info, IReadOnlyList<ImageSpecialization> images, out SamplerPlan plan,
        IReadOnlyList<(uint Root, uint Candidate)>? sharedCandidates = null)
    {
        plan = new SamplerPlan();
        if (info.Samplers.Count > plan.PointSampler.Length)
        {
            return false;
        }

        Array.Fill(plan.PointSampler, DescriptorConstants.NoIndex);
        plan.SamplerCount = (uint)info.Samplers.Count;
        var usage = new byte[ShaderResourceInfo.MaxSamplers];
        foreach (var pair in info.SampledPairs)
        {
            if (pair.Image >= images.Count || pair.Sampler >= info.Samplers.Count)
            {
                return false;
            }

            foreach (var candidate in ImageCandidates(images, pair.Image, sharedCandidates))
            {
                var image = images[(int)candidate];
                usage[pair.Sampler] |= RequiresPointSampler(image.NumericClass, image.ConversionFormat) ? (byte)2 : (byte)1;
            }
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
                if (plan.SamplerCount >= ShaderResourceInfo.MaxSamplers)
                {
                    return false;
                }

                plan.PointSampler[index] = plan.SamplerCount++;
            }
        }

        return true;
    }

    // Applies a specialization to the plan's tables: buffer strides and formats, image
    // classes and indirect candidates, point samplers and the sampler each access uses.
    public static SpecializedResourceInfo ApplyTo(ShaderResourcePlan plan, ResourceSpecialization specialization)
    {
        var source = WithRuntimeSamplers(plan.Info, specialization.RuntimeSamplers);
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
            if (specialized.EmulatedCompareFunction >= 0)
            {
                // The shader compares instead; the host keeps a color view and a plain sampler.
                image.DepthCompare = false;
            }

            image.IndirectResources = [];
        }

        if (specialization.IndirectImageCandidates.Count != 0)
        {
            foreach (var entry in specialization.IndirectImageCandidates)
            {
                if (entry.Root >= info.Images.Count || entry.Candidate >= info.Images.Count)
                    throw new ResourcePlanException("shared image candidate is outside the specialized resource table");
                info.Images[(int)entry.Root].IndirectResources.Add(entry.Candidate);
            }
        }
        else
        {
            for (var index = 0; index < info.Images.Count; index++)
            {
                var root = info.Images[index].IndirectRoot;
                if (root != DescriptorConstants.NoIndex)
                    info.Images[(int)root].IndirectResources.Add((uint)index);
            }
        }

        if (!BuildSamplerPlan(source, specialization.Images, out var samplerPlan, specialization.IndirectImageCandidates))
        {
            throw new ResourcePlanException($"shader resource specialization exceeds the sampler limit: hash=0x{plan.Hash:X16}");
        }

        foreach (var pair in source.SampledPairs)
            foreach (var candidate in ImageCandidates(specialization.Images, pair.Image, specialization.IndirectImageCandidates).Where(candidate => candidate != pair.Image))
                info.SampledPairs.Add(new SampledImagePair { Image = candidate, Sampler = pair.Sampler, FirstUsePc = pair.FirstUsePc });

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
        var compareUsage = new byte[ShaderResourceInfo.MaxSamplers];
        foreach (var pair in info.SampledPairs)
        {
            var image = info.Images[(int)pair.Image];
            compareUsage[pair.Sampler] |= image.DepthCompare ? (byte)2 : (byte)1;
        }

        var compareSampler = new uint[ShaderResourceInfo.MaxSamplers];
        Array.Fill(compareSampler, DescriptorConstants.NoIndex);
        for (var index = 0; index < info.Samplers.Count; index++)
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

            if (info.Samplers.Count >= ShaderResourceInfo.MaxSamplers)
            {
                throw new ResourcePlanException($"shader resource specialization exceeds the sampler limit: hash=0x{plan.Hash:X16}");
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
        var finiteSamplersByMemory = new Dictionary<int, SamplerResource>();
        var samplerByImageMemory = new Dictionary<(int Memory, uint Image, uint Sampler), uint>();
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
            uint ResolveSampler(uint candidate, ImageResource candidateImage)
            {
                if (RequiresPointSampler(candidateImage.NumericClass, candidateImage.ConversionFormat)) candidate = samplerPlan.PointSampler[candidate];
                if (candidateImage.DepthCompare) candidate = compareSampler[candidate];
                return candidate;
            }
            if (source.Samplers[(int)sampler].Candidates is not null)
            {
                var finite = source.Samplers[(int)sampler].Clone();
                finiteSamplersByMemory[index] = finite;
            }
            var samplerCandidates = source.Samplers[(int)sampler].Candidates?.Select(candidate => candidate.Sampler) ?? [sampler];
            foreach (var imageCandidate in ImageCandidates(specialization.Images, memory.Resource, specialization.IndirectImageCandidates))
                foreach (var samplerCandidate in samplerCandidates)
                    samplerByImageMemory[(index, imageCandidate, samplerCandidate)] = ResolveSampler(samplerCandidate, info.Images[(int)imageCandidate]);
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

        return new SpecializedResourceInfo { Info = info, SamplerByMemoryIndex = samplerByMemory,
            FiniteSamplersByMemoryIndex = finiteSamplersByMemory, SamplerByImageMemoryIndex = samplerByImageMemory };
    }
}

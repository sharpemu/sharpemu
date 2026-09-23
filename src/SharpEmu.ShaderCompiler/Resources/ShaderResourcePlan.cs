// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// An image access whose descriptor comes from a material-table key: the emitter reads
// the key and selects the heap candidate at run time.
public sealed record IndirectImageAccess(int MemoryIndex, ScalarValue Key, uint HeapSource)
{
    public bool KeyIsAddressOffset { get; init; }
}

// The immutable resource analysis of one program: graph, descriptor sources, flattened
// reads and dense tables. Built once, materialised per draw, free of any draw's user data.
public sealed class ShaderResourcePlan
{
    private ShaderResourcePlan(ScalarValueGraph graph, ShaderStage stage, ulong hash)
    {
        Graph = graph;
        Stage = stage;
        Hash = hash;
    }

    public ScalarValueGraph Graph { get; }
    public ShaderStage Stage { get; }
    public ulong Hash { get; }
    public uint UserDataBase => Graph.UserDataBase;
    public uint UserDataCount => Graph.UserDataCount;
    public MemoryAccessTable Memory => Graph.Memory;
    public IReadOnlyList<DescriptorSource> DescriptorSources { get; private set; } = [];
    public IReadOnlyList<uint> MaterializationSources { get; private set; } = [];
    public IReadOnlyList<ResourceBranchBlock> ResourceBranches { get; private set; } = [];
    public IReadOnlyList<ResourceTableRead> TableReads { get; private set; } = [];
    public IReadOnlyDictionary<int, uint> FlattenedSlotByMemoryIndex { get; private set; } = new Dictionary<int, uint>();
    public IReadOnlyList<ScalarValue> DynamicReads { get; private set; } = [];
    public IReadOnlyList<byte> CleanFlatSlots { get; private set; } = [];
    public IReadOnlyList<IndirectImageAccess> IndirectImages { get; private set; } = [];
    public bool RequiresSpecializationMemory { get; private set; }
    public ShaderResourceInfo Info { get; private set; } = new();

    // The handles each memory access reads, after flattened reads were replaced.
    public MemoryAccessBinding?[] Accesses { get; private set; } = [];

    public static ShaderResourcePlan Extract(Gen5ShaderProgram program, ShaderStage stage, ulong hash, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads = null, Action<ShaderResourcePlan>? beforeResourceTracking = null, uint waveSize = 64)
    {
        var graph = ScalarValueGraph.Build(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize);
        var plan = new ShaderResourcePlan(graph, stage, hash);
        var reads = ResourceTableReadPlanner.Plan(graph, stage, hash);
        var memo = new Dictionary<ScalarValue, ScalarValue>();
        ScalarValue Rewrite(ScalarValue value) => graph.Substitute(value, reads.Replacements, memo);
        ScalarValue RewriteRead(ScalarValue read)
        {
            // A flattened read keeps its own root; only its handle and offset are rewritten.
            var handle = Rewrite(read.Operands[0]);
            var offset = Rewrite(read.Operands[1]);
            return ReferenceEquals(handle, read.Operands[0]) && ReferenceEquals(offset, read.Operands[1])
                ? read
                : graph.MemoryRead(read.Kind, handle, offset, read.MemoryIndex);
        }

        plan.TableReads = reads.Reads.Select(read => new ResourceTableRead(RewriteRead(read.Value), read.FlatOffset)).ToList();
        plan.FlattenedSlotByMemoryIndex = reads.FlattenedSlotByMemoryIndex;
        plan.DynamicReads = reads.DynamicReads.Select(RewriteRead).ToList();
        plan.Accesses = new MemoryAccessBinding?[graph.Accesses.Length];
        for (var index = 0; index < graph.Accesses.Length; index++)
        {
            if (graph.Accesses[index] is not { } access)
            {
                continue;
            }

            plan.Accesses[index] = new MemoryAccessBinding(
                access.Handle is null ? null : Rewrite(access.Handle),
                access.SamplerHandle is null ? null : Rewrite(access.SamplerHandle),
                access.Read is null ? null : (reads.Replacements.ContainsKey(access.Read) ? RewriteRead(access.Read) : Rewrite(access.Read)),
                access.Offset is null ? null : Rewrite(access.Offset));
        }

        // Diagnostics observe rewritten values before descriptor validation.
        beforeResourceTracking?.Invoke(plan);
        var tracked = ResourceTracker.Track(plan);
        plan.DescriptorSources = tracked.Sources;
        plan.Info = tracked.Info;
        plan.IndirectImages = tracked.IndirectImages;
        plan.DynamicReads = plan.DynamicReads.Where(read => !tracked.IndirectReads.Contains(read)).ToList();

        var materialization = new List<uint>();
        foreach (var buffer in plan.Info.Buffers)
        {
            materialization.Add(buffer.Source);
        }

        foreach (var image in plan.Info.Images)
        {
            if (plan.DescriptorSources[(int)image.Source].IndirectImage is not null)
            {
                plan.RequiresSpecializationMemory = true;
            }
            else
            {
                materialization.Add(image.Source);
            }
        }

        foreach (var sampler in plan.Info.Samplers)
        {
            materialization.Add(sampler.Source);
        }

        plan.MaterializationSources = materialization;

        var cleanSlots = new byte[plan.TableReads.Count];
        foreach (var image in plan.Info.Images)
        {
            if (plan.DescriptorSources[(int)image.Source].IndirectImage is not { } indirect)
            {
                continue;
            }

            if (indirect.DirectCandidates is { } directCandidates)
            {
                foreach (var candidate in directCandidates)
                    plan.MarkCleanFlatSlots(plan.DescriptorSources[(int)candidate.Source], cleanSlots);
            }
            else if (indirect.Dense)
            {
                plan.MarkCleanFlatSlots(plan.DescriptorSources[(int)indirect.HeapSource], cleanSlots);
            }
            else
            {
                plan.MarkCleanFlatSlots(plan.DescriptorSources[(int)indirect.MaterialSource], cleanSlots);
                plan.MarkCleanFlatSlots(plan.DescriptorSources[(int)indirect.HeapSource], cleanSlots);
            }
        }

        plan.CleanFlatSlots = cleanSlots;
        plan.ResourceBranches = ResourceBranchBlock.Build(plan, Rewrite);
        plan.DeviceAddressRanges = DeviceAddressRangePlanner.Plan(plan);

        // Each written handle owns three flattened slots after the table reads: base
        // low, base high and size, which the shader checks every store against.
        var writtenSlots = new Dictionary<uint, uint>();
        foreach (var range in plan.DeviceAddressRanges)
        {
            if (range.Written)
            {
                writtenSlots[range.Handle] = (uint)(plan.TableReads.Count + writtenSlots.Count * WrittenRangeDwordCount);
            }
        }

        plan.WrittenRangeSlotByHandle = writtenSlots;
        return plan;
    }

    public const int WrittenRangeDwordCount = 3;

    // Planned after tracking; optional, and never a reason for the plan to fail.
    public IReadOnlyList<DeviceAddressRangePlan> DeviceAddressRanges { get; private set; } = [];

    public IReadOnlyDictionary<uint, uint> WrittenRangeSlotByHandle { get; private set; } = new Dictionary<uint, uint>();

    public int WrittenRangeCount => WrittenRangeSlotByHandle.Count;

    // The flattened table the host fills per draw: table reads, then the written ranges.
    public int FlattenedTableReservedCount => TableReads.Count + WrittenRangeCount * WrittenRangeDwordCount;

    // Every flattened slot an indirect table's descriptor depends on must be read
    // through the clean reader, including the slots those reads depend on.
    private void MarkCleanFlatSlots(DescriptorSource source, byte[] slots)
    {
        var pending = new Stack<ScalarValue>(source.Dwords);
        var visited = new HashSet<ScalarValue>();
        while (pending.Count != 0)
        {
            var value = pending.Pop();
            if (!visited.Add(value))
            {
                continue;
            }

            if (value.Kind == ScalarValueKind.ResourceTableWord)
            {
                var slot = (int)value.Payload;
                if (slot < slots.Length)
                {
                    slots[slot] = 1;
                    pending.Push(TableReads[slot].Value);
                }

                continue;
            }

            foreach (var operand in value.Operands)
            {
                pending.Push(operand);
            }
        }
    }

    public bool ValidateRuntimeValue(ScalarValue value) =>
        new RuntimeValueValidator(Graph, UserDataBase, UserDataCount, TableReads.Count).Validate(value);
}

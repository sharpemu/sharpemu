// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// Converts a frontend's descriptor to the common buffer layout without changing guest memory.
public delegate uint[] BufferDescriptorNormalizer(ReadOnlySpan<uint> words);
public delegate uint[] ImageDescriptorNormalizer(ReadOnlySpan<uint> words, ShaderStage stage, ulong shaderHash, string source);

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
    public BufferDescriptorNormalizer? NormalizeBufferDescriptor { get; private init; }
    public ImageDescriptorNormalizer? NormalizeImageDescriptor { get; private init; }
    public Func<long>? ImageNormalizationVersion { get; private init; }
    public ShaderStage Stage { get; }
    public ulong Hash { get; }

    // The layout of this plan's loop-selected image tables: the most candidates and keys any of
    // them has needed so far (see ResourceMaterializer.FitSharedImageTables).
    internal int SharedImageCandidates;
    internal int SharedImageKeys;
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
    public IReadOnlyList<BufferCandidateTablePlan> BufferCandidateTables { get; private set; } = [];
    public bool RequiresSpecializationMemory { get; private set; }
    public ShaderResourceInfo Info { get; private set; } = new();

    internal RawReadPrefetchPlan? RawReadPrefetch { get; set; }
    private readonly object _compileGate = new();
    private CompiledResourceEvaluator? _compiledEvaluator;
    private CompiledResourceTransaction? _compiledTransaction;
    private CompiledResourceTransaction? _compiledTableTransaction;
    internal ResourceMaterializer.SpecializationGuard? SpecializationGuard;
    private int _compiledEvaluatorState;

    private sealed record ProgramBox(MaterializationProgram? Program, string Ineligibility);

    private ProgramBox? _materializationProgram;

    internal MaterializationProgram? MaterializationProgram =>
        (Volatile.Read(ref _materializationProgram) ?? InitializeMaterializationProgram()).Program;

    internal string MaterializationProgramIneligibility =>
        (Volatile.Read(ref _materializationProgram) ?? InitializeMaterializationProgram()).Ineligibility;

    private ProgramBox InitializeMaterializationProgram()
    {
        var program = Resources.MaterializationProgram.Build(this, out var reason);
        var built = new ProgramBox(program, reason);
        return Interlocked.CompareExchange(ref _materializationProgram, built, null) ?? built;
    }

    internal object?[]? SpecializationMemos;
    internal int NextSpecializationMemo;

    internal CompiledResourceEvaluator? CompiledEvaluator
    {
        get
        {
            if (Volatile.Read(ref _compiledEvaluatorState) == 2) return _compiledEvaluator;
            if (Interlocked.CompareExchange(ref _compiledEvaluatorState, 1, 0) == 0) CompiledResourceEvaluator.Enqueue(this);
            return null;
        }
    }

    internal CompiledResourceEvaluator? CompileEvaluatorNow()
    {
        lock (_compileGate)
        {
            if (Volatile.Read(ref _compiledEvaluatorState) == 2) return _compiledEvaluator;
            CompiledResourceEvaluator? compiled;
            try
            {
                compiled = CompiledResourceEvaluator.Build(this);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                compiled = null;
            }

            try
            {
                _compiledTransaction = CompiledResourceTransaction.Build(this);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _compiledTransaction = null;
            }

            try
            {
                _compiledTableTransaction = CompiledResourceTransaction.Build(this, tableOnly: true);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _compiledTableTransaction = null;
            }

            _compiledEvaluator = compiled;
            Volatile.Write(ref _compiledEvaluatorState, 2);
            return compiled;
        }
    }

    internal CompiledResourceTransaction? CompiledTransaction
    {
        get
        {
            _ = CompiledEvaluator;
            return Volatile.Read(ref _compiledEvaluatorState) == 2 ? _compiledTransaction : null;
        }
    }

    internal CompiledResourceTransaction? CompiledTableTransaction
    {
        get
        {
            _ = CompiledEvaluator;
            return Volatile.Read(ref _compiledEvaluatorState) == 2 ? _compiledTableTransaction : null;
        }
    }

    // The handles each memory access reads, after flattened reads were replaced.
    public MemoryAccessBinding?[] Accesses { get; private set; } = [];

    // The image accesses whose image or sampler a linked callee's call record holds, and what each callee reads through its
    // records' arguments besides them (by its slot among the program's call targets; empty unless linked with run-time
    // arguments).
    public IReadOnlyList<CallRecordAccess> CallRecords { get; private set; } = [];
    public IReadOnlyList<CallRecordCalleePlan> CallRecordCallees { get; private set; } = [];

    // The argument pair each run-time call entry keeps (ScalarValueGraph.CallRecordArguments), after flattened reads were
    // replaced as in the accesses.
    public IReadOnlyDictionary<uint, (ScalarValue Low, ScalarValue High)> CallRecordArguments { get; private set; } =
        new Dictionary<uint, (ScalarValue Low, ScalarValue High)>();

    public static ShaderResourcePlan Extract(Gen5ShaderProgram program, ShaderStage stage, ulong hash, uint userDataBase, uint userDataCount,
        IReadOnlySet<uint>? fixedFunctionVertexLoads = null, Action<ShaderResourcePlan>? beforeResourceTracking = null, uint waveSize = 64,
        bool callRecords = false, bool callMarks = false, int dynamicUserData = -1,
        BufferDescriptorNormalizer? normalizeBufferDescriptor = null,
        ImageDescriptorNormalizer? normalizeImageDescriptor = null, Func<long>? imageNormalizationVersion = null)
    {
        // Mesh system inputs are not uniform user data, even when a GS pointer occupies s0:s1.
        var graph = ScalarGraphDiskCache.Build(program, userDataBase, userDataCount, fixedFunctionVertexLoads, waveSize,
            excludedUserDataRegisters: stage == ShaderStage.Mesh ? 0xFCUL : 0, dynamicUserData: dynamicUserData);
        var plan = new ShaderResourcePlan(graph, stage, hash)
        {
            NormalizeBufferDescriptor = normalizeBufferDescriptor,
            NormalizeImageDescriptor = normalizeImageDescriptor,
            ImageNormalizationVersion = imageNormalizationVersion,
        };
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
                access.Offset is null ? null : Rewrite(access.Offset),
                access.Active is null ? null : Rewrite(access.Active));
        }

        plan.CallRecordArguments = graph.CallRecordArguments.ToDictionary(
            static entry => entry.Key, entry => (Rewrite(entry.Value.Low), Rewrite(entry.Value.High)));

        // Diagnostics observe rewritten values before descriptor validation.
        beforeResourceTracking?.Invoke(plan);
        var tracked = ResourceTracker.Track(plan, callRecords);
        plan.DescriptorSources = tracked.Sources;
        plan.Info = tracked.Info;
        plan.CallRecords = tracked.CallRecords;
        if (program.Instructions.Any(instruction => instruction.Control is ShaderCallFaultControl))
            plan.Info.UsesDeviceAddresses = true;
        if (plan.Info.UsesDeviceAddresses && Gen5ShaderCallLinker.IsLinked(program))
            plan.Info.UsesDeviceAddressFallback = true;
        if (callMarks && program.Instructions.Any(static instruction => instruction.Control is ShaderCallFaultControl { ReturnPc: not null }))
            plan.Info.UsesCallMarks = true;
        plan.IndirectImages = tracked.IndirectImages;
        plan.BufferCandidateTables = tracked.BufferCandidateTables;
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
                if (indirect.WaveIndexed is not null)
                    plan.MarkHeapReadSlots(plan.DescriptorSources[(int)indirect.HeapSource], cleanSlots);
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
        plan.CallRecordCallees = CallRecordPlanner.Plan(plan);

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

    // A wave-indexed table picks its keys from a mask word read off the same heap.
    private void MarkHeapReadSlots(DescriptorSource heap, byte[] slots)
    {
        for (var slot = 0; slot < TableReads.Count && slot < slots.Length; slot++)
        {
            var value = TableReads[slot].Value;
            if (value.Kind == ScalarValueKind.ScalarAddressWord && value.Operands.Length != 0 &&
                value.Operands[0].Kind == ScalarValueKind.AddressHandle &&
                value.Operands[0].Operands.Length == heap.Dwords.Length &&
                value.Operands[0].Operands.Zip(heap.Dwords).All(pair => ReferenceEquals(pair.First, pair.Second) || Graph.Equivalent(pair.First, pair.Second)))
            {
                slots[slot] = 1;
            }
        }
    }

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

    public bool ValidateRuntimeValue(ScalarValue value) => ValidateRuntimeValue(value, out _);

    public bool ValidateRuntimeValue(ScalarValue value, out bool controlDependent)
    {
        var validator = new RuntimeValueValidator(Graph, UserDataBase, UserDataCount, TableReads.Count);
        var ok = validator.Validate(value);
        controlDependent = validator.ControlDependent;
        return ok;
    }
}

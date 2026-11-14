// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// A raw buffer whose base/format are dispatch inputs and whose count is loaded by the GPU.
// The shader retains the actual count. Only residency can use a count-independent upper bound.
public sealed class DeviceRecordCountPlan
{
    // SHARPEMU_DEVICE_RECORD_COUNTS=0 plans no such buffer.
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SHARPEMU_DEVICE_RECORD_COUNTS") != "0";
    public const uint CacheIdentity = 0x44524301;
    private readonly Dictionary<int, ScalarValue> _handles;
    private readonly HashSet<ScalarValue> _skipCounts;
    private readonly HashSet<uint> _pcs;
    private readonly object _gate = new();
    private sealed class ProofEntry(uint[] userData, DispatchBufferFootprint.Dispatch dispatch)
    {
        public uint[] UserData { get; } = userData;
        public DispatchBufferFootprint.Dispatch Dispatch { get; } = dispatch;
        public Dictionary<uint, DispatchBufferFootprint.Span>? Spans { get; set; }
    }
    private readonly ProofEntry?[] _proofs = new ProofEntry[32];
    private uint[] _inputWords = [];
    private long _proofRevision;
    internal long ProofRevision => Volatile.Read(ref _proofRevision);
    // Tests can control completion without timing races. Production always uses the bounded worker.
    internal Func<Action, bool> QueueProof { get; set; } = DeviceCountProofWorker.TryQueue;

    internal long MaterializationRevision(ResourceRuntimeInputs inputs)
    {
        lock (_gate)
        {
            if (inputs.ComputeState is { DeviceCountProof: true, Dispatch: { } dispatch })
            {
                foreach (var entry in _proofs)
                {
                    if (entry?.Spans is not { } spans || entry.Dispatch != dispatch ||
                        entry.UserData.Length != inputs.UserData.Count) continue;
                    var matches = true;
                    for (var index = 0; index < entry.UserData.Length; index++)
                        if (entry.UserData[index] != inputs.UserData[index]) { matches = false; break; }
                    if (matches) return Mapped(spans, inputs) ? _proofRevision : -_proofRevision;
                }
                // Eviction by pending/refused shapes need not publish a new proof. A previously proved
                // materialization must still stop serving until this exact input is proved and mapped again.
                return -_proofRevision;
            }
            return _proofRevision;
        }
    }

    private static bool Mapped(Dictionary<uint, DispatchBufferFootprint.Span> spans, ResourceRuntimeInputs inputs)
    {
        foreach (var span in spans.Values)
            if (inputs.IsRangeMapped?.Invoke(span.Base, span.Size) != true) return false;
        return true;
    }

    private DeviceRecordCountPlan(ScalarValueGraph graph, Dictionary<int, ScalarValue> handles)
    {
        _handles = handles;
        _skipCounts = handles.Values.ToHashSet();
        _pcs = handles.Keys.Select(index => graph.Memory[index].Pc).ToHashSet();
    }

    public bool Contains(int memoryIndex) => _handles.ContainsKey(memoryIndex);
    public bool SkipCount(ScalarValue handle) => _skipCounts.Contains(handle);

    // The alternate ordinary plan handles descriptor modes the device raw-buffer lowering does not support.
    // This reads only expressions proven independent of guest memory during Create.
    public bool SupportsDescriptors(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs);
        foreach (var handle in _handles.Values)
            if (!evaluator.Evaluate(handle.Operands[1], out var high) || !evaluator.Evaluate(handle.Operands[3], out var format) ||
                (high & 0x80000000) != 0 || (format & (1u << 23)) != 0) return false;
        return true;
    }

    public static DeviceRecordCountPlan? Create(ScalarValueGraph graph, ShaderStage stage)
    {
        if (stage != ShaderStage.Compute || graph.CallArgumentRegisters != 0 || graph.Program.IsFusedProgram ||
            Gen5ShaderCallLinker.IsLinked(graph.Program) || graph.Program.Instructions.Count > 2048 ||
            graph.Program.Instructions.Any(instruction => instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64"))
            return null;
        var handles = new Dictionary<int, ScalarValue>();
        for (var index = 0; index < graph.Accesses.Length; index++)
        {
            var memory = graph.Memory[index];
            if (memory.Kind != MemoryResourceKind.Buffer || memory.Typed || memory.Formatted ||
                memory.Access is not (MemoryAccess.Read or MemoryAccess.Write) ||
                !(memory.Opcode.StartsWith("BufferLoadDword", StringComparison.Ordinal) ||
                  memory.Opcode.StartsWith("BufferStoreDword", StringComparison.Ordinal)) ||
                graph.Accesses[index]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } handle ||
                (graph.ResolveInvariantPhi(handle.Operands[2]) ?? handle.Operands[2]) is not { Kind: ScalarValueKind.ScalarBufferWord } count ||
                !MemoryFree(graph, handle.Operands[0], []) || !MemoryFree(graph, handle.Operands[1], []) || !MemoryFree(graph, handle.Operands[3], []) ||
                count.Operands.Length != 2 || !count.Operands[1].IsConstant ||
                !count.Operands[0].Operands.All(value => MemoryFree(graph, value, []))) continue;
            handles.Add(index, handle);
        }
        return handles.Count == 0 ? null : new(graph, handles);
    }

    private static bool MemoryFree(ScalarValueGraph graph, ScalarValue value, HashSet<ScalarValue> visiting)
    {
        // Unmodified inputs and counts may cross loop headers. Resolve only proven invariant phis;
        // a varying count/base still refuses admission and follows the ordinary plan.
        value = graph.ResolveInvariantPhi(value) ?? value;
        if (value.IsConstant || value.Kind is ScalarValueKind.UserData or ScalarValueKind.ShaderBase) return true;
        if (value.Kind != ScalarValueKind.Operation || !RuntimeValueValidator.IsUniformOperation(value.Operation) ||
            !visiting.Add(value)) return false;
        var result = value.Operands.All(operand => MemoryFree(graph, operand, visiting));
        visiting.Remove(value);
        return result;
    }

    internal DeviceAddressRange[] Append(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, DeviceAddressRange[] ordinary)
    {
        Dictionary<uint, DispatchBufferFootprint.Span>? spans = null;
        if (inputs.ComputeState is { HasPartialWorkgroups: false, DeviceCountProof: true, Dispatch: { } dispatch })
        {
            // Indexing marks every input in the materialization cache's dependency recorder, even on a proof-cache hit.
            lock (_gate)
            {
                if (_inputWords.Length != inputs.UserData.Count) _inputWords = new uint[inputs.UserData.Count];
                for (var index = 0; index < inputs.UserData.Count; index++)
                    _inputWords[index] = inputs.UserData[index];
                bool Matches(ProofEntry? entry) => entry is not null && entry.Dispatch == dispatch &&
                    _inputWords.AsSpan().SequenceEqual(entry.UserData);
                var cached = _proofs[0];
                if (!Matches(cached))
                {
                    cached = null;
                    for (var index = 1; index < _proofs.Length; index++)
                    {
                        var entry = _proofs[index];
                        if (!Matches(entry)) continue;
                        cached = entry;
                        Array.Copy(_proofs, 0, _proofs, 1, index);
                        _proofs[0] = entry;
                        break;
                    }
                }
                if (cached is null)
                {
                    // A bounded LRU retains alternating dispatch shapes and refusals. It stores only mathematics;
                    // current mappings and the actual count fallback are still checked below on every evaluation.
                    var userData = (uint[])_inputWords.Clone();
                    cached = new(userData, dispatch);
                    Array.Copy(_proofs, 0, _proofs, 1, _proofs.Length - 1);
                    _proofs[0] = cached;
                    var pending = cached;
                    // The closure owns only immutable analysis inputs, never readers, mappings or GPU state.
                    var program = plan.Graph.Program;
                    var userDataBase = plan.Graph.UserDataBase;
                    if (!QueueProof(() => PrepareProof(program, userDataBase, pending)))
                    {
                        _proofs[0] = null; // Queue pressure is temporary: retry on a later dispatch.
                        // The cached fallback must retry admission too, even when its count word stays unchanged.
                        Interlocked.Increment(ref _proofRevision);
                    }
                }
                spans = cached.Spans;
            }
        }
        // Use one eligibility state for all selected ranges. Materialization-cache hits check it too,
        // including unmap/remap changes after an immutable mathematical proof was published.
        if (spans is not null && !Mapped(spans, inputs)) spans = null;
        var result = new List<DeviceAddressRange>(ordinary);
        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs);
        var seen = new HashSet<(ulong Base, ulong Size, bool Written)>();
        foreach (var (index, handle) in _handles)
        {
            if (!evaluator.Evaluate(handle.Operands[0], out var low) || !evaluator.Evaluate(handle.Operands[1], out var high) ||
                !evaluator.Evaluate(handle.Operands[3], out var format))
                throw new ResourcePlanException("The device-count buffer address cannot be resolved.");
            var address = low | ((ulong)(high & 0xFFFF) << 32);
            if ((high & 0x80000000) != 0 || (format & (1u << 23)) != 0)
                throw new ResourcePlanException("The device-count buffer uses unsupported swizzling or thread indices.");
            ulong size;
            var memory = plan.Memory[index];
            if (spans is not null && !spans.ContainsKey(memory.Pc)) // All paths proved inactive.
            {
                continue;
            }
            if (spans is not null && spans.TryGetValue(memory.Pc, out var span) && span.Base == address &&
                inputs.IsRangeMapped?.Invoke(address, span.Size) == true)
            {
                size = span.Size;
            }
            else
            {
                // Same device-descriptor shader, exact original residency and synchronization when the proof refuses.
                if (!evaluator.Evaluate(handle.Operands[2], out var count))
                    throw new ResourcePlanException("The device-count buffer fallback count cannot be resolved.");
                size = (ulong)count * Math.Max((high >> 16) & 0x3FFF, 1u);
            }
            if (size == 0) continue;
            if (size > 0x1_0000_0000_0000ul - address)
                throw new ResourcePlanException("The device-count buffer wraps the guest address space.");
            var written = memory.Access == MemoryAccess.Write;
            if (seen.Add((address, size, written))) result.Add(new((uint)result.Count, address, size, true, written));
        }
        return result.ToArray();
    }

    private void PrepareProof(Gen5ShaderProgram program, uint userDataBase, ProofEntry pending)
    {
        var proved = DispatchBufferFootprint.TryEvaluate(program, pending.UserData, userDataBase,
            pending.Dispatch, _pcs, out var spans);
        lock (_gate)
        {
            if (!proved || !_proofs.Contains(pending)) return;
            pending.Spans = spans;
            // Previously cached exact-count snapshots must discover the new proof on their next use.
            Interlocked.Increment(ref _proofRevision);
        }
    }
}

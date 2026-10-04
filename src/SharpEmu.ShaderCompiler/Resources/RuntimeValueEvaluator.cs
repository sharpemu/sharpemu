// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// Evaluates graph values against one draw's inputs. Results are memoised so values
// shared by several descriptors and flattened reads are computed once.
public sealed class RuntimeValueEvaluator
{
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    private readonly ShaderResourcePlan _plan;
    private readonly ResourceRuntimeInputs _inputs;
    private readonly IReadOnlyList<byte> _cleanFlatSlots;
    private readonly RuntimeValueEvaluator? _cleanEvaluator;
    private readonly ScalarValue? _activeMask;
    private readonly ScalarValueCache _cache;
    private readonly List<ScalarValue> _visiting;
    private string? _failureDetail;

    internal string? FailureDetail => _failureDetail;

    private static bool Fail(string message)
    {
        Console.Error.WriteLine($"shader runtime value evaluation failed: {message}");
        return false;
    }

    public RuntimeValueEvaluator(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots = null,
        RuntimeValueEvaluator? cleanEvaluator = null,
        ScalarValue? activeMask = null)
        : this(plan, inputs, cleanFlatSlots, cleanEvaluator, activeMask, new ScalarValueCache(), [])
    {
    }

    internal RuntimeValueEvaluator(
        RuntimeEvaluationScratch scratch,
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots = null,
        RuntimeValueEvaluator? cleanEvaluator = null,
        ScalarValue? activeMask = null)
        : this(plan, inputs, cleanFlatSlots, cleanEvaluator, activeMask, scratch.Values, scratch.Visiting)
    {
    }

    private RuntimeValueEvaluator(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots,
        RuntimeValueEvaluator? cleanEvaluator,
        ScalarValue? activeMask,
        ScalarValueCache cache,
        List<ScalarValue> visiting)
    {
        _cache = cache;
        _visiting = visiting;
        _plan = plan;
        _inputs = inputs;
        _cleanFlatSlots = cleanFlatSlots ?? [];
        _cleanEvaluator = cleanEvaluator;
        _activeMask = activeMask;
    }

    public bool Evaluate(ScalarValue value, out uint result)
    {
        if (!EvaluateWide(value, out var wide))
        {
            result = 0;
            return false;
        }

        result = (uint)wide;
        return true;
    }

    public bool EvaluateWide(ScalarValue value, out ulong result)
    {
        result = 0;
        if (value.IsConstant)
        {
            result = value.Payload;
            return true;
        }

        if (_activeMask is not null && value.Kind == ScalarValueKind.Select && ReferenceEquals(value.Operands[0], _activeMask))
        {
            return EvaluateWide(value.Operands[1], out result);
        }

        if (_cache.TryGetValue(value, out result))
        {
            return true;
        }

        if (_visiting.Contains(value))
        {
            _failureDetail ??= DescribeFailure(value, "cyclic_dependency");
            return false;
        }

        _visiting.Add(value);
        var evaluated = EvaluateNode(value, out var computed);
        _visiting.RemoveAt(_visiting.Count - 1);
        if (!evaluated)
        {
            _failureDetail ??= DescribeFailure(value, "cannot_evaluate");
            return false;
        }

        _cache[value] = computed;
        result = computed;
        return true;
    }

    private static string DescribeFailure(ScalarValue value, string reason) =>
        $"node={value.Id} kind={value.Kind} operation={value.Operation} payload=0x{value.Payload:X} reason={reason}";

    private bool Refuse(ScalarValue value, string reason)
    {
        _failureDetail ??= DescribeFailure(value, reason);
        return false;
    }

    private bool Operand(ScalarValue value, int index, out ulong result) => EvaluateWide(value.Operands[index], out result);

    private bool EvaluateNode(ScalarValue value, out ulong result)
    {
        result = 0;
        switch (value.Kind)
        {
            case ScalarValueKind.Undefined:
                return false;
            case ScalarValueKind.MemoryAperture:
                result = Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32;
                return true;
            case ScalarValueKind.UserData:
            {
                var register = value.UserDataRegister;
                if (register < _plan.UserDataBase || register - _plan.UserDataBase >= (uint)_inputs.UserData.Count)
                {
                    return false;
                }

                result = _inputs.UserData[(int)(register - _plan.UserDataBase)];
                return true;
            }
            case ScalarValueKind.ShaderBase:
                result = _inputs.ShaderBase;
                return true;
            case ScalarValueKind.Phi:
            {
                var invariant = _plan.Graph.ResolveInvariantPhi(value);
                return invariant is not null && EvaluateWide(invariant, out result);
            }
            case ScalarValueKind.FirstLane:
            {
                using var scratch = RuntimeEvaluationScratch.Rent();
                var laneEvaluator = new RuntimeValueEvaluator(
                    scratch,
                    _plan,
                    _inputs,
                    _cleanFlatSlots,
                    _cleanEvaluator,
                    value.Operands[1]);
                var evaluated = laneEvaluator.EvaluateWide(value.Operands[0], out result);
                if (!evaluated)
                {
                    _failureDetail ??= laneEvaluator.FailureDetail;
                }
                return evaluated;
            }
            case ScalarValueKind.ResourceTableWord:
            {
                var slot = (int)value.Payload;
                if (slot >= _plan.TableReads.Count)
                {
                    return Refuse(value, $"resource_table_slot_out_of_range slot={slot}");
                }

                if (slot < _cleanFlatSlots.Count && _cleanFlatSlots[slot] != 0 && _cleanEvaluator is not null)
                {
                    var evaluated = _cleanEvaluator.EvaluateWide(_plan.TableReads[slot].Value, out result);
                    if (!evaluated)
                    {
                        _failureDetail ??= _cleanEvaluator.FailureDetail;
                    }
                    return evaluated;
                }

                return EvaluateWide(_plan.TableReads[slot].Value, out result);
            }
            case ScalarValueKind.ScalarAddressWord:
            case ScalarValueKind.ScalarBufferWord:
                return EvaluateRawRead(value, out result);
            case ScalarValueKind.Select:
            {
                if (!Operand(value, 0, out var condition) || !Operand(value, 1, out var whenTrue) || !Operand(value, 2, out var whenFalse))
                {
                    return false;
                }

                result = condition != 0 ? whenTrue : whenFalse;
                return true;
            }
            case ScalarValueKind.Operation:
            {
                if (!RuntimeValueValidator.IsUniformOperation(value.Operation))
                {
                    return false;
                }

                Span<ulong> operands = stackalloc ulong[value.Operands.Length];
                for (var index = 0; index < operands.Length; index++)
                {
                    if (!Operand(value, index, out operands[index]))
                    {
                        return false;
                    }
                }

                return ScalarOperationSemantics.TryEvaluate(value.Operation, operands, out result);
            }
            default:
                return false;
        }
    }

    // A raw read adds the immediate and dynamic offsets to the 48-bit handle base, checks
    // a buffer read against its records, and reads one aligned dword.
    private bool EvaluateRawRead(ScalarValue value, out ulong result)
    {
        result = 0;
        if (value.MemoryIndex >= _plan.Memory.Count)
        {
            return Refuse(value, $"memory_index_out_of_range index={value.MemoryIndex}");
        }

        var memory = _plan.Memory[value.MemoryIndex];
        var handle = value.Operands[0];
        if (handle.Operands.Length < 2 ||
            !EvaluateWide(handle.Operands[0], out var low) ||
            !EvaluateWide(handle.Operands[1], out var high) ||
            !Operand(value, 1, out var offset))
        {
            return Refuse(value, "address_inputs_unavailable");
        }

        var baseAddress = ((high << 32) | (uint)low) & AddressMask;
        ulong records = 0;
        if (value.Kind == ScalarValueKind.ScalarBufferWord &&
            (handle.Operands.Length != 4 ||
             !EvaluateWide(handle.Operands[2], out records) ||
             !EvaluateWide(handle.Operands[3], out _)))
        {
            return Refuse(value, "buffer_descriptor_unavailable");
        }

        var immediate = (long)(int)memory.Offset;
        switch (ResolveRawAddress(value.Kind, baseAddress, high, records, immediate, (uint)offset, out var address))
        {
            case RawAddress.Failed:
                if (value.Kind == ScalarValueKind.ScalarBufferWord)
                {
                    if (immediate < 0)
                    {
                        return Refuse(value, $"negative_buffer_immediate immediate={immediate}");
                    }

                    var byteOffset = (ulong)immediate + (uint)offset;
                    var aligned = byteOffset & ~3ul;
                    var stride = ((uint)high >> 16) & 0x3FFFu;
                    var size = stride == 0 ? (ulong)(uint)records : (ulong)stride * (uint)records;
                    return Refuse(
                        value,
                        $"buffer_read_out_of_range offset=0x{aligned:X} size=0x{size:X}");
                }

                var relative = (immediate & ~3L) + (long)((uint)offset & ~3u);
                return Refuse(
                    value,
                    $"address_out_of_range base=0x{baseAddress:X16} relative={relative}");
            case RawAddress.Zero:
                result = 0;
                return true;
        }

        if (_inputs.ReadMemory is null || !_inputs.ReadMemory(address, out var word))
        {
            // Every read is evaluated up front, including ones in branches the shader skips
            // when a pointer is null. A load through a null base cannot execute on hardware,
            // so its value is never used.
            if ((baseAddress & ~3ul) == 0)
            {
                result = 0;
                return true;
            }

            var access = _plan.Memory[value.MemoryIndex];
            var reason = _inputs.ReadMemory is null ? "reader_unavailable" : "reader_refused";
            return Refuse(
                value,
                $"guest_read_failed memory={value.MemoryIndex} pc=0x{access.Pc:X8} " +
                $"address=0x{address:X16} {reason}");
        }

        result = word;
        return true;
    }

    internal enum RawAddress : byte
    {
        Read,
        Zero,
        Failed,
    }

    internal static RawAddress ResolveRawAddress(ScalarValueKind kind, ulong baseAddress, ulong high, ulong records, long immediate, uint offset,
        out ulong address)
    {
        address = 0;
        if (kind == ScalarValueKind.ScalarBufferWord)
        {
            if (immediate < 0)
            {
                return RawAddress.Failed;
            }

            var byteOffset = (ulong)immediate + offset;
            var aligned = byteOffset & ~3ul;
            var stride = ((uint)high >> 16) & 0x3FFFu;
            var size = stride == 0 ? (ulong)(uint)records : (ulong)stride * (uint)records;
            if (aligned > size || size - aligned < sizeof(uint))
            {
                // An unbound (empty) V# reads as zero; overrunning a bound buffer stays a failure.
                return (uint)records == 0 ? RawAddress.Zero : RawAddress.Failed;
            }

            address = ((baseAddress & ~3ul) + byteOffset) & ~3ul;
            return RawAddress.Read;
        }

        var relative = (immediate & ~3L) + (long)(offset & ~3u);
        return AddSignedAddress(baseAddress & ~3ul, relative, out address) ? RawAddress.Read : RawAddress.Failed;
    }

    internal bool TryEvaluateRawBase(ScalarValue handle, ScalarValueKind kind, out ulong baseAddress, out ulong high, out ulong records)
    {
        baseAddress = 0;
        high = 0;
        records = 0;
        if (handle.Operands.Length < 2 ||
            !EvaluateWide(handle.Operands[0], out var low) ||
            !EvaluateWide(handle.Operands[1], out high))
        {
            return false;
        }

        if (kind == ScalarValueKind.ScalarBufferWord &&
            (handle.Operands.Length != 4 ||
             !EvaluateWide(handle.Operands[2], out records) ||
             !EvaluateWide(handle.Operands[3], out _)))
        {
            return false;
        }

        baseAddress = ((high << 32) | (uint)low) & AddressMask;
        return true;
    }

    internal bool IsEvaluated(ScalarValue value) => _cache.TryGetValue(value, out _);

    internal void Seed(ScalarValue value, ulong result) => _cache[value] = result;

    internal ResourceRuntimeInputs Inputs => _inputs;

    private static bool AddSignedAddress(ulong baseAddress, long offset, out ulong result)
    {
        result = 0;
        if (baseAddress > AddressMask)
        {
            return false;
        }

        if (offset < 0)
        {
            var magnitude = (ulong)(-offset);
            if (magnitude > baseAddress)
            {
                return false;
            }

            result = baseAddress - magnitude;
            return true;
        }

        var forward = (ulong)offset;
        if (forward > AddressMask - baseAddress)
        {
            return false;
        }

        result = baseAddress + forward;
        return true;
    }

    // Evaluates descriptor sources and, when asked, the flattened table in one memoised
    // walk. On failure neither output changes.
    public static bool EvaluateSources(
        ShaderResourcePlan plan,
        IReadOnlyList<uint> sources,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte> cleanFlatSlots,
        bool evaluateTable,
        out List<DescriptorWords> results,
        out uint[] table)
        => EvaluateSources(plan, sources, inputs, cleanFlatSlots, evaluateTable, out results, out table, out _);

    internal static bool EvaluateSources(
        ShaderResourcePlan plan,
        IReadOnlyList<uint> sources,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte> cleanFlatSlots,
        bool evaluateTable,
        out List<DescriptorWords> results,
        out uint[] table,
        out bool[] activeSources,
        int additionalTableWords = 0)
        => EvaluateSources(
            plan,
            sources,
            inputs,
            cleanFlatSlots,
            evaluateTable,
            out results,
            out table,
            out activeSources,
            out _,
            additionalTableWords);

    internal static bool EvaluateSources(
        ShaderResourcePlan plan,
        IReadOnlyList<uint> sources,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte> cleanFlatSlots,
        bool evaluateTable,
        out List<DescriptorWords> results,
        out uint[] table,
        out bool[] activeSources,
        out string? failureDetail,
        int additionalTableWords = 0)
    {
        results = [];
        table = [];
        activeSources = [];
        failureDetail = null;
        var anyClean = false;
        foreach (var clean in cleanFlatSlots)
        {
            anyClean |= clean != 0;
        }

        if (anyClean && inputs.ReadCleanMemory is null)
        {
            failureDetail = "reason=clean_reader_unavailable";
            return Fail("clean flattened slots require a clean guest-memory reader");
        }

        using var cleanScratch = RuntimeEvaluationScratch.Rent();
        using var scratch = RuntimeEvaluationScratch.Rent();
        var cleanEvaluator = new RuntimeValueEvaluator(cleanScratch, plan, inputs.WithReader(inputs.ReadCleanMemory));
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs, cleanFlatSlots, cleanEvaluator);
        if (evaluateTable && plan.ResourceBranches.Count != 0)
            activeSources = EvaluateActiveSources(plan, inputs, cleanEvaluator);
        var evaluated = new List<DescriptorWords>(sources.Count);
        RawReadPrefetch.PrefetchSources(plan, sources, activeSources, evaluator);
        foreach (var sourceIndex in sources)
        {
            if (sourceIndex >= plan.DescriptorSources.Count)
            {
                failureDetail = $"descriptor_source={sourceIndex} reason=source_out_of_range " +
                    $"source_count={plan.DescriptorSources.Count}";
                return Fail(
                    $"descriptor source index {sourceIndex} exceeds count {plan.DescriptorSources.Count}");
            }

            var source = plan.DescriptorSources[(int)sourceIndex];
            var words = new uint[source.DwordCount];
            if (activeSources.Length == 0 || activeSources[sourceIndex])
            {
                for (var index = 0; index < words.Length; index++)
                {
                    if (!evaluator.Evaluate(source.Dwords[index], out words[index]))
                    {
                        var node = source.Dwords[index];
                        failureDetail = $"descriptor_source={sourceIndex} dword={index} " +
                            (evaluator.FailureDetail ?? DescribeFailure(node, "cannot_evaluate"));
                        return Fail(
                            $"descriptor source={sourceIndex} dword={index} node={node.Id} " +
                            $"kind={node.Kind} operation={node.Operation} payload=0x{node.Payload:X}");
                    }
                }
            }

            evaluated.Add(new DescriptorWords(words));
        }

        uint[] flattened = [];
        if (evaluateTable)
        {
            flattened = new uint[checked(plan.TableReads.Count + additionalTableWords)];
            inputs.TablePhase?.Invoke(true);
            try
            {
                RawReadPrefetch.PrefetchTable(plan, evaluator, cleanEvaluator, cleanFlatSlots);
                foreach (var read in plan.TableReads)
                {
                    var clean = read.FlatOffset < cleanFlatSlots.Count && cleanFlatSlots[(int)read.FlatOffset] != 0;
                    var selected = clean ? cleanEvaluator : evaluator;
                    if (read.FlatOffset >= plan.TableReads.Count || !selected.Evaluate(read.Value, out var word))
                    {
                    var node = read.Value;
                    failureDetail = read.FlatOffset >= plan.TableReads.Count
                        ? $"table_slot={read.FlatOffset} reason=slot_out_of_range table_count={plan.TableReads.Count}"
                        : $"table_slot={read.FlatOffset} clean={clean} " +
                            (selected.FailureDetail ?? DescribeFailure(node, "cannot_evaluate"));
                    return Fail(
                        $"flattened slot={read.FlatOffset} clean={clean} node={node.Id} " +
                        $"kind={node.Kind} operation={node.Operation} payload=0x{node.Payload:X}");
                    }

                    flattened[(int)read.FlatOffset] = word;
                }
            }
            finally
            {
                inputs.TablePhase?.Invoke(false);
            }
        }

        results = evaluated;
        table = flattened;
        return true;
    }

    private static bool[] EvaluateActiveSources(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, RuntimeValueEvaluator cleanEvaluator)
    {
        var activeSources = new bool[plan.DescriptorSources.Count];
        Array.Fill(activeSources, true);
        foreach (var block in plan.ResourceBranches)
            foreach (var source in block.Sources) activeSources[source] = false;

        var visited = new bool[plan.ResourceBranches.Count];
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.TryPop(out var blockIndex))
        {
            if (visited[blockIndex]) continue;
            visited[blockIndex] = true;
            var block = plan.ResourceBranches[blockIndex];
            foreach (var source in block.Sources) activeSources[source] = true;
            // An unreadable predicate keeps both paths; never use the general reader to choose one.
            if (block.Condition is { } condition && inputs.ReadCleanMemory is not null && cleanEvaluator.Evaluate(condition, out var value))
                pending.Push(block.Successors[value != 0 ? 0 : 1]);
            else
                foreach (var successor in block.Successors) pending.Push(successor);
        }

        ResourceMaterializationProfile.RecordActivity(activeSources);
        return activeSources;
    }

    public static bool EvaluateDescriptorSource(ShaderResourcePlan plan, uint source, ResourceRuntimeInputs inputs, out DescriptorWords result)
    {
        result = default;
        if (!EvaluateSources(plan, [source], inputs, [], evaluateTable: false, out var results, out _))
        {
            return false;
        }

        result = results[0];
        return true;
    }

    public static bool FlattenResourceTable(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, out uint[] table) =>
        EvaluateSources(plan, [], inputs, [], evaluateTable: true, out _, out table);
}

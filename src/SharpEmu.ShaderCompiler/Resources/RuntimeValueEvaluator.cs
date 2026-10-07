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
    private readonly CompiledResourceEvaluator? _compiled;
    private readonly CompiledValueCache _compiledValues;

    public RuntimeValueEvaluator(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots = null,
        RuntimeValueEvaluator? cleanEvaluator = null,
        ScalarValue? activeMask = null)
        : this(plan, inputs, cleanFlatSlots, cleanEvaluator, activeMask, new ScalarValueCache(), [], new CompiledValueCache(), true)
    {
    }

    internal RuntimeValueEvaluator(
        RuntimeEvaluationScratch scratch,
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots = null,
        RuntimeValueEvaluator? cleanEvaluator = null,
        ScalarValue? activeMask = null,
        bool useCompiled = true)
        : this(plan, inputs, cleanFlatSlots, cleanEvaluator, activeMask, scratch.Values, scratch.Visiting, scratch.CompiledValues, useCompiled)
    {
    }

    private RuntimeValueEvaluator(
        ShaderResourcePlan plan,
        ResourceRuntimeInputs inputs,
        IReadOnlyList<byte>? cleanFlatSlots,
        RuntimeValueEvaluator? cleanEvaluator,
        ScalarValue? activeMask,
        ScalarValueCache cache,
        List<ScalarValue> visiting,
        CompiledValueCache compiledValues,
        bool useCompiled)
    {
        _cache = cache;
        _visiting = visiting;
        _plan = plan;
        _inputs = inputs;
        _cleanFlatSlots = cleanFlatSlots ?? [];
        _cleanEvaluator = cleanEvaluator;
        _activeMask = activeMask;
        _compiled = useCompiled ? plan.CompiledEvaluator : null;
        _compiledValues = compiledValues;
        if (_compiled is not null) _compiledValues.EnsureCapacity(_compiled.Count);
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

        if (_compiled is not null && _compiled.TryGetIndex(value, out var compiledIndex))
        {
            return EvaluateCompiled(compiledIndex, out result);
        }

        if (_cache.TryGetValue(value, out result))
        {
            return true;
        }

        if (_visiting.Contains(value))
        {
            return false;
        }

        _visiting.Add(value);
        var evaluated = EvaluateNode(value, out var computed);
        _visiting.RemoveAt(_visiting.Count - 1);
        if (!evaluated)
        {
            return false;
        }

        _cache[value] = computed;
        result = computed;
        return true;
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
                return new RuntimeValueEvaluator(scratch, _plan, _inputs, _cleanFlatSlots, _cleanEvaluator, value.Operands[1], useCompiled: _compiled is not null)
                    .EvaluateWide(value.Operands[0], out result);
            }
            case ScalarValueKind.ResourceTableWord:
                return EvaluateTableReference((int)value.Payload, out result);
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
            return false;
        }

        var memory = _plan.Memory[value.MemoryIndex];
        var handle = value.Operands[0];
        if (handle.Operands.Length < 2 ||
            !EvaluateWide(handle.Operands[0], out var low) ||
            !EvaluateWide(handle.Operands[1], out var high) ||
            !Operand(value, 1, out var offset))
        {
            return false;
        }

        ulong records = 0;
        if (value.Kind == ScalarValueKind.ScalarBufferWord &&
            (handle.Operands.Length != 4 ||
             !EvaluateWide(handle.Operands[2], out records) ||
             !EvaluateWide(handle.Operands[3], out _)))
        {
            return false;
        }

        return ReadRawWord(value.Kind, low, high, (uint)offset, records, (int)memory.Offset, out result);
    }

    internal bool ReadRawWord(ScalarValueKind kind, ulong low, ulong high, uint offset, ulong records, long immediate, out ulong result)
    {
        result = 0;
        var baseAddress = ((high << 32) | (uint)low) & AddressMask;
        switch (ResolveRawAddress(kind, baseAddress, high, records, immediate, offset, out var address))
        {
            case RawAddress.Failed:
                return false;
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

            return false;
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

    internal bool IsEvaluated(ScalarValue value) =>
        _compiled is not null && _compiled.TryGetIndex(value, out var index)
            ? _compiledValues.Contains(index) : _cache.TryGetValue(value, out _);

    internal void Seed(ScalarValue value, ulong result)
    {
        if (_compiled is not null && _compiled.TryGetIndex(value, out var index)) _compiledValues.Store(index, result);
        else _cache[value] = result;
    }

    internal int BeginCompiled(int index, out ulong result) => _compiledValues.Begin(index, out result);
    internal void StoreCompiled(int index, ulong result) => _compiledValues.Store(index, result);
    internal void EndCompiled(int index) => _compiledValues.End(index);
    internal bool EvaluateCompiled(int index, out ulong result) => _compiled!.Evaluate(this, index, out result);
    internal bool EvaluateCompiledSpecial(int index, out ulong result) => EvaluateNode(_compiled!.Values[index], out result);
    internal bool IsCompiledActiveMask(int index) => ReferenceEquals(_activeMask, _compiled!.Values[index]);

    internal bool ReadUserData(int index, out ulong result)
    {
        result = 0;
        if ((uint)index >= (uint)_inputs.UserData.Count) return false;
        result = _inputs.UserData[index];
        return true;
    }

    internal ulong ReadShaderBase() => _inputs.ShaderBase;

    private bool EvaluateTableReference(int slot, out ulong result)
    {
        result = 0;
        if ((uint)slot >= (uint)_plan.TableReads.Count) return false;
        var selected = slot < _cleanFlatSlots.Count && _cleanFlatSlots[slot] != 0 && _cleanEvaluator is not null ? _cleanEvaluator : this;
        return selected._compiled is { } compiled
            ? selected.EvaluateCompiled(compiled.TableWords[slot], out result)
            : selected.EvaluateWide(_plan.TableReads[slot].Value, out result);
    }

    private bool EvaluateSourceWord(int source, int word, out uint result)
    {
        var value = _plan.DescriptorSources[source].Dwords[word];
        if (_compiled is null || value.IsConstant) return Evaluate(value, out result);
        var succeeded = EvaluateCompiled(_compiled.SourceWords[source][word], out var wide);
        result = (uint)wide;
        return succeeded;
    }

    private bool EvaluateTableRead(int index, out uint result)
    {
        if (_compiled is null) return Evaluate(_plan.TableReads[index].Value, out result);
        var succeeded = EvaluateCompiled(_compiled.TableWords[index], out var wide);
        result = (uint)wide;
        return succeeded;
    }

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
    {
        results = [];
        table = [];
        activeSources = [];
        var anyClean = false;
        foreach (var clean in cleanFlatSlots)
        {
            anyClean |= clean != 0;
        }

        if (anyClean && inputs.ReadCleanMemory is null)
        {
            return false;
        }

        using var cleanScratch = RuntimeEvaluationScratch.Rent();
        using var scratch = RuntimeEvaluationScratch.Rent();
        var cleanEvaluator = new RuntimeValueEvaluator(cleanScratch, plan, inputs.WithReader(inputs.ReadCleanMemory));
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs, cleanFlatSlots, cleanEvaluator);
        if (evaluateTable && plan.ResourceBranches.Count != 0)
            activeSources = EvaluateActiveSources(plan, inputs, cleanEvaluator, cleanScratch);
        var evaluated = new List<DescriptorWords>(sources.Count);
        RawReadPrefetch.PrefetchSources(plan, sources, activeSources, evaluator);
        foreach (var sourceIndex in sources)
        {
            if (sourceIndex >= plan.DescriptorSources.Count)
            {
                return false;
            }

            var source = plan.DescriptorSources[(int)sourceIndex];
            var words = new uint[source.DwordCount];
            if (activeSources.Length == 0 || activeSources[sourceIndex])
            {
                for (var index = 0; index < words.Length; index++)
                {
                    if (!evaluator.EvaluateSourceWord((int)sourceIndex, index, out words[index]))
                    {
                        return false;
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
                for (var index = 0; index < plan.TableReads.Count; index++)
                {
                    var read = plan.TableReads[index];
                    var clean = read.FlatOffset < cleanFlatSlots.Count && cleanFlatSlots[(int)read.FlatOffset] != 0;
                    var selected = clean ? cleanEvaluator : evaluator;
                    if (read.FlatOffset >= plan.TableReads.Count || !selected.EvaluateTableRead(index, out var word))
                    {
                        return false;
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

    private static bool[] EvaluateActiveSources(ShaderResourcePlan plan, ResourceRuntimeInputs inputs, RuntimeValueEvaluator cleanEvaluator,
        RuntimeEvaluationScratch scratch)
    {
        var activeSources = new bool[plan.DescriptorSources.Count];
        Array.Fill(activeSources, true);
        foreach (var block in plan.ResourceBranches)
            foreach (var source in block.Sources) activeSources[source] = false;

        var visited = scratch.PrepareBranches(plan.ResourceBranches.Count);
        var pending = scratch.PendingBranches;
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

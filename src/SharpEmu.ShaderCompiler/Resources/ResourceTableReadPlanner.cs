// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// Collects the scalar reads reachable from descriptor handles. Reads with an immediate
// offset receive compact flattened-table slots; dynamic offsets stay explicit.
public sealed class ResourceTableReadPlanner
{
    private readonly ScalarValueGraph _graph;
    private readonly ShaderStage _stage;
    private readonly ulong _hash;
    private readonly bool _scalarDataOnDevice;
    private readonly DeviceRecordCountPlan? _deviceRecordCounts;
    private readonly List<ResourceTableRead> _reads = [];
    private readonly List<ScalarValue> _dynamicReads = [];
    private readonly HashSet<ScalarValue> _dynamicReadSet = [];
    private readonly Dictionary<int, List<int>> _slotsByHash = [];
    private readonly List<(ScalarValue Read, uint Slot)> _patches = [];
    private readonly Dictionary<int, uint> _flattenedSlotByMemoryIndex = [];
    private readonly List<ScalarValue> _visiting = [];
    private readonly HashSet<ScalarValue> _visited = [];
    private readonly Dictionary<ScalarValue, ScalarValue> _replacements = [];

    private ResourceTableReadPlanner(ScalarValueGraph graph, ShaderStage stage, ulong hash, bool scalarDataOnDevice,
        DeviceRecordCountPlan? deviceRecordCounts)
    {
        _graph = graph;
        _stage = stage;
        _hash = hash;
        _deviceRecordCounts = deviceRecordCounts;
        _scalarDataOnDevice = scalarDataOnDevice && stage == ShaderStage.Compute && graph.CallArgumentRegisters == 0 &&
            !Gen5ShaderCallLinker.IsLinked(graph.Program) &&
            !graph.Program.Instructions.Any(instruction => instruction.Opcode is "SSetpcB64" or "SSwappcB64" or "SRfeB64");
    }

    public sealed record Result(
        IReadOnlyList<ResourceTableRead> Reads,
        IReadOnlyList<ScalarValue> DynamicReads,
        IReadOnlyDictionary<ScalarValue, ScalarValue> Replacements,
        IReadOnlyDictionary<int, uint> FlattenedSlotByMemoryIndex);

    public static Result Plan(ScalarValueGraph graph, ShaderStage stage, ulong hash, bool scalarDataOnDevice = false,
        DeviceRecordCountPlan? deviceRecordCounts = null) =>
        new ResourceTableReadPlanner(graph, stage, hash, scalarDataOnDevice, deviceRecordCounts).Run();

    private Result Run()
    {
        foreach (var value in _graph.Values)
        {
            if (value.Kind is ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord &&
                value.MemoryIndex < _graph.Memory.Count)
            {
                var kind = _graph.Memory[value.MemoryIndex].Kind;
                var crosswired = (value.Kind == ScalarValueKind.ScalarAddressWord && kind == MemoryResourceKind.ScalarBuffer) ||
                    (value.Kind == ScalarValueKind.ScalarBufferWord && kind == MemoryResourceKind.ScalarAddress);
                if (crosswired)
                {
                    Fail(_graph.Memory[value.MemoryIndex].Pc, $"{value.Kind} has incompatible scalar memory metadata");
                }
            }
        }

        foreach (var access in _graph.Accesses)
        {
            if (access?.Handle is { } handle)
            {
                for (var component = 0; component < handle.Operands.Length; component++)
                {
                    if (component != 2 || _deviceRecordCounts?.SkipCount(handle) != true)
                        Collect(handle.Operands[component], 0);
                }
            }

            if (access?.SamplerHandle is { } sampler)
            {
                foreach (var dword in sampler.Operands)
                {
                    Collect(dword, 0);
                }
            }
        }

        var validatorReadCount = _reads.Count;
        var hostValues = _scalarDataOnDevice ? HostConsumerValues() : null;
        foreach (var access in _graph.Accesses)
        {
            if (access?.Read is { Kind: ScalarValueKind.ScalarAddressWord } read &&
                IsRawRead(read) && read.Operands[1].IsConstant &&
                new RuntimeValueValidator(_graph, _graph.UserDataBase, _graph.UserDataCount, validatorReadCount).Validate(read))
            {
                // Leave data-only reads in the shader. The existing scalar-address lowering and bounded
                // DeviceAddressRangePlanner path own residency and producer visibility for these exact bytes.
                if (hostValues is not null && CanReadOnDevice(read, hostValues)) continue;
                Collect(read, _graph.Memory[read.MemoryIndex].Pc);
            }
        }

        PatchReads();
        return new Result(_reads, _dynamicReads, _replacements, _flattenedSlotByMemoryIndex);
    }

    private HashSet<ScalarValue> HostConsumerValues()
    {
        var values = new HashSet<ScalarValue>(_visited); // All descriptor/address/sampler ancestors already collected.
        void Visit(ScalarValue? value)
        {
            if (value is null || !values.Add(value)) return;
            foreach (var operand in value.Operands) Visit(operand);
        }
        foreach (var access in _graph.Accesses)
        {
            // Range bounds and resource activation can be evaluated by the host too.
            Visit(access?.Offset);
            Visit(access?.Active);
        }
        foreach (var condition in _graph.BranchConditions.Values) Visit(condition);
        foreach (var argument in _graph.CallRecordArguments.Values)
        {
            Visit(argument.Low);
            Visit(argument.High);
        }
        return values;
    }

    private bool CanReadOnDevice(ScalarValue read, HashSet<ScalarValue> hostValues)
    {
        if (hostValues.Contains(read)) return false;
        var memory = _graph.Memory[read.MemoryIndex];
        if (memory.Kind != MemoryResourceKind.ScalarAddress || memory.Access != MemoryAccess.Read ||
            memory.DataBits != 32 || memory.DataDwords != 1 ||
            read.Operands[1].ConstantU32 != 0 ||
            memory.Offset > DeviceAddressRangePlanner.MaxRangeBytes - sizeof(uint) ||
            read.Operands[0] is not { Kind: ScalarValueKind.AddressHandle, Operands.Length: 2 } handle)
            return false;
        // A pointer loaded from memory would reintroduce host reads to plan residency. Admit only an address
        // computed entirely from immutable dispatch inputs; no first-lane, loop, memory, or descriptor chain.
        bool MemoryFree(ScalarValue value, HashSet<ScalarValue> visiting)
        {
            if (value.IsConstant || value.Kind is ScalarValueKind.UserData or ScalarValueKind.ShaderBase) return true;
            if (value.Kind != ScalarValueKind.Operation || !RuntimeValueValidator.IsUniformOperation(value.Operation) ||
                !visiting.Add(value)) return false;
            var valid = value.Operands.All(operand => MemoryFree(operand, visiting));
            visiting.Remove(value);
            return valid;
        }
        return handle.Operands.All(value => MemoryFree(value, []));
    }

    private void Fail(uint pc, string message) =>
        throw new ResourcePlanException(
            $"shader resource-table planning failed: hash=0x{_hash:X16} stage={_stage} pc=0x{pc:X8} {message}");

    private bool IsRawRead(ScalarValue value) =>
        new RuntimeValueValidator(_graph, _graph.UserDataBase, _graph.UserDataCount, 0).IsRawRead(value);

    private void Collect(ScalarValue value, uint usePc)
    {
        if (value.IsConstant)
        {
            return;
        }

        var cycle = _visiting.IndexOf(value);
        if (cycle >= 0)
        {
            if (_visiting.Skip(cycle).Any(pending => pending.Kind == ScalarValueKind.Phi))
            {
                return;
            }

            Fail(usePc, $"cyclic planning value {value.Kind} without a phi");
        }

        if (!_visited.Add(value))
        {
            return;
        }

        _visiting.Add(value);
        foreach (var operand in value.Operands)
        {
            Collect(operand, usePc);
        }

        _visiting.RemoveAt(_visiting.Count - 1);
        if (!IsRawRead(value))
        {
            return;
        }

        var offset = value.Operands[1];
        if (!offset.IsConstant)
        {
            if (_dynamicReadSet.Add(value))
            {
                _dynamicReads.Add(value);
            }

            return;
        }

        // A read whose address depends on values the host cannot know (a loop over data the
        // shader loaded itself) cannot be flattened; the shader performs it on the device.
        if (!new RuntimeValueValidator(_graph, _graph.UserDataBase, _graph.UserDataCount, 0).Validate(value))
        {
            return;
        }

        var hash = _graph.StructuralHash(value);
        if (!_slotsByHash.TryGetValue(hash, out var slots))
        {
            slots = [];
            _slotsByHash.Add(hash, slots);
        }

        foreach (var slot in slots)
        {
            if (_graph.Equivalent(value, _reads[slot].Value))
            {
                _patches.Add((value, (uint)slot));
                return;
            }
        }

        var newSlot = (uint)_reads.Count;
        slots.Add((int)newSlot);
        _reads.Add(new ResourceTableRead(value, newSlot));
        _patches.Add((value, newSlot));
    }

    // Equivalent reads share one host read, but each instruction must load its destination
    // from that slot instead of reading guest memory again on the device.
    private void PatchReads()
    {
        foreach (var (read, slot) in _patches)
        {
            _replacements[read] = _graph.ResourceTableWord(slot);
            if (read.MemoryIndex < _graph.Memory.Count)
            {
                _graph.Memory[read.MemoryIndex].PlanningOnly = true;
                _flattenedSlotByMemoryIndex.Add(read.MemoryIndex, slot);
            }
        }
    }
}

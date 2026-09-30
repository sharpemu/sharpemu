// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Resources;

// One address handle's planned range: its base value and the signed byte span its
// accesses reach. A handle with a run-time offset is unbounded up to the cap.
public sealed record DeviceAddressRangePlan(
    uint Handle,
    ScalarValue BaseLow,
    ScalarValue BaseHigh,
    IReadOnlyList<int> MemoryIndices,
    bool Written,
    bool Bounded,
    long FirstByte,
    long EndByte,
    bool Plannable)
{
    public ulong Extent => Bounded && EndByte > FirstByte ? (ulong)(EndByte - FirstByte) : 0;
}

// Bounds every device-address handle after tracking. The pass is optional: an
// unplannable handle never fails the plan, it is reported for the hosts to decide.
public static class DeviceAddressRangePlanner
{
    public const ulong MaxRangeBytes = 16 * 1024 * 1024;
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    public static IReadOnlyList<DeviceAddressRangePlan> Plan(ShaderResourcePlan plan)
    {
        var ranges = new List<DeviceAddressRangePlan>();
        var handles = new List<ScalarValue>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.Kind is not (MemoryResourceKind.Flat or MemoryResourceKind.Global or MemoryResourceKind.ScalarAddress) || memory.PlanningOnly)
            {
                continue;
            }

            if (plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.AddressHandle } handle || handle.Operands.Length != 2)
            {
                continue;
            }

            if (memory.Kind == MemoryResourceKind.Flat && memory.Offset == 0 &&
                (memory.Opcode.StartsWith("FlatLoadDword", StringComparison.Ordinal) ||
                 memory.Opcode.StartsWith("FlatStoreDword", StringComparison.Ordinal)))
            {
                memory.AddressSpace = ClassifyFlatAddress(plan, handle.Operands[1], plan.Accesses[index]!.Active);
                if (memory.AddressSpace != FlatAddressSpace.Global ||
                    ExcludesGlobalAddress(plan, memory.Pc, handle.Operands[1]))
                {
                    continue;
                }
            }

            var slot = handles.FindIndex(existing => plan.Graph.Equivalent(existing, handle));
            if (slot < 0)
            {
                slot = handles.Count;
                handles.Add(handle);
                ranges.Add(new DeviceAddressRangePlan((uint)slot, handle.Operands[0], handle.Operands[1], [], false, true, long.MaxValue, long.MinValue, false));
            }

            // The immediate offset is signed; the span keeps the lowest and highest byte.
            var written = memory.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            var bytes = Math.Max((memory.DataBits + 7) / 8, 1u) * Math.Max(memory.DataDwords, 1u);
            var firstByte = (long)unchecked((int)memory.Offset);
            ranges[slot] = ranges[slot] with
            {
                MemoryIndices = [.. ranges[slot].MemoryIndices, index],
                Written = ranges[slot].Written || written,
                Bounded = ranges[slot].Bounded && IsBoundedOffset(plan, index),
                FirstByte = Math.Min(ranges[slot].FirstByte, firstByte),
                EndByte = Math.Max(ranges[slot].EndByte, firstByte + bytes),
            };
        }

        for (var slot = 0; slot < ranges.Count; slot++)
        {
            ranges[slot] = ranges[slot] with
            {
                Plannable = plan.ValidateRuntimeValue(ranges[slot].BaseLow) && plan.ValidateRuntimeValue(ranges[slot].BaseHigh),
            };
        }

        return ranges;
    }

    private static FlatAddressSpace ClassifyFlatAddress(ShaderResourcePlan plan, ScalarValue high, ScalarValue? active)
    {
        var range = UnsignedRange(plan, high, active, []);
        var (low, highBound) = IsEmpty(range) ? (0u, uint.MaxValue) : range;
        var lowShared = Gen5InlineConstants.IsSharedApertureHigh(low);
        var lowPrivate = Gen5InlineConstants.IsPrivateApertureHigh(low);
        var highShared = Gen5InlineConstants.IsSharedApertureHigh(highBound);
        var highPrivate = Gen5InlineConstants.IsPrivateApertureHigh(highBound);
        // This interval excludes global memory. Execution still checks the exact tag.
        if ((lowShared || lowPrivate) && (highShared || highPrivate))
        {
            return (lowShared || highShared, lowPrivate || highPrivate) switch
            {
                (true, true) => FlatAddressSpace.SharedOrPrivate,
                (true, false) => FlatAddressSpace.Shared,
                _ => FlatAddressSpace.Private,
            };
        }

        return ClassifyLocalAlternatives(plan.Graph, high, []);
    }

    // Unknown paths retain the full range. Only proven bounds can exclude global memory.
    private static readonly (uint Low, uint High) Empty = (uint.MaxValue, 0u);

    private static bool IsEmpty((uint Low, uint High) range) => range.Low > range.High;

    private static (uint Low, uint High) Union((uint Low, uint High) left, (uint Low, uint High) right) =>
        IsEmpty(left) ? right : IsEmpty(right) ? left : (Math.Min(left.Low, right.Low), Math.Max(left.High, right.High));

    private static (uint Low, uint High) UnsignedRange(ShaderResourcePlan plan, ScalarValue value, ScalarValue? active,
        HashSet<ScalarValue> visiting)
    {
        if (value.Kind == ScalarValueKind.Constant && value.Type == ScalarValueType.U32)
            return (value.ConstantU32, value.ConstantU32);
        if (value.Kind == ScalarValueKind.MemoryAperture)
        {
            var aperture = (uint)(Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32);
            return (aperture, aperture);
        }

        if (!visiting.Add(value))
            return (0, uint.MaxValue);
        try
        {
            switch (value.Kind)
            {
                case ScalarValueKind.Select when value.Operands.Length == 3 &&
                    active is not null && (ReferenceEquals(value.Operands[0], active) || plan.Graph.Equivalent(value.Operands[0], active)):
                    return UnsignedRange(plan, value.Operands[1], active, visiting);
                case ScalarValueKind.Select when value.Operands.Length == 3:
                    return Union(UnsignedRange(plan, value.Operands[1], active, visiting),
                        UnsignedRange(plan, value.Operands[2], active, visiting));
                case ScalarValueKind.Phi:
                {
                    var range = Empty;
                    foreach (var operand in value.Operands)
                        range = Union(range, UnsignedRange(plan, operand, active, visiting));
                    return range;
                }
                case ScalarValueKind.Operation when value.Operation is ScalarOperation.UMin32 or ScalarOperation.UMax32:
                {
                    var left = UnsignedRange(plan, value.Operands[0], active, visiting);
                    var right = UnsignedRange(plan, value.Operands[1], active, visiting);
                    if (IsEmpty(left) || IsEmpty(right))
                        return (0, uint.MaxValue);
                    return value.Operation == ScalarOperation.UMin32
                        ? (Math.Min(left.Low, right.Low), Math.Min(left.High, right.High))
                        : (Math.Max(left.Low, right.Low), Math.Max(left.High, right.High));
                }
                default:
                    return (0, uint.MaxValue);
            }
        }
        finally
        {
            visiting.Remove(value);
        }
    }

    private static bool ExcludesGlobalAddress(ShaderResourcePlan plan, uint pc, ScalarValue high)
    {
        var activeHigh = ActiveAddressHigh(plan, pc, high, out var minimumHigh);
        return minimumHigh >= 0x10000 || IsLocalAperture(plan.Graph, activeHigh, new HashSet<ScalarValue>());
    }

    private static ScalarValue ActiveAddressHigh(ShaderResourcePlan plan, uint pc, ScalarValue high, out uint minimumHigh)
    {
        minimumHigh = 0;
        var instructions = plan.Graph.Program.Instructions;
        var index = -1;
        for (var position = 0; position < instructions.Count; position++)
            if (instructions[position].Pc == pc) { index = position; break; }
        if (index < 0 || instructions[index].Control is not Gen5GlobalMemoryControl control) return high;
        var block = plan.Graph.ControlFlow.Blocks.First(block => pc >= block.StartPc && pc < block.EndPc);
        for (var position = index - 1; position >= 0 && instructions[position].Pc >= block.StartPc; position--)
        {
            var instruction = instructions[position];
            if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal) ||
                instruction.Destinations.Any(destination => destination.Kind == Gen5OperandKind.ScalarRegister && destination.Value >= 126)) break;
            if (instruction.Encoding is not (Gen5ShaderEncoding.Vop1 or Gen5ShaderEncoding.Vop2 or
                Gen5ShaderEncoding.Vop3 or Gen5ShaderEncoding.Vop3p) && instruction.Opcode is not ("SWaitcnt" or "SNop")) break;
            if (instruction.Destinations.Any(destination => destination.Kind == Gen5OperandKind.VectorRegister &&
                destination.Value == control.VectorAddress + 1))
            {
                // The address write and memory access use the same EXEC mask in this block.
                if (plan.Graph.UnsignedMedianSources.TryGetValue(instruction.Pc, out var sources))
                {
                    var constants = new List<uint>();
                    foreach (var source in sources)
                        if (TryConstant(plan.Graph, source, new HashSet<ScalarValue>(), out var constant))
                            constants.Add((uint)constant);
                    // Two constant operands bound an unsigned median from below.
                    if (constants.Count >= 2) minimumHigh = constants.Min();
                }
                return plan.Graph.ConditionalMaskResults.TryGetValue(instruction.Pc, out var result) ? result : high;
            }
        }
        return high;
    }

    private static bool IsLocalAperture(ScalarValueGraph graph, ScalarValue value, HashSet<ScalarValue> visiting) =>
        ClassifyLocalAlternatives(graph, value, visiting) != FlatAddressSpace.Global;

    private static FlatAddressSpace ClassifyLocalAlternatives(
        ScalarValueGraph graph, ScalarValue value, HashSet<ScalarValue> visiting)
    {
        value = graph.ResolveInvariantPhi(value) ?? value;
        if (TryConstant(graph, value, [], out var constant))
        {
            if (constant == Gen5InlineConstants.SharedApertureBase >> 32 ||
                constant == Gen5InlineConstants.SharedFlatApertureBase >> 32)
                return FlatAddressSpace.Shared;
            if (constant == Gen5InlineConstants.PrivateApertureBase >> 32 ||
                constant == Gen5InlineConstants.PrivateFlatApertureBase >> 32)
                return FlatAddressSpace.Private;
            return FlatAddressSpace.Global;
        }

        if (!visiting.Add(value)) return FlatAddressSpace.Global;
        try
        {
            var alternatives = value.Kind == ScalarValueKind.Select ? value.Operands.Skip(1) :
                value.Kind == ScalarValueKind.Phi ? value.Operands.AsEnumerable() : [];
            FlatAddressSpace? result = null;
            foreach (var alternative in alternatives)
            {
                var classification = ClassifyLocalAlternatives(graph, alternative, visiting);
                if (classification == FlatAddressSpace.Global) return FlatAddressSpace.Global;
                result = result is null || result == classification
                    ? classification : FlatAddressSpace.SharedOrPrivate;
            }
            return result ?? FlatAddressSpace.Global;
        }
        finally
        {
            visiting.Remove(value);
        }
    }

    private static bool TryConstant(ScalarValueGraph graph, ScalarValue value, HashSet<ScalarValue> visiting, out ulong result)
    {
        value = graph.ResolveInvariantPhi(value) ?? value;
        result = value.Payload;
        if (value.Kind == ScalarValueKind.MemoryAperture)
        {
            result = Gen5InlineConstants.DecodeAperture64((uint)value.Payload) >> 32;
            return true;
        }
        if (value.IsConstant) return true;
        if (value.Kind == ScalarValueKind.Phi && visiting.Add(value))
        {
            var pending = new Stack<ScalarValue>();
            var seen = new HashSet<ScalarValue>();
            pending.Push(value);
            ulong? candidate = null;
            var validPhi = true;
            while (pending.TryPop(out var current))
            {
                if (!seen.Add(current)) continue;
                if (current.Kind == ScalarValueKind.Phi)
                {
                    if (current.Operands.Length == 0) validPhi = false;
                    foreach (var incoming in current.Operands) pending.Push(incoming);
                }
                else if (!TryConstant(graph, current, visiting, out var incomingValue) ||
                    candidate.HasValue && candidate.Value != incomingValue) validPhi = false;
                else candidate = incomingValue;
            }
            visiting.Remove(value);
            result = candidate ?? 0;
            return validPhi && candidate.HasValue;
        }
        if (value.Kind != ScalarValueKind.Operation || !visiting.Add(value)) return false;
        var operands = new ulong[value.Operands.Length];
        var valid = true;
        for (var index = 0; index < operands.Length; index++)
            valid &= TryConstant(graph, value.Operands[index], visiting, out operands[index]);
        visiting.Remove(value);
        if (!valid || !ScalarOperationSemantics.TryEvaluate(value.Operation, operands, out result)) return false;
        if (value.Type == ScalarValueType.U32) result = (uint)result;
        return true;
    }

    // An access is bounded when its only run-time term is the record's immediate offset:
    // a scalar read, or a global access whose vector offset is a constant zero.
    private static bool IsBoundedOffset(ShaderResourcePlan plan, int memoryIndex)
    {
        var memory = plan.Memory[memoryIndex];
        if (memory.Kind == MemoryResourceKind.ScalarAddress)
        {
            var read = plan.Accesses[memoryIndex]?.Read;
            return read is not null && read.Operands[1].IsConstant;
        }

        var offset = plan.Accesses[memoryIndex]?.Offset;
        return offset is { IsConstant: true } && offset.ConstantU32 == 0;
    }

    // Evaluates the planned ranges for one draw. A bounded range spans its lowest byte to
    // its extent; an unbounded one starts at the lowest immediate and takes the cap.
    public static DeviceAddressRange[] Evaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var ranges = plan.DeviceAddressRanges;
        var result = new DeviceAddressRange[ranges.Count];
        using var scratch = RuntimeEvaluationScratch.Rent();
        var evaluator = new RuntimeValueEvaluator(scratch, plan, inputs);
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            uint low = 0;
            uint high = 0;
            var planned = range.Plannable && evaluator.Evaluate(range.BaseLow, out low) && evaluator.Evaluate(range.BaseHigh, out high);
            var handleBase = (low | ((ulong)high << 32)) & AddressMask;
            var firstByte = range.Bounded ? range.FirstByte : Math.Min(range.FirstByte, 0);
            var baseAddress = planned ? unchecked(handleBase + (ulong)firstByte) & AddressMask : 0;
            var size = range.Bounded ? range.Extent : MaxRangeBytes;
            result[index] = new DeviceAddressRange(range.Handle, baseAddress, planned ? size : 0, planned, range.Written);
        }

        if (plan.Graph.Program.FunctionBufferAccesses.Count == 0) return result;
        var functionRanges = new List<DeviceAddressRange>(result);
        var seen = new HashSet<(ulong Base, ulong Size, bool Written)>();
        foreach (var memory in plan.Memory.Entries)
        {
            if (!memory.DeviceDescriptor || !plan.Graph.Program.FunctionBufferAccesses.Contains(memory.Pc)) continue;
            if (!plan.Memory.TryGetIndex(memory.Pc, memory.ComponentIndex, out var memoryIndex) ||
                plan.Accesses[memoryIndex]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } handle)
                throw new ResourcePlanException("The shader function buffer has no descriptor.");
            var words = new uint[4];
            for (var component = 0; component < 4; component++)
                if (!evaluator.Evaluate(handle.Operands[component], out words[component]))
                    throw new ResourcePlanException("The shader function buffer range cannot be resolved.");
            var address = words[0] | ((ulong)(words[1] & 0xFFFF) << 32);
            if ((words[1] & 0x80000000) != 0)
                throw new ResourcePlanException("The shader function uses an unsupported swizzled buffer.");
            var stride = (words[1] >> 16) & 0x3FFF;
            var size = (ulong)words[2] * Math.Max(stride, 1u);
            var written = memory.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            if (size == 0) continue;
            if (size > AddressMask - address + 1)
                throw new ResourcePlanException("The shader function buffer range wraps the guest address space.");
            if (seen.Add((address, size, written)))
                functionRanges.Add(new((uint)functionRanges.Count, address, size, true, written));
        }
        return functionRanges.ToArray();
    }
}

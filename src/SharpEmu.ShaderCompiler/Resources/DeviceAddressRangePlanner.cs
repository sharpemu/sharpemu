// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using SharpEmu.HLE.Diagnostics;

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
    bool Plannable,
    DeviceAddressUnboundedReason UnboundedReason = DeviceAddressUnboundedReason.None)
{
    public ulong Extent => Bounded && EndByte > FirstByte ? (ulong)(EndByte - FirstByte) : 0;
    // Scalar dword loads mask the effective address down to a dword in the emitter.
    public bool AlignScalarReads { get; init; }
}

public enum DeviceAddressUnboundedReason
{
    None,
    ScalarRuntimeOffset,
    VectorOffset,
    ConstantOffset,
    UserDataOffset,
    LoadedOffset,
    ComputedOffset,
    OtherOffset,
    Count,
}

// Bounds every device-address handle after tracking. The pass is optional: an
// unplannable handle never fails the plan, it is reported for the hosts to decide.
public static class DeviceAddressRangePlanner
{
    public const ulong MaxRangeBytes = 16 * 1024 * 1024;
    private const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    private static readonly ThreadCounter BoundedEvaluations = new();
    private static readonly ThreadCounter[] UnboundedEvaluations =
        Enumerable.Range(0, (int)DeviceAddressUnboundedReason.Count).Select(_ => new ThreadCounter()).ToArray();

    public static string TakeUnboundedReport()
    {
        var parts = new List<string> { FormattableString.Invariant($"bounded={BoundedEvaluations.Take()}") };
        for (var reason = 1; reason < (int)DeviceAddressUnboundedReason.Count; reason++)
        {
            parts.Add(FormattableString.Invariant($"{(DeviceAddressUnboundedReason)reason}={UnboundedEvaluations[reason].Take()}"));
        }

        return string.Join(" ", parts);
    }

    public static IReadOnlyList<DeviceAddressRangePlan> Plan(ShaderResourcePlan plan) => Plan(plan, out _);

    public static IReadOnlyList<DeviceAddressRangePlan> Plan(ShaderResourcePlan plan, out bool readsBounded) =>
        Plan(plan, out readsBounded, out _, out _);

    // A scalar address read, and a global read with a scalar base, address the handle's 64-bit base plus a zero-extended
    // 32-bit offset (the translator widens it with UConvert) and a signed immediate, so such a handle reaches only
    // [base + first byte, base + 2^32 + end byte). A program is windowed when every range it does not bound exactly is such
    // a handle, read only, with a plannable base. windowBytes is the widest window measured from the host's range base
    // (EvaluateRange: base + min(first byte, 0), dword-aligned for scalar reads), with a pad for that alignment.
    public const ulong WindowOffsetBytes = 1UL << 32;
    private const long WindowPadBytes = 16;

    public static IReadOnlyList<DeviceAddressRangePlan> Plan(ShaderResourcePlan plan, out bool readsBounded, out bool readsWindowed,
        out ulong windowBytes)
    {
        readsBounded = true;
        var windowable = true;
        var windowSafe = new List<bool>();
        var ranges = new List<DeviceAddressRangePlan>();
        var handles = new List<ScalarValue>();
        var slotsByHash = new Dictionary<int, List<int>>();
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            var memory = plan.Memory[index];
            if (memory.DeviceDescriptor || memory.Opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay")
            {
                readsBounded = false;
                windowable = false;
            }

            if (memory.Kind is not (MemoryResourceKind.Flat or MemoryResourceKind.Global or MemoryResourceKind.ScalarAddress))
            {
                continue;
            }

            if (memory.PlanningOnly)
            {
                readsBounded &= memory.Kind == MemoryResourceKind.ScalarAddress;
                windowable &= memory.Kind == MemoryResourceKind.ScalarAddress;
                continue;
            }

            if (plan.Accesses[index]?.Handle is not { Kind: ScalarValueKind.AddressHandle } handle || handle.Operands.Length != 2)
            {
                readsBounded = false;
                windowable = false;
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

            var hash = plan.Graph.StructuralHash(handle);
            if (!slotsByHash.TryGetValue(hash, out var hashSlots))
            {
                hashSlots = [];
                slotsByHash.Add(hash, hashSlots);
            }

            var slot = -1;
            foreach (var existing in hashSlots)
            {
                if (plan.Graph.Equivalent(handles[existing], handle))
                {
                    slot = existing;
                    break;
                }
            }

            if (slot < 0)
            {
                slot = handles.Count;
                hashSlots.Add(slot);
                handles.Add(handle);
                windowSafe.Add(true);
                ranges.Add(new DeviceAddressRangePlan((uint)slot, handle.Operands[0], handle.Operands[1], [], false, true, long.MaxValue, long.MinValue, false)
                    { AlignScalarReads = true });
            }

            // The immediate offset is signed; the span keeps the lowest and highest byte.
            var written = memory.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            windowSafe[slot] = windowSafe[slot] &&
                (memory.Kind == MemoryResourceKind.ScalarAddress || (memory.Kind == MemoryResourceKind.Global && !memory.AddressIsFull));
            readsBounded &= memory.Access == MemoryAccess.Read;
            var bytes = Math.Max((memory.DataBits + 7) / 8, 1u) * Math.Max(memory.DataDwords, 1u);
            var firstByte = (long)unchecked((int)memory.Offset);
            var reason = UnboundedReasonOf(plan, index);
            var spread = 0L;
            if (reason != DeviceAddressUnboundedReason.None && !written && BoundedOffsets && OffsetInterval(plan, index) is { } interval &&
                interval.High - interval.Low + bytes <= (long)MaxRangeBytes)
            {
                reason = DeviceAddressUnboundedReason.None;
                firstByte += interval.Low;
                spread = interval.High - interval.Low;
            }
            ranges[slot] = ranges[slot] with
            {
                MemoryIndices = [.. ranges[slot].MemoryIndices, index],
                Written = ranges[slot].Written || written,
                AlignScalarReads = ranges[slot].AlignScalarReads && memory.Kind == MemoryResourceKind.ScalarAddress && !written,
                Bounded = ranges[slot].Bounded && reason == DeviceAddressUnboundedReason.None,
                UnboundedReason = ranges[slot].UnboundedReason != DeviceAddressUnboundedReason.None ? ranges[slot].UnboundedReason : reason,
                FirstByte = Math.Min(ranges[slot].FirstByte, firstByte),
                EndByte = Math.Max(ranges[slot].EndByte, firstByte + spread + bytes),
            };
        }

        var anyWindowed = false;
        long margin = 0;
        for (var slot = 0; slot < ranges.Count; slot++)
        {
            ranges[slot] = ranges[slot] with
            {
                Plannable = plan.ValidateRuntimeValue(ranges[slot].BaseLow) && plan.ValidateRuntimeValue(ranges[slot].BaseHigh),
            };
            var range = ranges[slot];
            var exact = range.Bounded && range.Plannable && !range.Written && range.Extent <= MaxRangeBytes;
            readsBounded &= exact;
            if (exact)
            {
                continue;
            }

            if (!range.Bounded && range.Plannable && !range.Written && windowSafe[slot] && range.EndByte >= range.FirstByte)
            {
                anyWindowed = true;
                margin = Math.Max(margin, range.EndByte - Math.Min(range.FirstByte, 0));
                continue;
            }

            windowable = false;
        }

        var plainProgram = ranges.Count != 0 && plan.DeviceRecordCounts is null && plan.Graph.Program.FunctionBufferAccesses.Count == 0;
        readsBounded &= plainProgram;
        readsWindowed = windowable && anyWindowed && plainProgram;
        windowBytes = readsWindowed ? WindowOffsetBytes + (ulong)(margin + WindowPadBytes) : 0;
        return ranges;
    }

    private static readonly bool BoundedOffsets = Environment.GetEnvironmentVariable("SHARPEMU_BOUNDED_BDA") != "0";

    private static readonly (uint Low, uint High) FullRange = (0u, uint.MaxValue);

    private static (long Low, long High)? OffsetInterval(ShaderResourcePlan plan, int memoryIndex)
    {
        var memory = plan.Memory[memoryIndex];
        var access = plan.Accesses[memoryIndex];
        ScalarValue? value;
        ScalarValue? predicate;
        if (memory.Kind == MemoryResourceKind.ScalarAddress)
        {
            value = access?.Read is { Operands.Length: >= 2 } read ? read.Operands[1] : null;
            predicate = null;
        }
        else
        {
            value = access?.Offset;
            predicate = access?.Active;
        }

        if (value is null || value.IsUndefined)
        {
            return null;
        }

        var budget = 4096;
        var (low, high) = BoundValue(plan, value, predicate, ref budget, 0);
        return low == FullRange.Low && high == FullRange.High ? null : (low, high);
    }

    private static bool SameValue(ShaderResourcePlan plan, ScalarValue left, ScalarValue right) =>
        ReferenceEquals(left, right) || plan.Graph.Equivalent(left, right);

    private static bool Implies(ShaderResourcePlan plan, ScalarValue predicate, ScalarValue condition, ref int budget, int depth)
    {
        if (budget <= 0)
        {
            return false;
        }

        budget--;
        if (SameValue(plan, predicate, condition))
        {
            return true;
        }

        if (depth >= 12 || predicate.Kind != ScalarValueKind.Operation || predicate.Operation != ScalarOperation.LogicalAnd ||
            predicate.Operands.Length != 2)
        {
            return false;
        }

        return Implies(plan, predicate.Operands[0], condition, ref budget, depth + 1) ||
            Implies(plan, predicate.Operands[1], condition, ref budget, depth + 1);
    }

    private static uint PredicateUpper(ShaderResourcePlan plan, ScalarValue value, ScalarValue? predicate, ref int budget, int depth)
    {
        if (predicate is null || budget <= 0 || depth >= 12 || predicate.Kind != ScalarValueKind.Operation || predicate.Operands.Length != 2)
        {
            return uint.MaxValue;
        }

        budget--;
        var left = predicate.Operands[0];
        var right = predicate.Operands[1];
        switch (predicate.Operation)
        {
            case ScalarOperation.LogicalAnd:
                return Math.Min(PredicateUpper(plan, value, left, ref budget, depth + 1), PredicateUpper(plan, value, right, ref budget, depth + 1));
            case ScalarOperation.ULessThan32 when right.IsConstant && right.ConstantU32 != 0 && SameValue(plan, left, value):
                return right.ConstantU32 - 1;
            case ScalarOperation.UGreaterThan32 when left.IsConstant && left.ConstantU32 != 0 && SameValue(plan, right, value):
                return left.ConstantU32 - 1;
            case ScalarOperation.ULessThanEqual32 when right.IsConstant && SameValue(plan, left, value):
                return right.ConstantU32;
            case ScalarOperation.UGreaterThanEqual32 when left.IsConstant && SameValue(plan, right, value):
                return left.ConstantU32;
            default:
                return uint.MaxValue;
        }
    }

    private static (uint Low, uint High) BoundValue(ShaderResourcePlan plan, ScalarValue value, ScalarValue? predicate, ref int budget, int depth)
    {
        if (budget <= 0)
        {
            return FullRange;
        }

        budget--;
        value = plan.Graph.ResolveInvariantPhi(value) ?? value;
        if (value.IsConstant && value.Type == ScalarValueType.U32)
        {
            return (value.ConstantU32, value.ConstantU32);
        }

        var result = FullRange;
        if (depth < 24)
        {
            var operands = value.Operands;
            switch (value.Kind)
            {
                case ScalarValueKind.Operation when operands.Length >= 2:
                    switch (value.Operation)
                    {
                        case ScalarOperation.And32:
                        {
                            var left = BoundValue(plan, operands[0], predicate, ref budget, depth + 1);
                            var right = BoundValue(plan, operands[1], predicate, ref budget, depth + 1);
                            result = (0, Math.Min(left.High, right.High));
                            break;
                        }
                        case ScalarOperation.UMin32:
                        {
                            var left = BoundValue(plan, operands[0], predicate, ref budget, depth + 1);
                            var right = BoundValue(plan, operands[1], predicate, ref budget, depth + 1);
                            result = (Math.Min(left.Low, right.Low), Math.Min(left.High, right.High));
                            break;
                        }
                        case ScalarOperation.IAdd32:
                        case ScalarOperation.IMul32:
                        {
                            var left = BoundValue(plan, operands[0], predicate, ref budget, depth + 1);
                            var right = BoundValue(plan, operands[1], predicate, ref budget, depth + 1);
                            var add = value.Operation == ScalarOperation.IAdd32;
                            var high = add ? (ulong)left.High + right.High : (ulong)left.High * right.High;
                            if (high <= uint.MaxValue)
                            {
                                result = (add ? left.Low + right.Low : left.Low * right.Low, (uint)high);
                            }

                            break;
                        }
                        case ScalarOperation.ShiftLeft32 when operands[1].IsConstant && operands[1].ConstantU32 < 32:
                        {
                            var shift = (int)operands[1].ConstantU32;
                            var source = BoundValue(plan, operands[0], predicate, ref budget, depth + 1);
                            if (((ulong)source.High << shift) <= uint.MaxValue)
                            {
                                result = (source.Low << shift, source.High << shift);
                            }

                            break;
                        }
                        case ScalarOperation.ShiftRightLogical32 when operands[1].IsConstant && operands[1].ConstantU32 < 32:
                        {
                            var shift = (int)operands[1].ConstantU32;
                            var source = BoundValue(plan, operands[0], predicate, ref budget, depth + 1);
                            result = (source.Low >> shift, source.High >> shift);
                            break;
                        }
                        case ScalarOperation.BitFieldUExtract when operands.Length == 3 && operands[1].IsConstant && operands[2].IsConstant &&
                            operands[2].ConstantU32 < 32 && operands[1].ConstantU32 <= 32 - operands[2].ConstantU32:
                            result = (0, (1u << (int)operands[2].ConstantU32) - 1);
                            break;
                    }

                    break;
                case ScalarValueKind.Select when operands.Length == 3:
                    if (predicate is not null && Implies(plan, predicate, operands[0], ref budget, 0))
                    {
                        result = BoundValue(plan, operands[1], predicate, ref budget, depth + 1);
                    }
                    else
                    {
                        var whenTrue = BoundValue(plan, operands[1], predicate, ref budget, depth + 1);
                        var whenFalse = BoundValue(plan, operands[2], predicate, ref budget, depth + 1);
                        result = (Math.Min(whenTrue.Low, whenFalse.Low), Math.Max(whenTrue.High, whenFalse.High));
                    }

                    break;
            }
        }

        var upper = PredicateUpper(plan, value, predicate, ref budget, 0);
        if (upper < result.High)
        {
            result.High = upper;
        }

        return result.Low > result.High ? FullRange : result;
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
    private static DeviceAddressUnboundedReason UnboundedReasonOf(ShaderResourcePlan plan, int memoryIndex)
    {
        var memory = plan.Memory[memoryIndex];
        if (memory.Kind == MemoryResourceKind.ScalarAddress)
        {
            var read = plan.Accesses[memoryIndex]?.Read;
            return read is not null && read.Operands[1].IsConstant ? DeviceAddressUnboundedReason.None : DeviceAddressUnboundedReason.ScalarRuntimeOffset;
        }

        return plan.Accesses[memoryIndex]?.Offset switch
        {
            null or { Kind: ScalarValueKind.Undefined } => DeviceAddressUnboundedReason.VectorOffset,
            { IsConstant: true } offset => offset.ConstantU32 == 0 ? DeviceAddressUnboundedReason.None : DeviceAddressUnboundedReason.ConstantOffset,
            { Kind: ScalarValueKind.UserData } => DeviceAddressUnboundedReason.UserDataOffset,
            { Kind: ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord or ScalarValueKind.ResourceTableWord } => DeviceAddressUnboundedReason.LoadedOffset,
            { Kind: ScalarValueKind.Operation or ScalarValueKind.Select or ScalarValueKind.Phi or ScalarValueKind.FirstLane } => DeviceAddressUnboundedReason.ComputedOffset,
            _ => DeviceAddressUnboundedReason.OtherOffset,
        };
    }

    internal const string MissingFunctionDescriptor = "The shader function buffer has no descriptor.";
    internal const string UnresolvedFunctionBuffer = "The shader function buffer range cannot be resolved.";
    internal const string SwizzledFunctionBuffer = "The shader function uses an unsupported swizzled buffer.";
    internal const string WrappingFunctionBuffer = "The shader function buffer range wraps the guest address space.";

    // Evaluates the planned ranges for one draw. A bounded range spans its lowest byte to
    // its extent; an unbounded one starts at the lowest immediate and takes the cap.
    public static DeviceAddressRange[] Evaluate(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        var ranges = CompiledDeviceAddressRanges.Configured == CompiledDeviceAddressRanges.Mode.Off
            ? EvaluateInterpreted(plan, inputs)
            : CompiledDeviceAddressRanges.Serve(plan, inputs);
        return plan.DeviceRecordCounts?.Append(plan, inputs, ranges) ?? ranges;
    }

    // One evaluator for every range and then the function V#s; the reference of CompiledDeviceAddressRanges.
    internal static DeviceAddressRange[] EvaluateInterpreted(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
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
            result[index] = EvaluateRange(range, planned, low, high);
        }

        if (plan.Graph.Program.FunctionBufferAccesses.Count == 0) return result;
        var functionRanges = new List<DeviceAddressRange>(result);
        var seen = new HashSet<(ulong Base, ulong Size, bool Written)>();
        foreach (var memory in plan.Memory.Entries)
        {
            if (!memory.DeviceDescriptor || !plan.Graph.Program.FunctionBufferAccesses.Contains(memory.Pc)) continue;
            if (!plan.Memory.TryGetIndex(memory.Pc, memory.ComponentIndex, out var memoryIndex) ||
                plan.Accesses[memoryIndex]?.Handle is not { Kind: ScalarValueKind.BufferHandle, Operands.Length: 4 } handle)
                throw new ResourcePlanException(MissingFunctionDescriptor);
            var words = new uint[4];
            var resolved = true;
            for (var component = 0; component < 4 && resolved; component++)
                resolved = evaluator.Evaluate(handle.Operands[component], out words[component]);
            if (!resolved)
            {
                // A V# of a callee linked with run-time arguments: the host resolves it for each record (CallRecordCallee).
                if (plan.CallRecordCallees.Count != 0) continue;
                throw new ResourcePlanException(UnresolvedFunctionBuffer);
            }
            var address = words[0] | ((ulong)(words[1] & 0xFFFF) << 32);
            if ((words[1] & 0x80000000) != 0)
                throw new ResourcePlanException(SwizzledFunctionBuffer);
            var stride = (words[1] >> 16) & 0x3FFF;
            var size = (ulong)words[2] * Math.Max(stride, 1u);
            var written = memory.Access is MemoryAccess.Write or MemoryAccess.Atomic;
            if (size == 0) continue;
            if (size > AddressMask - address + 1)
                throw new ResourcePlanException(WrappingFunctionBuffer);
            if (seen.Add((address, size, written)))
                functionRanges.Add(new((uint)functionRanges.Count, address, size, true, written));
        }
        return functionRanges.ToArray();
    }

    // Shared by interpreted and compiled evaluators. Widen a scalar read's residency range down to the exact
    // first dword the GPU can touch; aligned reads retain their original extent (e.g. six bounds words = 24 bytes).
    internal static DeviceAddressRange EvaluateRange(DeviceAddressRangePlan range, bool planned, uint low, uint high)
    {
        var handleBase = (low | ((ulong)high << 32)) & AddressMask;
        var firstByte = range.Bounded ? range.FirstByte : Math.Min(range.FirstByte, 0);
        var baseAddress = planned ? unchecked(handleBase + (ulong)firstByte) & AddressMask : 0;
        var size = range.Bounded ? range.Extent : MaxRangeBytes;
        if (range.Bounded)
        {
            BoundedEvaluations.Increment();
        }
        else
        {
            UnboundedEvaluations[(int)range.UnboundedReason].Increment();
        }

        if (planned && range.AlignScalarReads)
        {
            size += baseAddress & 3;
            baseAddress &= ~3ul;
        }
        return new DeviceAddressRange(range.Handle, baseAddress, planned ? size : 0, planned, range.Written);
    }
}

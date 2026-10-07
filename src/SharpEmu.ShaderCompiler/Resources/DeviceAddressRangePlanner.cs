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

            // A FLAT address built from an aperture base is an LDS or scratch
            // pointer; the backend routes it per lane, so it owns no device range.
            if (memory.Kind == MemoryResourceKind.Flat)
            {
                memory.AddressSpace = ClassifyFlatAddress(plan, handle.Operands[1], plan.Accesses[index]!.Active);
                if (memory.AddressSpace != FlatAddressSpace.Global)
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

    // A FLAT address is local when its high dword provably lies in an aperture.
    // Two shapes occur: the high dword is clamped between constants inside the
    // apertures (v_med3_u32 with the hard-coded PS5 bounds), or it derives from an
    // aperture operand, which shaders often merge into the upper half of a
    // computed dword with SDWA WORD1.
    private static FlatAddressSpace ClassifyFlatAddress(ShaderResourcePlan plan, ScalarValue high, ScalarValue? active)
    {
        var range = UnsignedRange(plan, high, active, []);
        var (low, highBound) = IsEmpty(range) ? (0u, uint.MaxValue) : range;
        var lowShared = Gen5InlineConstants.IsSharedApertureHigh(low);
        var lowPrivate = Gen5InlineConstants.IsPrivateApertureHigh(low);
        var highShared = Gen5InlineConstants.IsSharedApertureHigh(highBound);
        var highPrivate = Gen5InlineConstants.IsPrivateApertureHigh(highBound);
        // The two apertures are adjacent windows, so bounds inside them keep every
        // value in between inside them too.
        if ((lowShared || lowPrivate) && (highShared || highPrivate))
        {
            return (lowShared || highShared, lowPrivate || highPrivate) switch
            {
                (true, true) => FlatAddressSpace.SharedOrPrivate,
                (true, false) => FlatAddressSpace.Shared,
                _ => FlatAddressSpace.Private,
            };
        }

        return ClassifyByApertureOperand(high);
    }

    // Conservative unsigned bounds of a 32-bit value as the access sees it. Phi, Select,
    // UMin and UMax yield one of their inputs, so a cycle adds nothing (Empty); a select
    // on the access's own EXEC mask contributes only its active arm.
    // The mask is the same node when EXEC was not written in between, even when its
    // value is unknown (an undefined node is never structurally equivalent).
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
            return Empty;
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
                        return IsEmpty(left) ? right : left;
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

    private static FlatAddressSpace ClassifyByApertureOperand(ScalarValue high)
    {
        var shared = false;
        var @private = false;
        var pending = new Stack<ScalarValue>();
        var visited = new HashSet<ScalarValue>();
        pending.Push(high);
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value))
            {
                continue;
            }

            if (value.Kind == ScalarValueKind.MemoryAperture)
            {
                if (Gen5InlineConstants.IsSharedAperture((uint)value.Payload))
                    shared = true;
                else
                    @private = true;
                continue;
            }

            foreach (var operand in value.Operands)
                pending.Push(operand);
        }

        return (shared, @private) switch
        {
            (true, true) => FlatAddressSpace.SharedOrPrivate,
            (true, false) => FlatAddressSpace.Shared,
            (false, true) => FlatAddressSpace.Private,
            _ => FlatAddressSpace.Global,
        };
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

        return result;
    }
}

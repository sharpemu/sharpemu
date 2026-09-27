// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// Recognises a runtime V# whose four dwords come from one static scalar buffer (the SRT)
// at a dynamic offset driven by a canonical unsigned induction loop, and proves that the
// loop's guard bounds the descriptors the access can select. The result is a pure
// description of the candidate set; no descriptor is invented and no memory bound is
// promoted into a semantic proof.
internal static class BufferCandidateTablePlanner
{
    public static bool TryPlan(ShaderResourcePlan plan, ScalarValue handle, int memoryIndex, out BufferCandidateTablePlan table)
    {
        table = null!;
        if (handle.Kind != ScalarValueKind.BufferHandle || handle.Operands.Length != 4)
        {
            return false;
        }


        // Every dword must be the same-width scalar-buffer read at a shared dynamic offset,
        // with the component immediates 0,4,8,12 that one dwordx4 read produces.
        ScalarValue? srt = null;
        ScalarValue? dynamicOffset = null;
        for (var word = 0; word < 4; word++)
        {
            var read = handle.Operands[word];
            if (read.Kind != ScalarValueKind.ScalarBufferWord || read.MemoryIndex < 0 || read.MemoryIndex >= plan.Memory.Count)
            {
                return false;
            }

            var memory = plan.Memory[read.MemoryIndex];
            if (memory.Kind != MemoryResourceKind.ScalarBuffer || memory.DataBits != 32 || memory.DataDwords != 1 ||
                memory.Offset != (uint)word * sizeof(uint))
            {
                return false;
            }

            if (srt is null)
            {
                srt = read.Operands[0];
            }
            else if (!plan.Graph.Equivalent(srt, read.Operands[0]))
            {
                return false;
            }

            if (dynamicOffset is null)
            {
                dynamicOffset = read.Operands[1];
            }
            else if (!plan.Graph.Equivalent(dynamicOffset, read.Operands[1]))
            {
                return false;
            }
        }

        // offset = base + index * stride
        var offset = dynamicOffset!;
        var baseOffset = 0u;
        if (offset.Kind == ScalarValueKind.Operation && offset.Operation == ScalarOperation.IAdd32)
        {
            if (offset.Operands[0].IsConstant)
            {
                baseOffset = offset.Operands[0].ConstantU32;
                offset = offset.Operands[1];
            }
            else if (offset.Operands[1].IsConstant)
            {
                baseOffset = offset.Operands[1].ConstantU32;
                offset = offset.Operands[0];
            }
        }

        if (offset.Kind != ScalarValueKind.Operation || offset.Operation != ScalarOperation.IMul32)
        {
            return false;
        }

        ScalarValue index;
        uint stride;
        if (offset.Operands[0].IsConstant)
        {
            stride = offset.Operands[0].ConstantU32;
            index = offset.Operands[1];
        }
        else if (offset.Operands[1].IsConstant)
        {
            stride = offset.Operands[1].ConstantU32;
            index = offset.Operands[0];
        }
        else
        {
            return false;
        }

        if (stride < BufferCandidateTablePlan.DescriptorByteSize)
        {
            return false;
        }

        if (!TryProveInductionBound(plan, index, out var initial, out var step, out var limit, out var inclusive) || limit is null)
        {
            return false;
        }

        if (!initial.IsConstant)
        {
            return false;
        }

        var srtExtent = SrtByteExtent(plan, srt!);
        var minOffset = unchecked(baseOffset + initial.ConstantU32 * stride);
        var candidate = new BufferCandidateTablePlan
        {
            SrtHandle = srt!,
            OffsetExpression = dynamicOffset!,
            IndexExpression = index,
            Initial = initial,
            Step = step,
            Limit = limit.IsConstant ? null : limit,
            LimitInclusive = inclusive,
            Stride = stride,
            BaseOffset = baseOffset,
            MinOffset = minOffset,
            SrtByteExtent = srtExtent,
            MemoryIndices = [memoryIndex],
        };

        int count;
        uint maxOffset;
        if (limit.IsConstant)
        {
            if (!candidate.TryResolveCount(limit.ConstantU32, out count))
            {
                return false;
            }

            if (count <= 0 || count > candidate.Cap)
            {
                return false;
            }

            maxOffset = unchecked(candidate.StaticCandidateOffset(count - 1) + BufferCandidateTablePlan.DescriptorByteSize);
        }
        else
        {
            if (!plan.ValidateRuntimeValue(limit))
            {
                return false;
            }

            // A run-time limit is capped and memory-checked at materialisation; the static
            // start of the range still has to stay inside the declared SRT extent.
            count = -1;
            maxOffset = uint.MaxValue;
        }

        if (maxOffset < minOffset || (maxOffset != uint.MaxValue && maxOffset > srtExtent))
        {
            return false;
        }

        table = new BufferCandidateTablePlan
        {
            SrtHandle = srt!,
            OffsetExpression = dynamicOffset!,
            IndexExpression = index,
            Initial = initial,
            Step = step,
            Limit = limit.IsConstant ? null : limit,
            LimitInclusive = inclusive,
            Stride = stride,
            BaseOffset = baseOffset,
            MinOffset = minOffset,
            MaxOffset = maxOffset,
            Count = count,
            SrtByteExtent = srtExtent,
            MemoryIndices = [memoryIndex],
            Cap = candidate.Cap,
        };
        return true;
    }

    // A canonical unsigned induction: the value is a phi with a constant initial and an
    // add-recurrence, and a branch guarded by an unsigned comparison against the phi
    // lives at the loop header or one of its predecessors.
    private static bool TryProveInductionBound(
        ShaderResourcePlan plan,
        ScalarValue index,
        out ScalarValue initial,
        out uint step,
        out ScalarValue? limit,
        out bool inclusive)
    {
        initial = null!;
        step = 0;
        limit = null;
        inclusive = false;
        if (index.Kind != ScalarValueKind.Phi)
        {
            return false;
        }

        ScalarValue? found = null;
        var initialCount = 0;
        foreach (var operand in index.Operands)
        {
            if (operand.Kind == ScalarValueKind.Operation && operand.Operation == ScalarOperation.IAdd32)
            {
                if (ReferenceEquals(operand.Operands[0], index) && operand.Operands[1].IsConstant)
                {
                    step = operand.Operands[1].ConstantU32;
                }
                else if (ReferenceEquals(operand.Operands[1], index) && operand.Operands[0].IsConstant)
                {
                    step = operand.Operands[0].ConstantU32;
                }
                else
                {
                    continue;
                }
            }
            else if (!ReferenceEquals(operand, index))
            {
                found = operand;
                initialCount++;
            }
        }

        if (step == 0 || initialCount != 1 || found is null)
        {
            return false;
        }

        initial = found;

        var flow = plan.Graph.ControlFlow;
        var guardBlock = (int?)null;
        foreach (var pair in plan.Graph.BranchConditions)
        {
            var condition = pair.Value;
            if (condition.Kind != ScalarValueKind.Operation)
            {
                continue;
            }

            ScalarValue? bound;
            bool boundInclusive;
            switch (condition.Operation)
            {
                case ScalarOperation.ULessThan32 when MatchesIndex(plan, condition.Operands[0], index, step):
                    bound = condition.Operands[1];
                    boundInclusive = false;
                    break;
                case ScalarOperation.ULessThanEqual32 when MatchesIndex(plan, condition.Operands[0], index, step):
                    bound = condition.Operands[1];
                    boundInclusive = true;
                    break;
                case ScalarOperation.UGreaterThan32 when MatchesIndex(plan, condition.Operands[1], index, step):
                    bound = condition.Operands[0];
                    boundInclusive = false;
                    break;
                case ScalarOperation.UGreaterThanEqual32 when MatchesIndex(plan, condition.Operands[1], index, step):
                    bound = condition.Operands[0];
                    boundInclusive = true;
                    break;
                default:
                    continue;
            }

            if (!bound.IsConstant && !plan.ValidateRuntimeValue(bound))
            {
                continue;
            }

            var block = BlockOf(plan, pair.Key);
            if (block < 0)
            {
                continue;
            }

            // The guard must live at the loop header that owns the phi or one of the
            // blocks feeding it, so it really controls the recurrence.
            var header = index.PhiBlock;
            if (block != header && !flow.Predecessors[header].Contains(block))
            {
                continue;
            }

            if (limit is not null && !plan.Graph.Equivalent(limit, bound))
            {
                return false;
            }

            limit = bound;
            inclusive = boundInclusive;
            guardBlock = block;
        }

        return limit is not null && guardBlock is not null;
    }

    // The guard compares either the induction value itself or its next value; both bound
    // the same executed set, so a one-step add of the phi is accepted.
    private static bool MatchesIndex(ShaderResourcePlan plan, ScalarValue operand, ScalarValue index, uint step)
    {
        if (plan.Graph.Equivalent(operand, index))
        {
            return true;
        }

        if (operand.Kind != ScalarValueKind.Operation || operand.Operation != ScalarOperation.IAdd32)
        {
            return false;
        }

        if (plan.Graph.Equivalent(operand.Operands[0], index) && operand.Operands[1].IsConstant)
        {
            return operand.Operands[1].ConstantU32 == step;
        }

        if (plan.Graph.Equivalent(operand.Operands[1], index) && operand.Operands[0].IsConstant)
        {
            return operand.Operands[0].ConstantU32 == step;
        }

        return false;
    }

    private static int BlockOf(ShaderResourcePlan plan, uint pc)
    {
        var blocks = plan.Graph.ControlFlow.Blocks;
        for (var index = 0; index < blocks.Count; index++)
        {
            if (pc >= blocks[index].StartPc && pc < blocks[index].EndPc)
            {
                return index;
            }
        }

        return -1;
    }

    // The SRT's declared byte size when both its stride and record count are constants.
    // This is a memory-safety bound, never evidence that any entry is a valid descriptor.
    private static uint SrtByteExtent(ShaderResourcePlan plan, ScalarValue srt)
    {
        if (srt.Operands.Length != 4)
        {
            return uint.MaxValue;
        }

        var strideWord = plan.Graph.ResolveInvariantPhi(srt.Operands[1]) ?? srt.Operands[1];
        var recordsWord = plan.Graph.ResolveInvariantPhi(srt.Operands[2]) ?? srt.Operands[2];
        if (!strideWord.IsConstant || !recordsWord.IsConstant)
        {
            return uint.MaxValue;
        }

        var stride = (strideWord.ConstantU32 >> 16) & 0x3FFFu;
        var records = recordsWord.ConstantU32;
        var size = stride == 0 ? records : (ulong)stride * records;
        return size > uint.MaxValue ? uint.MaxValue : (uint)size;
    }
}

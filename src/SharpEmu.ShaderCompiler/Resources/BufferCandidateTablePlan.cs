// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// A runtime V# whose four dwords are read from a static scalar buffer (the SRT) at an
// offset driven by a canonical unsigned induction loop. The loop's guard bounds the set
// of descriptors the access can ever select, so the backend can enumerate that set into
// a flat table of native candidates and dispatch with a run-time selector.
//
// The plan is deliberately a pure analysis result: it names the guest SRT source and the
// offset/index expressions, and records the proven byte range. It does not invent a
// descriptor, and it never treats the safety extent of the SRT as a semantic proof of
// which entries are valid descriptors; that proof is the loop guard, and each candidate
// is decoded and validated again at materialisation.
public sealed class BufferCandidateTablePlan
{
    // A hard, generic ceiling on the number of native candidates one table may bind.
    // Exceeding it is an explicit unsupported case, never a silent truncation.
    public const int DefaultCap = 64;

    public const uint DescriptorByteSize = 16;

    // The descriptor source of the static guest SRT buffer the V# dwords are read from.
    public uint SourceSrtResource { get; set; } = DescriptorConstants.NoIndex;

    // The guest SRT handle itself, kept for diagnostics and equivalence.
    public ScalarValue SrtHandle { get; init; } = null!;

    // The full dynamic byte offset fed to the scalar-buffer read: base + index * stride.
    public ScalarValue OffsetExpression { get; init; } = null!;

    // The induction variable whose loop guard bounds the candidate set.
    public ScalarValue IndexExpression { get; init; } = null!;

    // The induction's first value and per-iteration increment.
    public ScalarValue Initial { get; init; } = null!;
    public uint Step { get; init; }

    // The guard's exclusive/inclusive limit; null when the bound is a compile-time constant.
    public ScalarValue? Limit { get; init; }
    public bool LimitInclusive { get; init; }

    // The byte distance between two consecutive candidates and the constant base offset.
    public uint Stride { get; init; }
    public uint BaseOffset { get; init; }

    // The proven candidate byte span: MinOffset is the first candidate, MaxOffset is one
    // past the final candidate's descriptor, so MaxOffset - MinOffset is the accessed span.
    public uint MinOffset { get; init; }
    public uint MaxOffset { get; init; }

    // The statically proven candidate count, or -1 when the guard limit is run-time.
    public int Count { get; init; } = -1;

    // The SRT's declared byte size when it is a compile-time constant, else uint.MaxValue.
    // This is a memory-safety bound, not the set of valid descriptors.
    public uint SrtByteExtent { get; init; } = uint.MaxValue;

    // The memory access records this table serves.
    public IReadOnlyList<int> MemoryIndices { get; init; } = [];

    public int Cap { get; init; } = DefaultCap;

    public bool IsStaticallyBounded => Count >= 0;

    // The byte distance between two consecutive candidates: the induction step times the
    // descriptor stride.
    public uint CandidateSpacing => unchecked(Step * Stride);

    // The byte offset of candidate index, valid only for a statically bounded table.
    public uint StaticCandidateOffset(int index) => unchecked(MinOffset + (uint)index * CandidateSpacing);

    // The candidate count for a run-time limit, clamped to the cap. Returns -1 when the
    // limit is below the initial value (an empty loop) or overflows.
    public bool TryResolveCount(uint limit, out int count)
    {
        count = 0;
        if (!Initial.IsConstant)
        {
            return false;
        }

        var first = Initial.ConstantU32;
        var bound = LimitInclusive ? (ulong)limit + 1ul : limit;
        if (bound <= first)
        {
            count = 0;
            return true;
        }

        var span = bound - first;
        var iterations = (span + Step - 1ul) / Step;
        if (iterations > int.MaxValue)
        {
            return false;
        }

        count = (int)iterations;
        return true;
    }

    public BufferCandidateTablePlan WithMemoryIndex(int memoryIndex) => new()
    {
        SourceSrtResource = SourceSrtResource,
        SrtHandle = SrtHandle,
        OffsetExpression = OffsetExpression,
        IndexExpression = IndexExpression,
        Initial = Initial,
        Step = Step,
        Limit = Limit,
        LimitInclusive = LimitInclusive,
        Stride = Stride,
        BaseOffset = BaseOffset,
        MinOffset = MinOffset,
        MaxOffset = MaxOffset,
        Count = Count,
        SrtByteExtent = SrtByteExtent,
        MemoryIndices = [.. MemoryIndices, memoryIndex],
        Cap = Cap,
    };
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Resources;

// SHARPEMU_BRANCHES_WITH_WRITES (on by default, =0 off): the branch selection of a plan that writes memory
// (ResourceBranchBlock.WrittenBuffers), including one that writes only LDS or scratch. Its predicates are read through
// a clean reader that keeps their addresses, and the selection stands only when no written buffer can reach one of
// them: each buffer's records, 256 bytes wider on each side, cover none of those words. A written descriptor the clean
// reader cannot read, or one that swizzles or adds the thread id, keeps every source active, as with the switch off.
public sealed class ResourceBranchWriteGuard : IDisposable
{
    private const ulong Margin = 256;

    private readonly ShaderResourcePlan _plan;
    private readonly GuestWordReader _clean;
    private readonly List<ulong> _reads = [];
    private readonly RuntimeEvaluationScratch _scratch = RuntimeEvaluationScratch.Rent();

    internal ResourceBranchWriteGuard(ShaderResourcePlan plan, ResourceRuntimeInputs inputs)
    {
        _plan = plan;
        _clean = inputs.ReadCleanMemory!;
        Evaluator = new RuntimeValueEvaluator(_scratch, plan, new ResourceRuntimeInputs
        {
            UserData = inputs.UserData, ShaderBase = inputs.ShaderBase, ReadMemory = Record, ReadCleanMemory = Record,
            ComputeState = inputs.ComputeState, IsRangeMapped = inputs.IsRangeMapped, ReadsClean = true,
        });
    }

    // Evaluates the predicates as the selection's clean evaluator does.
    internal RuntimeValueEvaluator Evaluator { get; }

    private bool Record(ulong address, out uint word)
    {
        if (!_clean(address, out word)) return false;
        _reads.Add(address);
        return true;
    }

    // Whether the selection made through Evaluator stands for this dispatch.
    internal bool Holds()
    {
        var predicateReads = _reads.Count;
        Span<uint> words = stackalloc uint[4];
        foreach (var buffer in _plan.ResourceBranchWrites)
        {
            var source = _plan.DescriptorSources[(int)_plan.Info.Buffers[(int)buffer].Source];
            if (source.DwordCount != 4) return false;
            for (var index = 0; index < 4; index++)
            {
                if (!Evaluator.Evaluate(source.Dwords[index], out words[index])) return false;
            }

            if (Reaches(words[0], words[1], words[2], words[3], CollectionsMarshal.AsSpan(_reads)[..predicateReads])) return false;
        }

        return true;
    }

    // Whether a written V# may reach one of the words the predicates read: one that swizzles or adds the thread id may, and
    // so may one whose records, Margin bytes wider on each side, cover such a word.
    internal static bool Reaches(uint word0, uint word1, uint word2, uint word3, ReadOnlySpan<ulong> predicateReads)
    {
        // A descriptor that is not a buffer is bound as null (ResourceMaterializer.BuildSpecialization): nothing is written.
        if ((word3 >> 30) != 0) return false;
        if ((word1 >> 31) != 0 || ((word3 >> 23) & 1) != 0) return true;
        var stride = (word1 >> 16) & 0x3FFFu;
        var size = stride == 0 ? word2 : (ulong)stride * word2;
        var start = word0 | ((ulong)(word1 & 0xFFFF) << 32);
        var low = start > Margin ? start - Margin : 0;
        var high = start + size + Margin;
        foreach (var read in predicateReads)
        {
            if (read + sizeof(uint) > low && read < high) return true;
        }

        return false;
    }

    public void Dispose() => _scratch.Dispose();
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace SharpEmu.ShaderCompiler.Ir;

public readonly record struct IrBlockRange(uint StartPc, uint EndPc);

public sealed class IrControlFlowGraph
{
    private IrControlFlowGraph(
        IReadOnlyList<IrBlockRange> blocks,
        IReadOnlyDictionary<uint, int> blockByStartPc,
        IReadOnlyList<IReadOnlyList<int>> successors,
        IReadOnlyList<IReadOnlyList<int>> predecessors,
        IReadOnlySet<int> loopHeaders)
    {
        Blocks = blocks;
        BlockByStartPc = blockByStartPc;
        Successors = successors;
        Predecessors = predecessors;
        LoopHeaders = loopHeaders;
    }

    public IReadOnlyList<IrBlockRange> Blocks { get; }

    public IReadOnlyDictionary<uint, int> BlockByStartPc { get; }

    public IReadOnlyList<IReadOnlyList<int>> Successors { get; }

    public IReadOnlyList<IReadOnlyList<int>> Predecessors { get; }

    public IReadOnlySet<int> LoopHeaders { get; }

    public bool HasControlFlow => Blocks.Count > 1;

    // Blocks are sorted by start and each ends where the next begins.
    public int BlockOf(uint pc) => BlockOf(Blocks, pc);

    private static int BlockOf(IReadOnlyList<IrBlockRange> blocks, uint pc)
    {
        var low = 0;
        var high = blocks.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var range = blocks[middle];
            if (pc < range.StartPc)
                high = middle - 1;
            else if (pc >= range.EndPc)
                low = middle + 1;
            else
                return middle;
        }

        return -1;
    }

    public static IrControlFlowGraph Build(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IIrBranchResolver resolver)
    {
        var leaders = new SortedSet<uint>();
        if (instructions.Count > 0)
        {
            leaders.Add(instructions[0].Pc);
        }

        for (var index = 0; index < instructions.Count; index++)
        {
            var instruction = instructions[index];
            if (!resolver.IsBranch(instruction))
            {
                continue;
            }

            if (resolver.TryGetBranchTarget(instruction, out var target))
            {
                leaders.Add(target);
            }

            if (index + 1 < instructions.Count)
            {
                leaders.Add(instructions[index + 1].Pc);
            }
        }

        var ordered = leaders.ToList();
        var ranges = new List<IrBlockRange>(ordered.Count);
        var byStart = new Dictionary<uint, int>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var start = ordered[index];
            var end = index + 1 < ordered.Count
                ? ordered[index + 1]
                : instructions.Count > 0 ? instructions[^1].Pc + 1 : start;
            byStart[start] = ranges.Count;
            ranges.Add(new IrBlockRange(start, end));
        }

        var successors = new List<List<int>>(ranges.Count);
        var predecessors = new List<List<int>>(ranges.Count);
        for (var index = 0; index < ranges.Count; index++)
        {
            successors.Add([]);
            predecessors.Add([]);
        }

        var lastByBlock = new Gen5ShaderInstruction?[ranges.Count];
        foreach (var instruction in instructions)
        {
            var block = BlockOf(ranges, instruction.Pc);
            if (block >= 0)
                lastByBlock[block] = instruction;
        }

        for (var blockIndex = 0; blockIndex < ranges.Count; blockIndex++)
        {
            var last = lastByBlock[blockIndex];
            if (last is null)
            {
                continue;
            }

            var isBranch = resolver.IsBranch(last);
            var hasTarget = isBranch && resolver.TryGetBranchTarget(last, out var target) &&
                byStart.TryGetValue(target, out var targetIndex);
            if (hasTarget)
            {
                _ = resolver.TryGetBranchTarget(last, out var resolved);
                Link(successors, predecessors, blockIndex, byStart[resolved]);
            }

            var fallsThrough = !isBranch || resolver.IsConditional(last);
            if (fallsThrough && blockIndex + 1 < ranges.Count)
            {
                Link(successors, predecessors, blockIndex, blockIndex + 1);
            }
        }

        var headers = new HashSet<int>();
        for (var blockIndex = 0; blockIndex < ranges.Count; blockIndex++)
        {
            foreach (var successor in successors[blockIndex])
            {
                if (successor <= blockIndex)
                {
                    headers.Add(successor);
                }
            }
        }

        return new IrControlFlowGraph(
            ranges,
            byStart,
            successors.Select(list => (IReadOnlyList<int>)list).ToList(),
            predecessors.Select(list => (IReadOnlyList<int>)list).ToList(),
            headers);
    }

    private static void Link(
        List<List<int>> successors,
        List<List<int>> predecessors,
        int from,
        int to)
    {
        if (!successors[from].Contains(to))
        {
            successors[from].Add(to);
        }

        if (!predecessors[to].Contains(from))
        {
            predecessors[to].Add(from);
        }
    }
}

public interface IIrBranchResolver
{
    bool IsBranch(Gen5ShaderInstruction instruction);

    bool IsConditional(Gen5ShaderInstruction instruction);

    bool TryGetBranchTarget(Gen5ShaderInstruction instruction, out uint targetPc);
}

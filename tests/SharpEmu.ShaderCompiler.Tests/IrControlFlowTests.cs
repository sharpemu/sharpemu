// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class IrControlFlowTests
{
    private sealed class Resolver : IIrBranchResolver
    {
        public Dictionary<uint, (bool Conditional, uint Target)> Branches { get; } = [];

        public bool IsBranch(Gen5ShaderInstruction instruction) =>
            Branches.ContainsKey(instruction.Pc);

        public bool IsConditional(Gen5ShaderInstruction instruction) =>
            Branches.TryGetValue(instruction.Pc, out var branch) && branch.Conditional;

        public bool TryGetBranchTarget(Gen5ShaderInstruction instruction, out uint targetPc)
        {
            if (Branches.TryGetValue(instruction.Pc, out var branch))
            {
                targetPc = branch.Target;
                return true;
            }

            targetPc = 0;
            return false;
        }
    }

    private static Gen5ShaderInstruction Instruction(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SMov", [], [], [], null);

    private static List<Gen5ShaderInstruction> Straight(params uint[] pcs)
    {
        var list = new List<Gen5ShaderInstruction>();
        foreach (var pc in pcs)
        {
            list.Add(Instruction(pc));
        }

        return list;
    }

    [Fact]
    public void StraightLineCodeIsASingleBlock()
    {
        var instructions = Straight(0, 4, 8, 12);

        var cfg = IrControlFlowGraph.Build(instructions, new Resolver());

        Assert.Single(cfg.Blocks);
        Assert.False(cfg.HasControlFlow);
        Assert.Empty(cfg.LoopHeaders);
    }

    [Fact]
    public void ConditionalBranchSplitsIntoThreeBlocks()
    {
        var instructions = Straight(0, 4, 8, 12, 16);
        var resolver = new Resolver();
        resolver.Branches[4] = (Conditional: true, Target: 12);

        var cfg = IrControlFlowGraph.Build(instructions, resolver);

        Assert.Equal(3, cfg.Blocks.Count);
        Assert.True(cfg.HasControlFlow);

        var entry = cfg.BlockByStartPc[0];
        var fallthrough = cfg.BlockByStartPc[8];
        var target = cfg.BlockByStartPc[12];

        Assert.Contains(target, cfg.Successors[entry]);
        Assert.Contains(fallthrough, cfg.Successors[entry]);
        Assert.Contains(entry, cfg.Predecessors[target]);
    }

    [Fact]
    public void UnconditionalBranchDoesNotFallThrough()
    {
        var instructions = Straight(0, 4, 8, 12);
        var resolver = new Resolver();
        resolver.Branches[4] = (Conditional: false, Target: 12);

        var cfg = IrControlFlowGraph.Build(instructions, resolver);

        var entry = cfg.BlockByStartPc[0];
        var skipped = cfg.BlockByStartPc[8];
        var target = cfg.BlockByStartPc[12];

        Assert.Equal([target], cfg.Successors[entry]);
        Assert.DoesNotContain(skipped, cfg.Successors[entry]);
    }

    [Fact]
    public void BackwardBranchMarksALoopHeader()
    {
        var instructions = Straight(0, 4, 8, 12);
        var resolver = new Resolver();
        resolver.Branches[8] = (Conditional: true, Target: 4);

        var cfg = IrControlFlowGraph.Build(instructions, resolver);

        var header = cfg.BlockByStartPc[4];
        Assert.Contains(header, cfg.LoopHeaders);
        Assert.Contains(header, cfg.Successors[cfg.BlockByStartPc[4]]);
    }

    [Fact]
    public void MergeBlockRecordsBothPredecessors()
    {
        var instructions = Straight(0, 4, 8, 12, 16, 20);
        var resolver = new Resolver();
        resolver.Branches[0] = (Conditional: true, Target: 12);
        resolver.Branches[8] = (Conditional: false, Target: 16);

        var cfg = IrControlFlowGraph.Build(instructions, resolver);

        var merge = cfg.BlockByStartPc[16];
        Assert.Equal(2, cfg.Predecessors[merge].Count);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarSsaTests
{
    [Fact]
    public void CachedQueriesKeepAnalysisInputsSeparate()
    {
        Gen5ShaderInstruction[] instructions = [Mov(0, 8, 42), Nop(4)];
        var first = Gen5ScalarSsa.Build(instructions, [10u]);
        var second = Gen5ScalarSsa.Build(instructions, [20u]);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            Assert.Equal(10u, first.GetScalarAt(4, 0).Constant);
            Assert.Equal(20u, second.GetScalarAt(4, 0).Constant);
            Assert.Equal(42u, first.GetScalarAt(4, 8).Constant);
            Assert.Equal(IrReachingDefinition.At(0), first.GetReachingDefinitionAt(4, 8));
            Assert.Equal(IrReachingDefinition.None, first.GetReachingDefinitionAt(0, 8));
            Assert.Equal(IrScalarValue.Unknown, first.GetScalarAt(99, 8));
            Assert.Equal(IrReachingDefinition.None, first.GetReachingDefinitionAt(4, 256));
        }
    }

    [Fact]
    public void CachedQueriesAreConcurrentAndDoNotAllocateOnHits()
    {
        var analysis = Gen5ScalarSsa.Build([Mov(0, 8, 42), Nop(4)], []);
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal(42u, analysis.GetScalarAt(4, 8).Constant);
            Assert.Equal(IrReachingDefinition.At(0), analysis.GetReachingDefinitionAt(4, 8));
        });
        for (var warmup = 0; warmup < 256; warmup++)
        {
            analysis.GetScalarAt(4, 8);
            analysis.GetReachingDefinitionAt(4, 8);
        }
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var correct = true;
        for (var query = 0; query < 1024; query++)
        {
            correct &= analysis.GetScalarAt(4, 8).Constant == 42;
            correct &= analysis.GetReachingDefinitionAt(4, 8) == IrReachingDefinition.At(0);
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(correct);
        Assert.Equal(0, allocatedBytes);
    }

    private static Gen5ShaderInstruction Mov(uint pc, uint destination, uint literal) =>
        new(
            pc,
            Gen5ShaderEncoding.Sop1,
            "SMov",
            [0u],
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, literal)],
            [Gen5Operand.Scalar(destination)],
            null);

    private static Gen5ShaderInstruction Nop(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SNop", [0u], [], [], null);

    private static Gen5ShaderInstruction Branch(uint pc, string opcode, short wordOffset) =>
        new(
            pc,
            Gen5ShaderEncoding.Sopp,
            opcode,
            [unchecked((uint)(ushort)wordOffset)],
            [],
            [],
            null);

    [Fact]
    public void StraightLineMovResolvesToAConstant()
    {
        List<Gen5ShaderInstruction> program =
        [
            Mov(0, destination: 8, literal: 0x1234),
            Nop(4),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);
        var value = ssa.GetScalarAt(4, 8);

        Assert.Equal(IrScalarState.Constant, value.State);
        Assert.Equal(0x1234u, value.Constant);
    }

    [Fact]
    public void UserDataSeedsTheEntryState()
    {
        List<Gen5ShaderInstruction> program = [Nop(0)];

        var ssa = Gen5ScalarSsa.Build(program, userData: [0x40F55240, 0x00000004]);

        Assert.Equal(IrScalarState.Constant, ssa.GetScalarAt(0, 0).State);
        Assert.Equal(0x40F55240u, ssa.GetScalarAt(0, 0).Constant);
        Assert.Equal(0x00000004u, ssa.GetScalarAt(0, 1).Constant);
    }

    [Fact]
    public void ConflictingValuesFromTwoPathsBecomeMerged()
    {
        // if (cc) s8 = 0xAAAA else s8 = 0xBBBB; use s8
        List<Gen5ShaderInstruction> program =
        [
            Branch(0, "SCbranchScc0", 2),
            Mov(4, destination: 8, literal: 0xAAAA),
            Branch(8, "SBranch", 1),
            Mov(12, destination: 8, literal: 0xBBBB),
            Nop(16),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.True(ssa.Graph.HasControlFlow);
        Assert.Equal(IrScalarState.Merged, ssa.GetScalarAt(16, 8).State);
    }

    [Fact]
    public void SameValueOnBothPathsStaysResolved()
    {
        List<Gen5ShaderInstruction> program =
        [
            Branch(0, "SCbranchScc0", 2),
            Mov(4, destination: 8, literal: 0xCAFE),
            Branch(8, "SBranch", 1),
            Mov(12, destination: 8, literal: 0xCAFE),
            Nop(16),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);
        var value = ssa.GetScalarAt(16, 8);

        Assert.Equal(IrScalarState.Constant, value.State);
        Assert.Equal(0xCAFEu, value.Constant);
    }

    [Fact]
    public void RegisterWrittenOnOnlyOnePathIsMergedAtTheJoin()
    {
        List<Gen5ShaderInstruction> program =
        [
            Mov(0, destination: 8, literal: 0x1111),
            Branch(4, "SCbranchScc0", 1),
            Mov(8, destination: 8, literal: 0x2222),
            Nop(12),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(IrScalarState.Merged, ssa.GetScalarAt(12, 8).State);
    }

    [Fact]
    public void ValueBeforeTheBranchIsStillResolved()
    {
        List<Gen5ShaderInstruction> program =
        [
            Mov(0, destination: 8, literal: 0x1111),
            Branch(4, "SCbranchScc0", 1),
            Mov(8, destination: 8, literal: 0x2222),
            Nop(12),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);
        var value = ssa.GetScalarAt(4, 8);

        Assert.Equal(IrScalarState.Constant, value.State);
        Assert.Equal(0x1111u, value.Constant);
    }

    [Fact]
    public void MergeJoinIsReportedForDivergentBlocks()
    {
        List<Gen5ShaderInstruction> program =
        [
            Branch(0, "SCbranchScc0", 1),
            Mov(4, destination: 8, literal: 1),
            Nop(8),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.True(ssa.IsInsideDivergentMerge(8));
        Assert.False(ssa.IsInsideDivergentMerge(0));
    }

    [Fact]
    public void LoopDoesNotHangTheFixpoint()
    {
        List<Gen5ShaderInstruction> program =
        [
            Mov(0, destination: 8, literal: 1),
            Nop(4),
            Branch(8, "SCbranchScc1", -3),
            Nop(12),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.NotEmpty(ssa.Graph.LoopHeaders);
        Assert.Equal(IrScalarState.Constant, ssa.GetScalarAt(12, 8).State);
    }
}

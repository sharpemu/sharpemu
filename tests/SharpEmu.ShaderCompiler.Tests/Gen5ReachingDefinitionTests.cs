// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ReachingDefinitionTests
{
    private static Gen5ShaderInstruction Load(uint pc, uint destination) =>
        new(
            pc,
            Gen5ShaderEncoding.Smem,
            "SLoadDwordx4",
            [0u, 0u],
            [Gen5Operand.Scalar(0)],
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
    public void SingleDefinitionIsTracked()
    {
        List<Gen5ShaderInstruction> program = [Load(0, 16), Nop(4)];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);
        var reaching = ssa.GetReachingDefinitionAt(4, 16);

        Assert.Equal(IrReachingState.Single, reaching.State);
        Assert.Equal(0u, reaching.DefinitionPc);
    }

    [Fact]
    public void TwoDefinitionsOnDifferentPathsBecomeMultiple()
    {
        // The case that produced garbage descriptors: the same register is
        // written on both sides of a branch and read after the join.
        List<Gen5ShaderInstruction> program =
        [
            Branch(0, "SCbranchScc0", 2),
            Load(4, 16),
            Branch(8, "SBranch", 1),
            Load(12, 16),
            Nop(16),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(IrReachingState.Multiple, ssa.GetReachingDefinitionAt(16, 16).State);
    }

    [Fact]
    public void DefinitionOnOnlyOnePathIsAlsoMultipleAtTheJoin()
    {
        List<Gen5ShaderInstruction> program =
        [
            Load(0, 16),
            Branch(4, "SCbranchScc0", 1),
            Load(8, 16),
            Nop(12),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(IrReachingState.Multiple, ssa.GetReachingDefinitionAt(12, 16).State);
    }

    [Fact]
    public void DefinitionBeforeTheBranchStaysSingle()
    {
        List<Gen5ShaderInstruction> program =
        [
            Load(0, 16),
            Branch(4, "SCbranchScc0", 1),
            Load(8, 16),
            Nop(12),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);
        var reaching = ssa.GetReachingDefinitionAt(4, 16);

        Assert.Equal(IrReachingState.Single, reaching.State);
        Assert.Equal(0u, reaching.DefinitionPc);
    }

    [Fact]
    public void UserDataCountsAsADefinition()
    {
        List<Gen5ShaderInstruction> program = [Nop(0)];

        var ssa = Gen5ScalarSsa.Build(program, userData: [0x1111, 0x2222]);

        Assert.Equal(IrReachingState.Single, ssa.GetReachingDefinitionAt(0, 0).State);
        Assert.Equal(IrReachingState.None, ssa.GetReachingDefinitionAt(0, 64).State);
    }

    [Fact]
    public void UnwrittenRegisterHasNoDefinition()
    {
        List<Gen5ShaderInstruction> program = [Load(0, 16), Nop(4)];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(IrReachingState.None, ssa.GetReachingDefinitionAt(4, 32).State);
    }

    [Fact]
    public void ReachingDefinitionSeesLoadsThatConstantPropagationCannot()
    {
        // The whole point of the second analysis: SLoadDwordx4 has no compile-time
        // value, so constant propagation reports Unknown and never Merged. Reaching
        // definitions still proves the register has two possible writers.
        List<Gen5ShaderInstruction> program =
        [
            Branch(0, "SCbranchScc0", 2),
            Load(4, 16),
            Branch(8, "SBranch", 1),
            Load(12, 16),
            Nop(16),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(IrScalarState.Unknown, ssa.GetScalarAt(16, 16).State);
        Assert.Equal(IrReachingState.Multiple, ssa.GetReachingDefinitionAt(16, 16).State);
    }

    [Fact]
    public void VopcWithExplicitScalarDestinationDoesNotOverwriteVcc()
    {
        var compare = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vopc,
            "VCmpLeF32",
            [0u, 0u],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            [Gen5Operand.Scalar(14)],
            null);
        List<Gen5ShaderInstruction> program = [compare, Nop(8)];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(
            IrReachingState.Single,
            ssa.GetReachingDefinitionAt(8, 14).State);
        Assert.Equal(
            IrReachingState.None,
            ssa.GetReachingDefinitionAt(8, Gen5ScalarSsa.VccLo).State);
        Assert.Equal(
            IrReachingState.None,
            ssa.GetReachingDefinitionAt(8, Gen5ScalarSsa.VccHi).State);
    }
}

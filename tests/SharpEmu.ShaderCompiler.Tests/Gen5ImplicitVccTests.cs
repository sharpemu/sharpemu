// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImplicitVccTests
{
    private static Gen5ShaderInstruction Vopc(uint pc, string opcode = "VCmpEqU32") =>
        new(pc, Gen5ShaderEncoding.Vopc, opcode, [0u], [], [], null);

    private static Gen5ShaderInstruction Vop2(uint pc, string opcode) =>
        new(pc, Gen5ShaderEncoding.Vop2, opcode, [0u], [], [Gen5Operand.Vector(0)], null);

    private static Gen5ShaderInstruction Nop(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SNop", [0u], [], [], null);

    [Fact]
    public void VopcIsRecognisedAsAnImplicitVccWriter()
    {
        Assert.True(Gen5ScalarSsa.WritesVccImplicitly(Vopc(0)));
        Assert.True(Gen5ScalarSsa.WritesVccImplicitly(Vopc(0, "VCmpLtF32")));
        Assert.False(Gen5ScalarSsa.WritesVccImplicitly(Vopc(0, "VCmpxLtF16")));
    }

    [Fact]
    public void Vop2CarryFormsAreRecognised()
    {
        Assert.True(Gen5ScalarSsa.WritesVccImplicitly(Vop2(0, "VAddCoCiU32")));
        Assert.True(Gen5ScalarSsa.WritesVccImplicitly(Vop2(0, "VSubCoCiU32")));
        Assert.True(Gen5ScalarSsa.WritesVccImplicitly(Vop2(0, "VSubrevCoCiU32")));
    }

    [Fact]
    public void PlainVop2DoesNotWriteVcc()
    {
        Assert.False(Gen5ScalarSsa.WritesVccImplicitly(Vop2(0, "VAddF32")));
        Assert.False(Gen5ScalarSsa.WritesVccImplicitly(Nop(0)));
    }

    [Fact]
    public void VopcDefinesBothVccHalves()
    {
        List<Gen5ShaderInstruction> program = [Vopc(0), Nop(4)];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        var low = ssa.GetReachingDefinitionAt(4, Gen5ScalarSsa.VccLo);
        var high = ssa.GetReachingDefinitionAt(4, Gen5ScalarSsa.VccHi);

        Assert.Equal(IrReachingState.Single, low.State);
        Assert.Equal(0u, low.DefinitionPc);
        Assert.Equal(IrReachingState.Single, high.State);
        Assert.Equal(0u, high.DefinitionPc);
    }

    [Fact]
    public void WithoutTheImplicitWriteVccWouldLookUndefined()
    {
        // The regression this models: before implicit VCC was tracked the offset
        // register reported "none" and its stale value was used as a byte offset.
        List<Gen5ShaderInstruction> program = [Nop(0), Nop(4)];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(IrReachingState.None, ssa.GetReachingDefinitionAt(4, Gen5ScalarSsa.VccLo).State);
    }

    [Fact]
    public void VccWrittenOnBothBranchesBecomesMultiple()
    {
        List<Gen5ShaderInstruction> program =
        [
            new(0, Gen5ShaderEncoding.Sopp, "SCbranchScc0", [2u], [], [], null),
            Vopc(4),
            new(8, Gen5ShaderEncoding.Sopp, "SBranch", [1u], [], [], null),
            Vopc(12),
            Nop(16),
        ];

        var ssa = Gen5ScalarSsa.Build(program, userData: []);

        Assert.Equal(
            IrReachingState.Multiple,
            ssa.GetReachingDefinitionAt(16, Gen5ScalarSsa.VccLo).State);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class IndirectSelectorRegisterEffectsTests
{
    private static Gen5ShaderInstruction Operation(string opcode) => opcode.StartsWith("VCmp", StringComparison.Ordinal)
        ? Vopc(0xF1C, opcode, Operand(0), 0)
        : opcode.StartsWith("VFmaMix", StringComparison.Ordinal)
            ? Vop3(0xF1C, opcode, 4, Gen5Operand.Vector(0), Gen5Operand.Vector(0), Gen5Operand.Vector(0)) with
                { Encoding = Gen5ShaderEncoding.Vop3p, Control = new Gen5Vop3pControl(0, 0, 0, 0, false) }
            : Vop3(0xF1C, opcode, 4, Gen5Operand.Vector(0), Gen5Operand.Vector(0), Gen5Operand.Vector(0));

    // The scalar bit scan is live across the inserted operation. Its vector
    // copy is defined afterwards, so EXEC narrowing cannot invalidate it.
    private static ShaderResourcePlan Plan(Gen5ShaderInstruction operation, uint scalar = 106)
    {
        var image = ResourceTrackerTests.IndirectImageProgram(false);
        return Extract(Program([
            Sop1(0xF10, "SFF1I32B32", scalar, Gen5Operand.Scalar(8)),
            Sop2(0xF18, "SLshlB32", scalar, Gen5Operand.Scalar(scalar), Operand(5)),
            operation,
            MoveVectorFromScalar(0x1000, 1, scalar),
            .. image.Instructions.Skip(1)]));
    }

    private static IndirectSelectorValues? Selector(ShaderResourcePlan plan) =>
        plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!.SelectorValues;

    private static void AssertFinite(ShaderResourcePlan plan)
    {
        var selector = Assert.IsType<IndirectSelectorValues>(Selector(plan));
        Assert.True(selector.TryEvaluate(plan, Inputs(new uint[128]), out var values));
        var expected = Enumerable.Range(0, 32).Select(i => (uint)i << 5).Append(uint.MaxValue << 5);
        Assert.Equal(expected.Order(), values.Order());
    }

    [Theory]
    [InlineData(106u)]
    [InlineData(107u)]
    public void UnmodifiedVccBitScanHasFiniteDomain(uint scalar) => AssertFinite(Plan(Nop(0xF1C), scalar));

    [Theory]
    [InlineData("VCmpxEqU32", 106u)]
    [InlineData("VCmpxLtF32", 107u)]
    [InlineData("VFmaMixF32", 106u)]
    [InlineData("VFmaMixloF16", 107u)]
    [InlineData("VFmaMixhiF16", 106u)]
    public void ExecOnlyCompareAndMixedFmaPreserveScalarBitScan(string opcode, uint scalar) =>
        AssertFinite(Plan(Operation(opcode), scalar));

    [Theory]
    [InlineData(Gen5ShaderEncoding.Vopc)]
    [InlineData(Gen5ShaderEncoding.Vop3)]
    public void ExecOnlyCompareIgnoresLegacyScalarDestination(Gen5ShaderEncoding encoding)
    {
        Gen5InstructionControl control = encoding == Gen5ShaderEncoding.Vopc
            ? new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, 40)
            : new Gen5Vop3Control(0, 0, 0, false, 0, 40);
        AssertFinite(Plan(Operation("VCmpxEqU32") with
            { Encoding = encoding, Control = control, Destinations = [Gen5Operand.Scalar(40)] }, 41));
    }

    [Theory]
    [InlineData("VCmpEqU32")]
    [InlineData("VAddCoU32")]
    [InlineData("VAddcU32")]
    [InlineData("VSubbU32")]
    [InlineData("VMadU64U32")]
    [InlineData("VUnknownFutureOpcode")]
    public void RealOrUnknownVccWriterDeclines(string opcode) => Assert.Null(Selector(Plan(Operation(opcode))));

    [Theory]
    [InlineData(Gen5ShaderEncoding.Vopc, 40u)]
    [InlineData(Gen5ShaderEncoding.Vopc, 41u)]
    [InlineData(Gen5ShaderEncoding.Vop3, 40u)]
    [InlineData(Gen5ShaderEncoding.Vop3, 41u)]
    public void CompareScalarDestinationClobbersBothHalves(Gen5ShaderEncoding encoding, uint scalar)
    {
        Gen5InstructionControl control = encoding == Gen5ShaderEncoding.Vopc
            ? new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, 40)
            : new Gen5Vop3Control(0, 0, 0, false, 0, 40);
        Assert.Null(Selector(Plan(Operation("VCmpEqU32") with
            { Encoding = encoding, Control = control, Destinations = [Gen5Operand.Scalar(40)] }, scalar)));
    }

    [Theory]
    [InlineData(106u)]
    [InlineData(107u)]
    public void ExplicitScalarClobberDeclines(uint scalar) =>
        Assert.Null(Selector(Plan(MoveScalarRegister(0xF1C, scalar, 20), scalar)));

    [Fact]
    public void WideScalarClobberOfHighHalfDeclines() =>
        Assert.Null(Selector(Plan(Sop1(0xF1C, "SMovB64", 106, Gen5Operand.Scalar(20)), 107)));

    [Fact]
    public void MixedFmaVectorClobberDeclines()
    {
        var plan = Plan(Nop(0xF1C));
        var instructions = plan.Graph.Program.Instructions.ToList();
        instructions.Add(Operation("VFmaMixF32") with { Pc = 0x1004, Destinations = [Gen5Operand.Vector(1)] });
        Assert.Null(Selector(Extract(Program([.. instructions.OrderBy(i => i.Pc)]))));
    }

    [Fact]
    public void ExecOnlyCompareAfterVectorDefinitionStillDeclines()
    {
        var plan = Plan(Nop(0xF1C));
        var instructions = plan.Graph.Program.Instructions.ToList();
        instructions.Add(Operation("VCmpxEqU32") with { Pc = 0x1004 });
        Assert.Null(Selector(Extract(Program([.. instructions.OrderBy(i => i.Pc)]))));
    }
}

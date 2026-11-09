// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class VectorCompareResourceTests
{
    [Theory]
    [InlineData("ImageSampleA", 12u, true, false)]
    [InlineData("ImageSampleA", 0u, false, false)]
    [InlineData("ImageSample", 12u, false, false)]
    [InlineData("ImageSampleA", 12u, true, true)]
    [InlineData("ImageSampleA", 0u, false, true)]
    [InlineData("ImageSample", 12u, false, true)]
    [InlineData("ImageSampleA", 12u, true, true, true)]
    [InlineData("ImageSampleA", 0u, false, true, true)]
    [InlineData("ImageSample", 12u, false, true, true)]
    public void QuadMaskSamplerAdjustmentPreservesDescriptorBits(string opcode, uint shift, bool valid, bool loop, bool descriptorBeforeLoop = false)
    {
        var program = Program(
            Sop1(0, "SWqmB64", 126, Gen5Operand.Scalar(126)),
            Vopc(4, "VCmpEqU32", Operand(1), 4),
            Sop1(8, "SQuadmaskB64", 106, Gen5Operand.Scalar(106)),
            Sop2(12, "SLshlB32", 56, Gen5Operand.Scalar(106), Operand(shift)),
            descriptorBeforeLoop ? Sop2(16, "SOrB32", 19, Gen5Operand.Scalar(19), Gen5Operand.Scalar(56)) : Nop(16),
            Nop(20),
            loop ? Branch(24, "SCbranchScc1", -2) : Nop(24),
            descriptorBeforeLoop ? Nop(28) : Sop2(28, "SOrB32", 19, Gen5Operand.Scalar(19), Gen5Operand.Scalar(56)),
            Image(32, opcode, 8, 16) with { Words = [opcode == "ImageSampleA" ? 0xF0800709u : 0xF0800708u, 0u] },
            EndProgram(40));
        if (!valid)
        {
            Assert.Throws<ResourcePlanException>(() => ShaderResourcePlan.Extract(program, ShaderStage.Pixel, Hash, 0, 20));
            return;
        }
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, Hash, 0, 20);
        var userData = new uint[20];
        userData[8] = 0x20;
        userData[9] = 71u << 20;
        userData[11] = (9u << 28) | Gen5ShaderTranslator.IdentityImageDstSelect;
        userData[16] = 4;
        userData[19] = 0xC0000123;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        Assert.Equal(userData[19], Assert.Single(snapshot.Samplers)[3]);
    }

    [Fact]
    public void BitreplicateB64B32Compiles()
    {
        var program = Program(
            Sop1(0, "SBitreplicateB64B32", 0, Operand(7)),
            EndProgram(8));

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, userDataCount: 0), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    [Fact]
    public void InvariantMergeRequiresEquivalentExternalInputs()
    {
        var memory = MemoryAccessTable.Build(Program(EndProgram(0)));
        var first = ScalarValue.Phi(1, ScalarValueType.U32);
        var second = ScalarValue.Phi(2, ScalarValueType.U32);
        var mask = ScalarValue.ConstantOf(0x0FFFF000u);
        first.SetPhiOperands([0, 2], [mask, second]);
        second.SetPhiOperands([1, 2], [first, second]);
        Assert.Same(mask, ScalarValueEquivalence.ResolveInvariantPhi(memory, first));

        second.SetPhiOperands([0, 1], [ScalarValue.ConstantOf(1u), first]);
        Assert.Null(ScalarValueEquivalence.ResolveInvariantPhi(memory, first));

        first.SetPhiOperands([2], [second]);
        second.SetPhiOperands([1], [first]);
        Assert.Null(ScalarValueEquivalence.ResolveInvariantPhi(memory, first));
    }

    [Theory]
    [InlineData("VCmpEqU16")]
    [InlineData("VCmpLtU16")]
    [InlineData("VCmpGeU16")]
    [InlineData("VCmpEqI16")]
    [InlineData("VCmpLtI16")]
    public void Integer16BitCompareCompilesToSpirv(string opcode)
    {
        var program = Program(
            Vopc(0, opcode, Gen5Operand.Vector(0), 1),
            EndProgram(8));

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, userDataCount: 0), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }
}

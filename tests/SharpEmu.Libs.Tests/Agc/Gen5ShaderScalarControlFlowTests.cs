// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderScalarControlFlowTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Theory]
    [InlineData("SCbranchVccz", "VCmpEqF32")]
    [InlineData("SCbranchVccnz", "VCmpEqF32")]
    [InlineData("SCbranchExecz", "VCmpxEqF32")]
    [InlineData("SCbranchExecnz", "VCmpxEqF32")]
    public void UnknownVectorMaskBranch_CollectsImagesFromBothSuccessors(
        string branchOpcode,
        string compareOpcode)
    {
        var instructions = new[]
        {
            Instruction(0, Gen5ShaderEncoding.Vopc, compareOpcode),
            Branch(4, branchOpcode, 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(new uint[] { 8, 16 }, evaluation.ImageBindings.Select(binding => binding.Pc));
        Assert.Equal(
            CreateUserData()[..8],
            evaluation.ImageBindings.Single(binding => binding.Pc == 8).ResourceDescriptor);
        Assert.Equal(
            CreateUserData()[8..16],
            evaluation.ImageBindings.Single(binding => binding.Pc == 16).ResourceDescriptor);
    }

    [Theory]
    [InlineData("VAddcU32")]
    [InlineData("VSubbU32")]
    [InlineData("VSubbrevU32")]
    public void Vop2CarryOutput_MakesVccBranchUnknown(string opcode)
    {
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Vop2,
                opcode,
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                [Gen5Operand.Vector(2)]),
            Branch(4, "SCbranchVccz", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(new uint[] { 8, 16 }, evaluation.ImageBindings.Select(binding => binding.Pc));
    }

    [Fact]
    public void Gfx10Cmpx_PreservesKnownVcc()
    {
        var instructions = new[]
        {
            Instruction(0, Gen5ShaderEncoding.Vopc, "VCmpxEqF32"),
            Branch(4, "SCbranchVccz", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(16u, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Fact]
    public void SdwaCompareWithScalarDestination_PreservesKnownVcc()
    {
        var instructions = new[]
        {
            new Gen5ShaderInstruction(
                0,
                Gen5ShaderEncoding.Vopc,
                "VCmpEqU32",
                [0],
                [Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
                [],
                new Gen5SdwaControl(
                    DestinationSelect: 0,
                    DestinationUnused: 0,
                    Source0Select: 6,
                    Source1Select: 6,
                    Source0SignExtend: false,
                    Source1SignExtend: false,
                    AbsoluteMask: 0,
                    NegateMask: 0,
                    OutputModifier: 0,
                    Clamp: false,
                    ScalarDestination: 0)),
            Branch(4, "SCbranchVccz", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(16u, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Theory]
    [InlineData("SCbranchScc0", 8u)]
    [InlineData("SCbranchScc1", 16u)]
    public void KnownSccBranch_UsesOnlyTheArchitecturalSuccessor(
        string branchOpcode,
        uint expectedImagePc)
    {
        var userData = CreateUserData();
        userData[24] = 0x55AA;
        userData[25] = 0x55AA;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopc,
                "SCmpEqU32",
                [Gen5Operand.Scalar(24), Gen5Operand.Scalar(25)]),
            Branch(4, branchOpcode, 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(expectedImagePc, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Theory]
    [InlineData("SCbranchVccz", 12u)]
    [InlineData("SCbranchVccnz", 4u)]
    [InlineData("SCbranchExecz", 4u)]
    [InlineData("SCbranchExecnz", 12u)]
    public void KnownVectorMaskBranch_UsesOnlyTheArchitecturalSuccessor(
        string branchOpcode,
        uint expectedImagePc)
    {
        var instructions = new[]
        {
            Branch(0, branchOpcode, 12),
            Image(4, 0, 16),
            Branch(8, "SBranch", 16),
            Image(12, 8, 20),
            End(16),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(expectedImagePc, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Fact]
    public void UnconditionalBranch_DoesNotEvaluateTheFallthroughBlock()
    {
        var instructions = new[]
        {
            Branch(0, "SBranch", 8),
            Image(4, 0, 16),
            Image(8, 8, 20),
            End(12),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(8u, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Fact]
    public void DirectBranchBeyondDecodedProgram_IsRejected()
    {
        var instructions = new[]
        {
            Branch(0, "SBranch", 0x40),
            End(4),
        };

        Assert.False(
            TryEvaluate(instructions, CreateUserData(), out _, out var error));
        Assert.Contains("invalid-scalar-branch-target", error, StringComparison.Ordinal);
        Assert.Contains("target=0x40", error, StringComparison.Ordinal);
    }

    [Fact]
    public void StraightLineEvaluation_PreservesScalarValuesAndSnapshots()
    {
        var instructions = new[]
        {
            MoveLiteral(0, 24, 7),
            Instruction(
                4,
                Gen5ShaderEncoding.Sopk,
                "SAddkI32",
                [new Gen5Operand(Gen5OperandKind.EncodedConstant, 5)],
                [Gen5Operand.Scalar(24)]),
            End(8),
        };
        var userData = CreateUserData();

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(userData[24], evaluation.InitialScalarRegisters[24]);
        Assert.Equal(7u, evaluation.ScalarRegistersByPc![4][24]);
        Assert.Equal(12u, evaluation.ScalarRegistersByPc[8][24]);
        Assert.Equal(12u, evaluation.ScalarRegisters[24]);
    }

    [Fact]
    public void NullScalarRegister_ReadsZeroAndDiscardsSingleAndPairWrites()
    {
        var userData = new uint[128];
        userData[125] = 0xDEAD_BEEF;
        var instructions = new[]
        {
            MoveLiteral(0, 125, 0xAAAA_AAAA),
            Instruction(
                4,
                Gen5ShaderEncoding.Sop1,
                "SMovB32",
                [Gen5Operand.Scalar(125)],
                [Gen5Operand.Scalar(24)]),
            Instruction(
                8,
                Gen5ShaderEncoding.Sop1,
                "SMovB64",
                [new Gen5Operand(Gen5OperandKind.LiteralConstant, 0)],
                [Gen5Operand.Scalar(125)]),
            End(12),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(0u, evaluation.InitialScalarRegisters[125]);
        Assert.Equal(0u, evaluation.ScalarRegisters[24]);
        Assert.Equal(0u, evaluation.ScalarRegisters[125]);
        Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[126]);
    }

    [Theory]
    [InlineData("SBfeU64")]
    [InlineData("SBfeI64")]
    public void ScalarBfe64_PropagatesBothDestinationDwords(string opcode)
    {
        var userData = new uint[26];
        userData[24] = 0x89AB_CDEF;
        userData[25] = 0x8123_4567;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sop2,
                opcode,
                [
                    Gen5Operand.Scalar(24),
                    new Gen5Operand(
                        Gen5OperandKind.LiteralConstant,
                        64u << 16),
                ],
                [Gen5Operand.Scalar(30)]),
            Instruction(
                4,
                Gen5ShaderEncoding.Sop1,
                "SMovB64",
                [Gen5Operand.Scalar(30)],
                [Gen5Operand.Scalar(32)]),
            End(8),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(userData[24], evaluation.ScalarRegisters[30]);
        Assert.Equal(userData[25], evaluation.ScalarRegisters[31]);
        Assert.Equal(userData[24], evaluation.ScalarRegisters[32]);
        Assert.Equal(userData[25], evaluation.ScalarRegisters[33]);
    }

    [Fact]
    public void ScalarShift64_SignExtendsNegativeInlineInteger()
    {
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sop2,
                "SLshrB64",
                [
                    new Gen5Operand(Gen5OperandKind.EncodedConstant, 193),
                    new Gen5Operand(Gen5OperandKind.EncodedConstant, 160),
                ],
                [Gen5Operand.Scalar(24)]),
            End(4),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(uint.MaxValue, evaluation.ScalarRegisters[24]);
        Assert.Equal(0u, evaluation.ScalarRegisters[25]);
    }

    [Fact]
    public void BackEdge_ConvergesAfterRegisterValuesJoin()
    {
        var instructions = new[]
        {
            Instruction(0, Gen5ShaderEncoding.Vopc, "VCmpEqF32"),
            Image(4, 0, 16),
            Instruction(
                8,
                Gen5ShaderEncoding.Sopk,
                "SAddkI32",
                [new Gen5Operand(Gen5OperandKind.EncodedConstant, 1)],
                [Gen5Operand.Scalar(24)]),
            Branch(12, "SCbranchVccz", 4),
            End(16),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(4u, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Fact]
    public void RuntimeComputeRegister_MakesDependentSccBranchUnknown()
    {
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopc,
                "SCmpEqU32",
                [
                    Gen5Operand.Scalar(24),
                    new Gen5Operand(Gen5OperandKind.EncodedConstant, 128),
                ]),
            Branch(4, "SCbranchScc1", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(
                instructions,
                CreateUserData(),
                out var evaluation,
                out var error,
                new Gen5ComputeSystemRegisters(24, null, null, null)),
            error);
        Assert.Equal(new uint[] { 8, 16 }, evaluation.ImageBindings.Select(binding => binding.Pc));
    }

    [Fact]
    public void EntryScc_IsUnknownUntilAnInstructionDefinesIt()
    {
        var instructions = new[]
        {
            Branch(0, "SCbranchScc1", 12),
            Image(4, 0, 16),
            Branch(8, "SBranch", 16),
            Image(12, 8, 20),
            End(16),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(new uint[] { 4, 12 }, evaluation.ImageBindings.Select(binding => binding.Pc));
    }

    [Fact]
    public void UnmodeledEntrySgpr_MakesDependentSccBranchUnknown()
    {
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopc,
                "SCmpEqU32",
                [
                    Gen5Operand.Scalar(40),
                    new Gen5Operand(Gen5OperandKind.EncodedConstant, 128),
                ]),
            Branch(4, "SCbranchScc1", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(new uint[] { 8, 16 }, evaluation.ImageBindings.Select(binding => binding.Pc));
    }

    [Fact]
    public void UnsignedSopkCompare_ZeroExtendsItsImmediate()
    {
        var userData = CreateUserData();
        userData[24] = uint.MaxValue;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopk,
                "SCmpkEqU32",
                [new Gen5Operand(Gen5OperandKind.EncodedConstant, 0xFFFF)],
                [Gen5Operand.Scalar(24)],
                [0xFFFF]),
            Branch(4, "SCbranchScc1", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(8u, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Fact]
    public void SAddkI32_SetsSccOnSignedOverflow()
    {
        var userData = CreateUserData();
        userData[24] = int.MaxValue;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopk,
                "SAddkI32",
                [new Gen5Operand(Gen5OperandKind.EncodedConstant, 1)],
                [Gen5Operand.Scalar(24)],
                [1]),
            Branch(4, "SCbranchScc1", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(16u, Assert.Single(evaluation.ImageBindings).Pc);
        Assert.Equal(0x8000_0000u, evaluation.ScalarRegisters[24]);
    }

    [Theory]
    [InlineData("SBrevB32", 0u)]
    [InlineData("SFF1I32B32", 1u)]
    public void ScalarUnaryWithoutSccResult_PreservesScc(
        string opcode,
        uint sourceValue)
    {
        var userData = CreateUserData();
        userData[24] = 7;
        userData[25] = 7;
        userData[26] = sourceValue;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopc,
                "SCmpEqU32",
                [Gen5Operand.Scalar(24), Gen5Operand.Scalar(25)]),
            Instruction(
                4,
                Gen5ShaderEncoding.Sop1,
                opcode,
                [Gen5Operand.Scalar(26)],
                [Gen5Operand.Scalar(27)]),
            Branch(8, "SCbranchScc1", 20),
            Image(12, 0, 16),
            Branch(16, "SBranch", 24),
            Image(20, 8, 20),
            End(24),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(20u, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Fact]
    public void ScalarConditionReadWithUnknownData_PreservesScc()
    {
        var userData = CreateUserData();
        userData[24] = 7;
        userData[25] = 7;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopc,
                "SCmpEqU32",
                [Gen5Operand.Scalar(24), Gen5Operand.Scalar(25)]),
            Instruction(
                4,
                Gen5ShaderEncoding.Sop2,
                "SCselectB32",
                [
                    Gen5Operand.Scalar(40),
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 5),
                ],
                [Gen5Operand.Scalar(26)]),
            Branch(8, "SCbranchScc1", 20),
            Image(12, 0, 16),
            Branch(16, "SBranch", 24),
            Image(20, 8, 20),
            End(24),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(20u, Assert.Single(evaluation.ImageBindings).Pc);
    }

    [Fact]
    public void SCselectB64_PreservesSccWhenSelectedValueIsZero()
    {
        var userData = CreateUserData();
        userData[24] = 7;
        userData[25] = 7;
        userData[26] = 0;
        userData[27] = 0;
        userData[28] = uint.MaxValue;
        userData[29] = uint.MaxValue;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sopc,
                "SCmpEqU32",
                [Gen5Operand.Scalar(24), Gen5Operand.Scalar(25)]),
            Instruction(
                4,
                Gen5ShaderEncoding.Sop2,
                "SCselectB64",
                [Gen5Operand.Scalar(26), Gen5Operand.Scalar(28)],
                [Gen5Operand.Scalar(30)]),
            Branch(8, "SCbranchScc1", 20),
            Image(12, 0, 16),
            Branch(16, "SBranch", 24),
            Image(20, 8, 20),
            End(24),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(20u, Assert.Single(evaluation.ImageBindings).Pc);
        Assert.Equal(0u, evaluation.ScalarRegisters[30]);
        Assert.Equal(0u, evaluation.ScalarRegisters[31]);
    }

    [Fact]
    public void SWqmB64_ExpandsActiveQuadAndSetsScc()
    {
        var userData = CreateUserData();
        userData[24] = 0b0010;
        userData[25] = 0;
        var instructions = new[]
        {
            Instruction(
                0,
                Gen5ShaderEncoding.Sop1,
                "SWqmB64",
                [Gen5Operand.Scalar(24)],
                [Gen5Operand.Scalar(26)]),
            Branch(4, "SCbranchScc1", 16),
            Image(8, 0, 16),
            Branch(12, "SBranch", 20),
            Image(16, 8, 20),
            End(20),
        };

        Assert.True(
            TryEvaluate(instructions, userData, out var evaluation, out var error),
            error);
        Assert.Equal(16u, Assert.Single(evaluation.ImageBindings).Pc);
        Assert.Equal(0b1111u, evaluation.ScalarRegisters[26]);
        Assert.Equal(0u, evaluation.ScalarRegisters[27]);
    }

    [Fact]
    public void StorageMip_UsesOnlyTheCfgReachingVectorConstant()
    {
        var instructions = new[]
        {
            VectorMoveLiteral(0, 3, 3),
            Branch(4, "SBranch", 12),
            VectorMoveLiteral(8, 3, 9),
            ImageMip(12, "ImageStoreMip"),
            End(16),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Equal(3u, Assert.Single(evaluation.ImageBindings).MipLevel);
    }

    [Fact]
    public void StorageMip_RejectsConflictingCfgConstants()
    {
        var instructions = new[]
        {
            Instruction(0, Gen5ShaderEncoding.Vopc, "VCmpEqF32"),
            Branch(4, "SCbranchVccz", 16),
            VectorMoveLiteral(8, 3, 1),
            Branch(12, "SBranch", 20),
            VectorMoveLiteral(16, 3, 2),
            ImageMip(20, "ImageStoreMip"),
            End(24),
        };

        Assert.False(
            TryEvaluate(instructions, CreateUserData(), out _, out var error));
        Assert.Contains("conflicting storage mip", error, StringComparison.Ordinal);
        Assert.Contains("v3", error, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageMip_RejectsRuntimeVectorValue()
    {
        var instructions = new[]
        {
            ImageMip(0, "ImageStoreMip"),
            End(4),
        };

        Assert.False(
            TryEvaluate(instructions, CreateUserData(), out _, out var error));
        Assert.Contains("path-dependent storage mip", error, StringComparison.Ordinal);
        Assert.Contains("v3", error, StringComparison.Ordinal);
    }

    [Fact]
    public void SampledImageLoadMip_AcceptsRuntimeVectorLod()
    {
        var instructions = new[]
        {
            ImageMip(0, "ImageLoadMip"),
            End(4),
        };

        Assert.True(
            TryEvaluate(instructions, CreateUserData(), out var evaluation, out var error),
            error);
        Assert.Null(Assert.Single(evaluation.ImageBindings).MipLevel);
    }

    [Theory]
    [InlineData(0u, "resource=s0")]
    [InlineData(16u, "sampler=s16")]
    public void ConflictingReachingImageTuples_AreRejectedExplicitly(
        uint destination,
        string expectedRegister)
    {
        var instructions = new[]
        {
            Instruction(0, Gen5ShaderEncoding.Vopc, "VCmpEqF32"),
            Branch(4, "SCbranchVccz", 16),
            MoveLiteral(8, destination, 0xAAAA_AAAA),
            Branch(12, "SBranch", 20),
            MoveLiteral(16, destination, 0xBBBB_BBBB),
            Image(20, 0, 16),
            End(24),
        };

        Assert.False(
            TryEvaluate(instructions, CreateUserData(), out _, out var error));
        Assert.Contains("conflicting image binding", error, StringComparison.Ordinal);
        Assert.Contains(expectedRegister, error, StringComparison.Ordinal);
    }

    private static bool TryEvaluate(
        IReadOnlyList<Gen5ShaderInstruction> instructions,
        IReadOnlyList<uint> userData,
        out Gen5ShaderEvaluation evaluation,
        out string error,
        Gen5ComputeSystemRegisters? computeSystemRegisters = null)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, instructions),
            userData,
            null,
            computeSystemRegisters);
        return Gen5ShaderScalarEvaluator.TryEvaluate(
            ctx,
            state,
            out evaluation,
            out error);
    }

    private static uint[] CreateUserData() =>
        Enumerable.Range(0, 32)
            .Select(index => 0x1000_0000u + (uint)index)
            .ToArray();

    private static Gen5ShaderInstruction Branch(
        uint pc,
        string opcode,
        uint targetPc)
    {
        var delta = (long)targetPc - pc - sizeof(uint);
        Assert.Equal(0, delta % sizeof(uint));
        var offset = checked((short)(delta / sizeof(uint)));
        return Instruction(
            pc,
            Gen5ShaderEncoding.Sopp,
            opcode,
            words: [unchecked((ushort)offset)]);
    }

    private static Gen5ShaderInstruction Image(
        uint pc,
        uint scalarResource,
        uint scalarSampler) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            "ImageSampleLz",
            [0],
            [],
            [],
            new Gen5ImageControl(
                1,
                0,
                [0],
                0,
                scalarResource,
                scalarSampler,
                2,
                false,
                false,
                false,
                false,
                false));

    private static Gen5ShaderInstruction MoveLiteral(
        uint pc,
        uint destination,
        uint value) =>
        Instruction(
            pc,
            Gen5ShaderEncoding.Sop1,
            "SMovB32",
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, value)],
            [Gen5Operand.Scalar(destination)]);

    private static Gen5ShaderInstruction VectorMoveLiteral(
        uint pc,
        uint destination,
        uint value) =>
        Instruction(
            pc,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, value)],
            [Gen5Operand.Vector(destination)]);

    private static Gen5ShaderInstruction ImageMip(uint pc, string opcode) =>
        new(
            pc,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [0],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Scalar(0),
                Gen5Operand.Scalar(16),
            ],
            opcode == "ImageStoreMip" ? [] : [Gen5Operand.Vector(4)],
            new Gen5ImageControl(
                1,
                0,
                [0, 1, 2],
                4,
                0,
                16,
                2,
                false,
                false,
                false,
                false,
                false));

    private static Gen5ShaderInstruction End(uint pc) =>
        Instruction(pc, Gen5ShaderEncoding.Sopp, "SEndpgm");

    private static Gen5ShaderInstruction Instruction(
        uint pc,
        Gen5ShaderEncoding encoding,
        string opcode,
        IReadOnlyList<Gen5Operand>? sources = null,
        IReadOnlyList<Gen5Operand>? destinations = null,
        IReadOnlyList<uint>? words = null) =>
        new(
            pc,
            encoding,
            opcode,
            words ?? [0],
            sources ?? [],
            destinations ?? [],
            null);
}

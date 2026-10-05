// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5MoveRelativeOffsetsTests
{
    private const uint M0 = 124;
    private const uint Five = 133;
    private const uint Seven = 135;

    private readonly List<Gen5ShaderInstruction> _program = [];
    private uint _pc;

    private uint Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] destinations, Gen5Operand[] sources, uint word = 0)
    {
        var pc = _pc;
        _program.Add(new Gen5ShaderInstruction(pc, encoding, opcode, [word], sources, destinations, null));
        _pc += 4;
        return pc;
    }

    private uint Scalar(string opcode, uint destination, params Gen5Operand[] sources) =>
        Add(opcode, Gen5ShaderEncoding.Sop2, [Gen5Operand.Scalar(destination)], sources);

    private uint MoveRelative() =>
        Add("VMovrelsB32", Gen5ShaderEncoding.Vop1, [Gen5Operand.Vector(40)], [Gen5Operand.Vector(4), Gen5Operand.Scalar(M0)]);

    private uint Branch(string opcode, uint targetPc) =>
        Add(opcode, Gen5ShaderEncoding.Sopp, [], [], 0xBF820000u | (ushort)(short)((int)(targetPc - _pc - 4) / 4));

    private IReadOnlyDictionary<uint, uint[]> Analyze() =>
        Gen5MoveRelativeOffsets.Analyze(new Gen5ShaderProgram(0x1000, _program));

    private static Gen5Operand Constant(uint encoded) => new(Gen5OperandKind.EncodedConstant, encoded);

    [Fact]
    public void BitFieldTimesAConstant_GivesEveryMultiple()
    {
        Scalar("SBfeU32", 106, Gen5Operand.Scalar(32), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x0003000C));
        Scalar("SMulI32", 106, Gen5Operand.Scalar(106), Constant(Five));
        Scalar("SMovB32", M0, Gen5Operand.Scalar(106));
        var move = MoveRelative();

        Assert.Equal(new uint[] { 0, 5, 10, 15, 20, 25, 30, 35 }, Analyze()[move]);
    }

    [Fact]
    public void MaskedValues_AreTheSubsetsOfTheMask()
    {
        Scalar("SAndB32", 12, Gen5Operand.Scalar(32), Constant(Seven));
        Scalar("SMovB32", M0, Gen5Operand.Scalar(12));
        var move = MoveRelative();

        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5, 6, 7 }, Analyze()[move]);
    }

    [Fact]
    public void UserDataOrLoadedValues_StayUnbounded()
    {
        Scalar("SMovB32", M0, Gen5Operand.Scalar(30));
        var move = MoveRelative();

        Assert.False(Analyze().ContainsKey(move));
    }

    [Fact]
    public void AVectorCompareInBetween_ClobbersVcc()
    {
        Scalar("SAndB32", 106, Gen5Operand.Scalar(32), Constant(Seven));
        Add("VCmpLtF32", Gen5ShaderEncoding.Vopc, [], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)]);
        Scalar("SMovB32", M0, Gen5Operand.Scalar(106));
        var move = MoveRelative();

        Assert.False(Analyze().ContainsKey(move));
    }

    [Fact]
    public void AJoinReachedFromOneBranchOnly_FollowsThatBranch()
    {
        Scalar("SAndB32", 106, Gen5Operand.Scalar(32), Constant(Seven));
        var branchIndex = _program.Count;
        Branch("SCbranchScc0", 0); // patched below
        Scalar("SMovB32", 106, Gen5Operand.Scalar(40));
        Branch("SBranch", 0); // patched below
        var join = _pc;
        Scalar("SMovB32", M0, Gen5Operand.Scalar(106));
        var move = MoveRelative();
        var original = _program[branchIndex];
        var offset = (ushort)(short)((int)(join - original.Pc - 4) / 4);
        _program[branchIndex] = original with { Opcode = "SCbranchScc0", Words = [0xBF840000u | offset] };
        // The unconditional branch skips the join, so only the conditional one reaches it.
        var skip = _program[branchIndex + 2];
        _program[branchIndex + 2] = skip with { Words = [0xBF820000u | (ushort)(short)((int)(move + 4 - skip.Pc - 4) / 4)] };

        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5, 6, 7 }, Analyze()[move]);
    }

    [Fact]
    public void AJoinWithAFallThrough_StaysUnbounded()
    {
        Scalar("SAndB32", 106, Gen5Operand.Scalar(32), Constant(Seven));
        var branch = _program.Count;
        Branch("SCbranchScc0", 0);
        Scalar("SMovB32", 106, Gen5Operand.Scalar(40));
        var join = _pc;
        Scalar("SMovB32", M0, Gen5Operand.Scalar(106));
        var move = MoveRelative();
        var original = _program[branch];
        _program[branch] = original with { Words = [0xBF840000u | (ushort)(short)((int)(join - original.Pc - 4) / 4)] };

        Assert.False(Analyze().ContainsKey(move));
    }
}

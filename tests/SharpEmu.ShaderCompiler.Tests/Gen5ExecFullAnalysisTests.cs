// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ExecFullAnalysisTests
{
    private const uint Exec = 126;

    private readonly List<Gen5ShaderInstruction> _program = [];
    private uint _pc;

    private uint Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] destinations, Gen5Operand[] sources, uint word = 0)
    {
        var pc = _pc;
        _program.Add(new Gen5ShaderInstruction(pc, encoding, opcode, [word], sources, destinations, null));
        _pc += 4;
        return pc;
    }

    private uint VectorAdd() =>
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Gen5Operand.Vector(1)], [Gen5Operand.Vector(2), Gen5Operand.Vector(3)]);

    private uint Scalar(string opcode, uint destination, params Gen5Operand[] sources) =>
        Add(opcode, Gen5ShaderEncoding.Sop1, [Gen5Operand.Scalar(destination)], sources);

    private uint Kill() =>
        Add("VCmpxLtF32", Gen5ShaderEncoding.Vopc, [], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)]);

    private uint Branch(string opcode) => Add(opcode, Gen5ShaderEncoding.Sopp, [], [], 0xBF840000u);

    private void PointBranch(uint branchPc, uint targetPc)
    {
        var index = _program.FindIndex(instruction => instruction.Pc == branchPc);
        var original = _program[index];
        var offset = (ushort)(short)((int)(targetPc - branchPc - 4) / 4);
        _program[index] = original with { Words = [(original.Words[0] & 0xFFFF0000u) | offset] };
    }

    private IReadOnlySet<uint> Analyze() => Gen5ExecFullAnalysis.Analyze(new Gen5ShaderProgram(0x1000, _program), wave32: false);

    [Fact]
    public void StraightLineCode_RunsWithAFullExec()
    {
        var first = VectorAdd();
        var second = VectorAdd();

        var full = Analyze();
        Assert.Contains(first, full);
        Assert.Contains(second, full);
    }

    [Fact]
    public void AfterAVectorCompareIntoExec_ExecIsUnknown()
    {
        var before = VectorAdd();
        Kill();
        var after = VectorAdd();

        var full = Analyze();
        Assert.Contains(before, full);
        Assert.DoesNotContain(after, full);
    }

    [Fact]
    public void RestoringASavedFullExec_MakesItFullAgain()
    {
        Scalar("SMovB64", 40, Gen5Operand.Scalar(Exec));
        Kill();
        var masked = VectorAdd();
        Scalar("SMovB64", Exec, Gen5Operand.Scalar(40));
        var restored = VectorAdd();

        var full = Analyze();
        Assert.DoesNotContain(masked, full);
        Assert.Contains(restored, full);
    }

    [Fact]
    public void ACopyOverwrittenBeforeTheRestore_DoesNotCount()
    {
        Scalar("SMovB64", 40, Gen5Operand.Scalar(Exec));
        Kill();
        Scalar("SMovB32", 41, Gen5Operand.Scalar(3));
        Scalar("SMovB64", Exec, Gen5Operand.Scalar(40));
        var after = VectorAdd();

        Assert.DoesNotContain(after, Analyze());
    }

    [Fact]
    public void AJoinWithAPartialPath_IsUnknown()
    {
        var branch = Branch("SCbranchScc0");
        Kill();
        var join = VectorAdd();
        PointBranch(branch, join);

        Assert.DoesNotContain(join, Analyze());
    }

    [Fact]
    public void WqmOfAFullExec_StaysFull()
    {
        Scalar("SWqmB64", Exec, Gen5Operand.Scalar(Exec));
        var after = VectorAdd();

        Assert.Contains(after, Analyze());
    }
}

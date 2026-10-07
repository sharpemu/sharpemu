// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ExecFullAnalysisTests
{
    [Theory]
    [InlineData("SMovB64", false)]
    [InlineData("SOrB64", false)]
    [InlineData("SWqmB64", false)]
    [InlineData("SAndB64", true)]
    [InlineData("SAndn2B64", true)]
    public void InitializedLaneProofRejectsExpansionsButKeepsSubsets(string opcode, bool expected)
    {
        var initialization = VectorAdd();
        if (opcode == "SMovB64") Scalar(opcode, Exec, Gen5Operand.Source(193));
        else Scalar(opcode, Exec, Gen5Operand.Scalar(Exec), Gen5Operand.Source(193));
        var use = VectorAdd();
        var result = Gen5ExecFullAnalysis.AnalyzeInitializedLanes(new Gen5ShaderProgram(0, _program), initialization, false);
        Assert.Equal(expected, result.Contains(use));
    }

    [Fact]
    public void InitializedLaneProofRejectsAJoinThatSkipsInitialization()
    {
        var branch = Branch("SCbranchScc0");
        var initialization = VectorAdd();
        var use = VectorAdd();
        PointBranch(branch, use);
        Assert.DoesNotContain(use, Gen5ExecFullAnalysis.AnalyzeInitializedLanes(new Gen5ShaderProgram(0, _program), initialization, false));
    }

    [Fact]
    public void MatchingExecutionRequiresRestoringTheSavedMask()
    {
        var reference = VectorAdd();
        Scalar("SMovB64", 8, Gen5Operand.Scalar(Exec));
        Kill();
        var restricted = VectorAdd();
        Scalar("SMovB64", Exec, Gen5Operand.Scalar(8));
        var restored = VectorAdd();
        var result = Gen5ExecFullAnalysis.AnalyzeMatchingExecutionLanes(new Gen5ShaderProgram(0, _program), reference, false);
        Assert.DoesNotContain(restricted, result);
        Assert.Contains(restored, result);
    }

    private const uint Exec = 126;

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void QuadExpansionRequiresAQuadClosedInitializationMask(bool initializeWholeQuads, bool expected)
    {
        Kill();
        if (initializeWholeQuads) Scalar("SWqmB64", Exec, Gen5Operand.Scalar(Exec));
        var initialization = VectorAdd();
        Kill();
        Scalar("SWqmB64", Exec, Gen5Operand.Scalar(Exec));
        var use = VectorAdd();
        var program = new Gen5ShaderProgram(0, _program);
        Assert.Equal(expected, Gen5ExecFullAnalysis.AnalyzeInitializedLanes(program, initialization, false).Contains(use));
        Assert.DoesNotContain(use, Gen5ExecFullAnalysis.AnalyzeMatchingExecutionLanes(program, initialization, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QuadInitializationProofRejectsSkippedOrNarrowedWqm(bool skip)
    {
        var branch = skip ? Branch("SCbranchScc0") : uint.MaxValue;
        Scalar("SWqmB64", Exec, Gen5Operand.Scalar(Exec));
        if (!skip) Kill();
        var initialization = VectorAdd();
        if (skip) PointBranch(branch, initialization);
        Kill();
        Scalar("SWqmB64", Exec, Gen5Operand.Scalar(Exec));
        var use = VectorAdd();
        Assert.DoesNotContain(use, Gen5ExecFullAnalysis.AnalyzeInitializedLanes(new Gen5ShaderProgram(0, _program), initialization, false));
    }

    [Theory]
    [InlineData(false, "SAndn2B64", true)]
    [InlineData(true, "SAndn2B64", false)]
    [InlineData(false, "SOrB64", false)]
    public void QuadClosureRetainsOnlySurvivingSubsetMasks(bool overwrite, string operation, bool expected)
    {
        Kill();
        Scalar("SMovB64", 8, Gen5Operand.Scalar(Exec));
        Scalar("SWqmB64", Exec, Gen5Operand.Scalar(Exec));
        if (overwrite) Scalar("SMovB32", 9, Gen5Operand.Scalar(20));
        var initialization = VectorAdd();
        Scalar(operation, 8, Gen5Operand.Scalar(8), Gen5Operand.Scalar(20));
        Scalar("SWqmB64", Exec, Gen5Operand.Scalar(8));
        var use = VectorAdd();
        Assert.Equal(expected, Gen5ExecFullAnalysis.AnalyzeInitializedLanes(new Gen5ShaderProgram(0, _program), initialization, false).Contains(use));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CompareExecutionOnlyPreservesTheSavedScalarMask(bool executionOnly, bool expected)
    {
        var initialization = VectorAdd();
        Scalar("SMovB64", 106, Gen5Operand.Scalar(Exec));
        var pc = Add(executionOnly ? "VCmpxLtF32" : "VCmpLtF32", Gen5ShaderEncoding.Vopc,
            [Gen5Operand.Scalar(106)], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)]);
        var index = _program.FindIndex(instruction => instruction.Pc == pc);
        _program[index] = _program[index] with { Control = new Gen5SdwaControl(6, 0, 6, 6,
            false, false, 0, 0, 0, false, 106) };
        Scalar("SMovB64", Exec, Gen5Operand.Scalar(106));
        var use = VectorAdd();
        var program = new Gen5ShaderProgram(0, _program);
        Assert.Equal(expected, Gen5ExecFullAnalysis.AnalyzeInitializedLanes(program, initialization, false).Contains(use));
        Assert.Equal(expected, Gen5ExecFullAnalysis.AnalyzeMatchingExecutionLanes(program, initialization, false).Contains(use));
        Assert.Equal(expected, Gen5ExecFullAnalysis.Analyze(program, false).Contains(use));
    }

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
    public void AfterAnSdwaVectorCompareIntoExec_ExecIsUnknown()
    {
        var before = VectorAdd();
        var pc = _pc;
        _program.Add(new Gen5ShaderInstruction(
            pc,
            Gen5ShaderEncoding.Vopc,
            "VCmpxNltF32",
            [0u],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            [],
            new Gen5SdwaControl(0, 0, 6, 6, false, false, 0, 0, 0, false, 106)));
        _pc += 4;
        var save = Scalar("SMovB64", 22, Gen5Operand.Scalar(Exec));
        VectorAdd();
        Scalar("SMovB64", Exec, Gen5Operand.Scalar(22));
        var after = VectorAdd();

        var full = Analyze();
        Assert.Contains(before, full);
        Assert.DoesNotContain(save, full);
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

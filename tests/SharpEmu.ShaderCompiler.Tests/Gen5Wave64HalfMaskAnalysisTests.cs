// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Wave64HalfMaskAnalysisTests
{
    private const uint Vcc = 106;
    private const uint Exec = 126;

    private readonly List<Gen5ShaderInstruction> _program = [];
    private uint _pc;

    private uint Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] destinations, Gen5Operand[] sources,
        Gen5InstructionControl? control = null, uint word = 0)
    {
        var pc = _pc;
        _program.Add(new Gen5ShaderInstruction(pc, encoding, opcode, [word], sources, destinations, control));
        _pc += 4;
        return pc;
    }

    private uint CompareToVcc() =>
        Add("VCmpLtU32", Gen5ShaderEncoding.Vopc, [], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)]);

    private uint CompareTo(uint destination) =>
        Add("VCmpLtU32", Gen5ShaderEncoding.Vop3, [Gen5Operand.Scalar(destination)], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            new Gen5Vop3Control(0, 0, 0, false, 0, destination));

    private uint CompareToExec() =>
        Add("VCmpxLtU32", Gen5ShaderEncoding.Vopc, [], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)]);

    private uint Select(uint mask) =>
        Add("VCndmaskB32", Gen5ShaderEncoding.Vop3, [Gen5Operand.Vector(2)], [Gen5Operand.Vector(3), Gen5Operand.Vector(4), Gen5Operand.Scalar(mask)],
            new Gen5Vop3Control(0, 0, 0, false, 0, null));

    private uint Scalar1(string opcode, uint destination, params Gen5Operand[] sources) =>
        Add(opcode, Gen5ShaderEncoding.Sop1, [Gen5Operand.Scalar(destination)], sources);

    private uint Scalar2(string opcode, uint destination, params Gen5Operand[] sources) =>
        Add(opcode, Gen5ShaderEncoding.Sop2, [Gen5Operand.Scalar(destination)], sources);

    private uint Branch(string opcode) => Add(opcode, Gen5ShaderEncoding.Sopp, [], [], word: 0xBF840000u);

    private uint DataShare(string opcode) =>
        Add(opcode, Gen5ShaderEncoding.Ds, opcode.StartsWith("DsRead", StringComparison.Ordinal) ? [Gen5Operand.Vector(5)] : [],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], new Gen5DataShareControl(0, 0, false));

    private uint End() => Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], [], word: 0xBF810000u);

    private void PointBranch(uint branchPc, uint targetPc)
    {
        var index = _program.FindIndex(instruction => instruction.Pc == branchPc);
        var original = _program[index];
        var offset = (ushort)(short)((int)(targetPc - branchPc - 4) / 4);
        _program[index] = original with { Words = [(original.Words[0] & 0xFFFF0000u) | offset] };
    }

    private Gen5Wave64HalfMaskPlan Analyze() =>
        Gen5Wave64HalfMaskAnalysis.Analyze(new Gen5ShaderProgram(0x1000, _program), new HashSet<uint>())
            ?? throw new InvalidOperationException("analysis gave up");

    [Fact]
    public void ACompareReadPerLane_NeedsNoWholeWaveMask()
    {
        var compare = CompareTo(20);
        var select = Select(20);
        End();

        var plan = Analyze();
        Assert.Equal(20u, plan.HalfMaskWrites[compare]);
        Assert.False(plan.ExactPairsBefore.ContainsKey(select));
    }

    [Fact]
    public void ABranchOnVcc_CombinesVccFirst()
    {
        CompareToVcc();
        var branch = Branch("SCbranchVccz");
        End();
        PointBranch(branch, _pc - 4);

        Assert.Equal(new[] { Vcc }, Analyze().ExactPairsBefore[branch]);
    }

    [Fact]
    public void ABranchOnExec_CombinesExecFirst()
    {
        CompareToExec();
        var branch = Branch("SCbranchExecz");
        End();
        PointBranch(branch, _pc - 4);

        Assert.Equal(new[] { Exec }, Analyze().ExactPairsBefore[branch]);
    }

    [Fact]
    public void ABitwiseMaskOperationWithADeadScc_StaysHalf()
    {
        CompareTo(20);
        CompareToVcc();
        var and = Scalar2("SAndB64", 22, Gen5Operand.Scalar(20), Gen5Operand.Scalar(Vcc));
        var select = Select(22);
        End();

        var plan = Analyze();
        Assert.False(plan.ExactPairsBefore.ContainsKey(and));
        Assert.False(plan.ExactPairsBefore.ContainsKey(select));
    }

    [Fact]
    public void ABitwiseMaskOperationWhoseSccIsRead_CombinesItsInputs()
    {
        CompareTo(20);
        CompareToVcc();
        var and = Scalar2("SAndB64", 22, Gen5Operand.Scalar(20), Gen5Operand.Scalar(Vcc));
        var branch = Branch("SCbranchScc0");
        End();
        PointBranch(branch, _pc - 4);

        var plan = Analyze();
        Assert.Equal(new[] { 20u, Vcc }, plan.ExactPairsBefore[and]);
        Assert.False(plan.ExactPairsBefore.ContainsKey(branch));
    }

    [Fact]
    public void AScalarReadOfAMaskHalf_CombinesThePair()
    {
        CompareTo(20);
        var move = Scalar1("SMovB32", 30, Gen5Operand.Scalar(21));
        End();

        Assert.Equal(new[] { 20u }, Analyze().ExactPairsBefore[move]);
    }

    [Fact]
    public void ACombinedMask_IsNotCombinedAgain()
    {
        CompareTo(20);
        var first = Scalar1("SMovB32", 30, Gen5Operand.Scalar(20));
        var second = Scalar1("SMovB32", 31, Gen5Operand.Scalar(21));
        End();

        var plan = Analyze();
        Assert.Equal(new[] { 20u }, plan.ExactPairsBefore[first]);
        Assert.False(plan.ExactPairsBefore.ContainsKey(second));
    }

    [Fact]
    public void ASaveexecCopiesAHalfExec_SoTheRestoreStaysHalf()
    {
        CompareTo(20);
        var save = Scalar1("SAndSaveexecB64", 24, Gen5Operand.Scalar(20));
        Select(Exec);
        var restore = Scalar2("SOrB64", Exec, Gen5Operand.Scalar(Exec), Gen5Operand.Scalar(24));
        var read = Scalar1("SMovB32", 30, Gen5Operand.Scalar(24));
        End();

        var plan = Analyze();
        Assert.False(plan.ExactPairsBefore.ContainsKey(save));
        Assert.False(plan.ExactPairsBefore.ContainsKey(restore));
        Assert.Equal(new[] { 24u }, plan.ExactPairsBefore[read]);
    }

    [Fact]
    public void ALoopExitOnExec_CombinesExecEveryIteration()
    {
        var header = CompareToExec();
        var exit = Branch("SCbranchExecz");
        Select(Exec);
        var back = Branch("SBranch");
        var end = End();
        PointBranch(exit, end);
        PointBranch(back, header);

        var plan = Analyze();
        Assert.Equal(new[] { Exec }, plan.ExactPairsBefore[exit]);
        Assert.Contains(header, plan.HalfMaskWrites.Keys);
    }

    [Fact]
    public void ASharedReadAfterAWrite_WaitsForTheOtherHalf()
    {
        var write = DataShare("DsWriteB32");
        var read = DataShare("DsReadB32");
        var again = DataShare("DsReadB32");
        End();

        var plan = Analyze();
        Assert.DoesNotContain(write, plan.SharedMemoryBarriersBefore);
        Assert.Contains(read, plan.SharedMemoryBarriersBefore);
        Assert.DoesNotContain(again, plan.SharedMemoryBarriersBefore);
    }

    [Fact]
    public void ACombineBarrier_AlsoOrdersSharedMemory()
    {
        DataShare("DsWriteB32");
        CompareTo(20);
        var move = Scalar1("SMovB32", 30, Gen5Operand.Scalar(20));
        var read = DataShare("DsReadB32");
        End();

        var plan = Analyze();
        Assert.True(plan.ExactPairsBefore.ContainsKey(move));
        Assert.DoesNotContain(read, plan.SharedMemoryBarriersBefore);
    }

    [Fact]
    public void ADynamicJump_GivesUp()
    {
        Scalar1("SSetpcB64", 0, Gen5Operand.Scalar(4));
        End();

        Assert.Null(Gen5Wave64HalfMaskAnalysis.Analyze(new Gen5ShaderProgram(0x1000, _program), new HashSet<uint>()));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5VectorObservationAnalysisTests
{
    private const uint Exec = 126;

    private readonly List<Gen5ShaderInstruction> _instructions = [];
    private uint _pc;

    private uint Add(Gen5ShaderInstruction instruction)
    {
        var pc = _pc;
        _instructions.Add(instruction with { Pc = pc });
        _pc += 8;
        return pc;
    }

    private Gen5VectorObservationPlan Analyze()
    {
        Add(EndProgram(0));
        var plan = Gen5VectorObservationAnalysis.Analyze(Program([.. _instructions]), wave32: false);
        Assert.NotNull(plan);
        return plan;
    }

    private void Narrow() => Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));

    private void SaveExec() => Add(Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(Exec)));

    private void RestoreExec() => Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(20)));

    [Fact]
    public void ATemporaryOfANarrowedRegion_IsNotObserved()
    {
        SaveExec();
        Narrow();
        var temporary = Add(Vop2(0, "VAddU32", 3, Operand(1), Gen5Operand.Vector(2)));
        var result = Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(3)));
        RestoreExec();
        Add(Vop2(0, "VAddU32", 5, Operand(1), Gen5Operand.Vector(4)));

        var plan = Analyze();
        Assert.True(plan.UnobservedWrites(temporary).Contains(3));
        Assert.False(plan.UnobservedWrites(result).Contains(4));
    }

    [Fact]
    public void ACrossLaneReadObservesInactiveLanes()
    {
        Narrow();
        var write = Add(Vop2(0, "VAddU32", 3, Operand(1), Gen5Operand.Vector(2)));
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u], [Gen5Operand.Vector(3)], [Gen5Operand.Vector(4)],
            new Gen5DppControl(0x111, false, false, 0, 0, 0xF, 0xF)));

        Assert.False(Analyze().UnobservedWrites(write).Contains(3));
    }

    [Fact]
    public void AFullWriteUnderAFullExecEndsTheObservation()
    {
        SaveExec();
        Narrow();
        var write = Add(Vop2(0, "VAddU32", 3, Operand(1), Gen5Operand.Vector(2)));
        RestoreExec();
        Add(MoveVector(0, 3, 9));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(3)));

        Assert.True(Analyze().UnobservedWrites(write).Contains(3));
    }

    [Fact]
    public void AnAccumulatingWriteReadsItsDestination()
    {
        SaveExec();
        Narrow();
        var write = Add(Vop2(0, "VAddU32", 3, Operand(1), Gen5Operand.Vector(2)));
        RestoreExec();
        Add(Vop2(0, "VMacF32", 3, Gen5Operand.Vector(1), Gen5Operand.Vector(2)));

        Assert.False(Analyze().UnobservedWrites(write).Contains(3));
    }

    [Fact]
    public void ARelativeMoveDisablesThePlan()
    {
        Add(Vop1(0, "VMovrelsB32", 3, Gen5Operand.Vector(4)));
        Add(EndProgram(0));

        Assert.Null(Gen5VectorObservationAnalysis.Analyze(Program([.. _instructions]), wave32: false));
    }
}

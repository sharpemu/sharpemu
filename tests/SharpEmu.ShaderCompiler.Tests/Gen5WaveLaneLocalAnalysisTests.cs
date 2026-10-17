// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Ir;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5WaveLaneLocalAnalysisTests
{
    private const uint Exec = 126;
    private static readonly Gen5Operand AllLanes = Gen5Operand.Source(193);

    private readonly List<Gen5ShaderInstruction> _instructions = [];
    private uint _pc;

    private uint Add(Gen5ShaderInstruction instruction)
    {
        var pc = _pc;
        _instructions.Add(instruction with { Pc = pc });
        _pc += 8;
        return pc;
    }

    private void Point(uint branchPc, uint targetPc)
    {
        var index = _instructions.FindIndex(instruction => instruction.Pc == branchPc);
        var offset = (short)(((int)targetPc - (int)branchPc - 4) / 4);
        _instructions[index] = _instructions[index] with { Words = [unchecked((uint)(ushort)offset)] };
    }

    private void Lane()
    {
        Add(Vop2(0, "VLshlrevB32", 2, Operand(3), Gen5Operand.Vector(1)));
        Add(Vop2(0, "VAddI32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(2)));
    }

    private void CompareTo(string opcode, uint destination, Gen5Operand left, Gen5Operand right) =>
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3, opcode, [0u, 0u], [left, right], [Gen5Operand.Scalar(destination)],
            new Gen5Vop3Control(0, 0, 0, false, 0, destination)));

    private void SkipUnlessActive(Action body)
    {
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        body();
        var join = Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Point(skip, join);
    }

    private bool Analyze(out string reason, params uint[] sharedFlatPcs)
    {
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(2)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        return Gen5WaveLaneLocalAnalysis.IsLaneLocal(Program([.. _instructions]), sharedFlatPcs.ToHashSet(), out reason);
    }

    private void AssertRefused(string expected)
    {
        Assert.False(Analyze(out var reason));
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LaneArithmetic_IsLaneLocal()
    {
        Lane();
        Add(Vop2(0, "VAddU32", 4, Operand(100), Gen5Operand.Vector(2)));

        Assert.True(Analyze(out _));
    }

    [Fact]
    public void ACompareSelectingLanes_IsLaneLocal()
    {
        Lane();
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(2), Operand(40));
        Add(Vop3(0, "VCndmaskB32", 4, Operand(0), Operand(1000), Gen5Operand.Scalar(10)));

        Assert.True(Analyze(out _));
    }

    [Fact]
    public void ReadingAnotherLane_IsRefused()
    {
        Lane();
        Add(ReadLane(0, 20, 2, 45));
        Add(MoveVectorFromScalar(0, 4, 20));

        AssertRefused("V_READLANE");
    }

    [Fact]
    public void CountingTheLanesOfAMask_IsRefused()
    {
        Lane();
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(Exec)));
        Add(MoveVectorFromScalar(0, 4, 14));

        AssertRefused("a lane mask used as a scalar value");
    }

    [Fact]
    public void BroadcastingAMaskToTheLanes_IsRefused()
    {
        Lane();
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(2), Operand(40));
        Add(MoveVectorFromScalar(0, 4, 10));

        AssertRefused("a lane mask broadcast to the lanes");
    }

    [Fact]
    public void SharedMemory_IsRefused()
    {
        Lane();
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(2), Gen5Operand.Vector(2)], []));

        AssertRefused("LDS");
    }

    [Fact]
    public void ExecFromALiteralMask_IsRefused()
    {
        Lane();
        Add(Sop1(0, "SMovB64", Exec, Operand(0xFFFF)));

        AssertRefused("EXEC set from the wave's layout");
    }

    [Fact]
    public void AnExecSkipAroundLaneWork_IsLaneLocal()
    {
        Lane();
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        SkipUnlessActive(() => Add(MoveVector(0, 4, 77)));

        Assert.True(Analyze(out _));
    }

    [Fact]
    public void ScalarStateWrittenInAnExecSkip_IsRefused()
    {
        Lane();
        Add(MoveScalar(0, 20, 5));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        SkipUnlessActive(() => Add(MoveScalar(0, 20, 77)));
        Add(MoveVectorFromScalar(0, 4, 20));

        AssertRefused("scalar state escapes a lane-mask branch");
    }

    [Fact]
    public void AMaskKeptThroughItsComplementInAnExecSkip_IsLaneLocal()
    {
        Lane();
        Add(Sop1(0, "SMovB64", 2, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(Sop1(0, "SMovB64", 8, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpNeU32", Operand(0), 2));
        Add(Sop2(0, "SOrn2B64", 106, Gen5Operand.Scalar(106), Gen5Operand.Scalar(8)));
        Add(Sop2(0, "SAndB64", 2, Gen5Operand.Scalar(2), Gen5Operand.Scalar(106)));
        var join = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(2)));
        Point(skip, join);

        Assert.True(Analyze(out var reason), reason);
    }

    [Fact]
    public void AMaskChangedInAnExecSkip_IsRefused()
    {
        Lane();
        Add(Sop1(0, "SMovB64", 2, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(Sop1(0, "SMovB64", 8, Gen5Operand.Scalar(Exec)));
        Add(Sop2(0, "SOrB64", 2, Gen5Operand.Scalar(2), Gen5Operand.Scalar(8)));
        Add(Sop1(0, "SNotB64", 2, Gen5Operand.Scalar(2)));
        var join = Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Point(skip, join);
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(2)));

        AssertRefused("scalar state escapes a lane-mask branch");
    }

    [Fact]
    public void AWideMultiplyOfAScalarBesideAMask_IsLaneLocal()
    {
        Lane();
        Add(Sop2(0, "SCselectB64", 10, Gen5Operand.Scalar(Exec), Operand(0)));
        Add(Vop3(0, "VMadU64U32", 4, Gen5Operand.Scalar(9), Gen5Operand.Vector(2), Operand(0)));

        Assert.True(Analyze(out var reason), reason);
    }

    [Fact]
    public void ABarrierInAnExecSkip_IsRefused()
    {
        Lane();
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        SkipUnlessActive(() => Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null)));

        AssertRefused("a barrier");
    }

    [Fact]
    public void ReadFirstLaneOfAUniformValue_IsLaneLocal()
    {
        Lane();
        Add(MoveScalar(0, 20, 9));
        Add(MoveVectorFromScalar(0, 3, 20));
        Add(ReadFirstLane(0, 21, 3));
        Add(MoveVectorFromScalar(0, 4, 21));

        Assert.True(Analyze(out _));
    }

    [Fact]
    public void ReadFirstLaneOfALaneValue_IsRefused()
    {
        Lane();
        Add(ReadFirstLane(0, 21, 2));
        Add(MoveVectorFromScalar(0, 4, 21));

        AssertRefused("outside a waterfall loop");
    }

    [Fact]
    public void AWaterfallLoop_IsLaneLocal()
    {
        Lane();
        Add(Vop2(0, "VAndB32", 3, Operand(3), Gen5Operand.Vector(2)));
        Add(Sop1(0, "SMovB64", 30, Gen5Operand.Scalar(Exec)));
        var loop = Add(ReadFirstLane(0, 20, 3));
        CompareTo("VCmpEqU32", 22, Gen5Operand.Vector(3), Gen5Operand.Scalar(20));
        Add(Sop1(0, "SAndSaveexecB64", 24, Gen5Operand.Scalar(22)));
        Add(MoveVectorFromScalar(0, 4, 20));
        Add(Sop2(0, "SXorB64", Exec, Gen5Operand.Scalar(24), Gen5Operand.Scalar(Exec)));
        var back = Add(Branch(0, "SCbranchExecnz", 0));
        Point(back, loop);
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(30)));

        Assert.True(Analyze(out var reason), reason);
    }

    [Theory]
    [InlineData(0x111u, true)]
    [InlineData(0x141u, true)]
    [InlineData(0x142u, false)]
    [InlineData(0x143u, false)]
    public void Dpp_IsLaneLocalOnlyWithinARow(uint control, bool laneLocal)
    {
        Lane();
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop1, "VMovB32", [0u, 0u], [Gen5Operand.Vector(2)], [Gen5Operand.Vector(4)],
            new Gen5DppControl(control, false, false, 0, 0, 0xF, 0xF)));

        Assert.Equal(laneLocal, Analyze(out var reason));
        Assert.True(laneLocal || reason.Contains("cross-row DPP", StringComparison.Ordinal));
    }
}

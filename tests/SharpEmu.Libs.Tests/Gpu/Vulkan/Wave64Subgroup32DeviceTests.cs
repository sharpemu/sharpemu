// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class Wave64Subgroup32DeviceTests(HeadlessVulkanFixture fixture) : Wave64HalfMaskDeviceTests(fixture)
{
    protected override bool HostSubgroup32 => true;

    [Fact]
    public void LaneLocalArithmetic_UsesPairedLanesUnlessHalfWavesAreEnabled()
    {
        if (!Ready()) return;
        Lane(rowShift: 4);
        Add(Vop2(0, "VAddU32", 4, Operand(17), Gen5Operand.Vector(2)));

        var lanes = Run(sizeX: 16, sizeY: 16, laneLocal: true);
        for (uint index = 0; index < 256; index++)
        {
            Assert.Equal(index + 17, lanes[index]);
        }
    }

    [Theory]
    [InlineData(45u)]
    [InlineData(7u)]
    public void ReadLane_ReadsEitherHalfOfTheWave(uint lane)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAddU32", 3, Operand(100), Gen5Operand.Vector(2)));
        Add(ReadLane(0, 20, 3, lane));
        Add(MoveVectorFromScalar(0, 4, 20));

        Assert.All(Run(laneLocal: false), value => Assert.Equal(100 + lane, value));
    }

    [Fact]
    public void WriteLane_WritesOneLaneOfTheUpperHalf()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 1));
        Add(MoveScalar(0, 20, 9));
        Add(WriteLane(0, 4, 20, 50));

        var lanes = Run(laneLocal: false);
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane == 50 ? 9u : 1u, lanes[lane]);
        }
    }

    [Fact]
    public void EveryWaveOfALargeGroup_CountsItsOwnLanes()
    {
        if (!Ready()) return;
        Lane(rowShift: 4);
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(2), Operand(100));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(10)));
        Add(MoveVectorFromScalar(0, 4, 14));

        var lanes = Run(sizeX: 16, sizeY: 16, laneLocal: false);
        for (uint index = 0; index < 256; index++)
        {
            var wave = index / 64;
            Assert.Equal(wave == 0 ? 64u : wave == 1 ? 36u : 0u, lanes[index]);
        }
    }

    [Fact]
    public void APartialWave_HasOnlyItsThreadsInExec()
    {
        if (!Ready()) return;
        Lane();
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(Exec)));
        Add(MoveVectorFromScalar(0, 4, 14));

        Assert.All(Run(sizeX: 8, sizeY: 6, laneLocal: false), value => Assert.Equal(48u, value));
    }

    [Fact]
    public void TwoWaves_ExchangeThroughSharedMemoryAfterABarrier()
    {
        if (!Ready()) return;
        Lane(rowShift: 4);
        Add(Vop2(0, "VAddU32", 3, Operand(1), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(2)));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(3)], []));
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
        Add(Vop2(0, "VSubU32", 7, Operand(127), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VLshlrevB32", 7, Operand(2), Gen5Operand.Vector(7)));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(7)], [4]));

        var lanes = Run(localDataShareDwords: 128, sizeX: 16, sizeY: 8, laneLocal: false);
        for (uint index = 0; index < 128; index++)
        {
            Assert.Equal(128 - index, lanes[index]);
        }
    }

    [Fact]
    public void EveryHalfOfALargeGroup_SelectsItsOwnLanes()
    {
        if (!Ready()) return;
        Lane(rowShift: 4);
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(2), Operand(100));
        Add(Vop3(0, "VCndmaskB32", 4, Operand(0), Operand(1000), Gen5Operand.Scalar(10)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(2), Gen5Operand.Vector(4)));

        var lanes = Run(sizeX: 16, sizeY: 16, laneLocal: true);
        for (uint index = 0; index < 256; index++)
        {
            Assert.Equal(index < 100 ? 1000 + index : index, lanes[index]);
        }
    }

    [Fact]
    public void APartialLastWave_SelectsOnlyItsThreads()
    {
        if (!Ready()) return;
        Lane(rowShift: 4);
        CompareTo("VCmpGeU32", 10, Gen5Operand.Vector(2), Operand(40));
        Add(Vop3(0, "VCndmaskB32", 4, Operand(0), Operand(2000), Gen5Operand.Scalar(10)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(2), Gen5Operand.Vector(4)));

        var lanes = Run(sizeX: 16, sizeY: 5, laneLocal: true);
        for (uint index = 0; index < 80; index++)
        {
            Assert.Equal(index >= 40 ? 2000 + index : index, lanes[index]);
        }
    }

    [Theory]
    [InlineData(48u)]
    [InlineData(8u)]
    [InlineData(64u)]
    public void AnExecSkip_LeavesTheInactiveLanesOfEachHalf(uint firstActiveLane)
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 5));
        Add(Vopc(0, "VCmpxLeU32", Operand(firstActiveLane), 2));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(MoveVector(0, 4, 77));
        var join = Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Point(skip, join);

        var lanes = Run(laneLocal: true);
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane >= firstActiveLane ? 77u : 5u, lanes[lane]);
        }
    }

    [Fact]
    public void AWaterfallLoop_VisitsEveryValueOfEveryHalf()
    {
        if (!Ready()) return;
        Lane(rowShift: 4);
        Add(Vop2(0, "VAndB32", 3, Operand(3), Gen5Operand.Vector(2)));
        Add(Sop1(0, "SMovB64", 30, Gen5Operand.Scalar(Exec)));
        var loop = Add(ReadFirstLane(0, 20, 3));
        CompareTo("VCmpEqU32", 22, Gen5Operand.Vector(3), Gen5Operand.Scalar(20));
        Add(Sop1(0, "SAndSaveexecB64", 24, Gen5Operand.Scalar(22)));
        Add(MoveVectorFromScalar(0, 4, 20));
        Add(Vop2(0, "VAddU32", 4, Operand(100), Gen5Operand.Vector(4)));
        Add(Sop2(0, "SXorB64", Exec, Gen5Operand.Scalar(24), Gen5Operand.Scalar(Exec)));
        var back = Add(Branch(0, "SCbranchExecnz", 0));
        Point(back, loop);
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(30)));

        var lanes = Run(sizeX: 16, sizeY: 16, laneLocal: true);
        for (uint index = 0; index < 256; index++)
        {
            Assert.Equal((index & 3) + 100, lanes[index]);
        }
    }
}

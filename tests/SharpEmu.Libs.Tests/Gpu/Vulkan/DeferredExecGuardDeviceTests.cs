// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Ir;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// Wave32 programs whose vector writes under a partial EXEC are merged once per EXEC region: every lane EXEC leaves
// out must still show its old value afterwards, and to any instruction that reads another lane inside the region.
public sealed class DeferredExecGuardDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
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

    private void Point(uint branchPc, uint targetPc)
    {
        var index = _instructions.FindIndex(instruction => instruction.Pc == branchPc);
        var offset = (short)(((int)targetPc - (int)branchPc - 4) / 4);
        _instructions[index] = _instructions[index] with { Words = [unchecked((uint)(ushort)offset)] };
    }

    // v2 = the thread's index in the 8 x 8 group: lanes 0-31 are the first wave, 32-63 the second.
    private void Lane()
    {
        Add(Vop2(0, "VLshlrevB32", 2, Operand(3), Gen5Operand.Vector(1)));
        Add(Vop2(0, "VAddI32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(2)));
    }

    // waveSize 64: one wave of 64 lanes on a 32-lane host, which runs a lane-local program as two independent half waves.
    private uint[] Run(uint waveSize = 32)
    {
        const uint threads = 64;
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(2)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var program = Program([.. _instructions]);
        if (waveSize == 64)
        {
            Assert.True(Gen5WaveLaneLocalAnalysis.IsLaneLocal(program, new HashSet<uint>(), out var reason), reason);
        }

        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, ThreadCountX = 8, ThreadCountY = 8, WaveSize = waveSize, HostComputeSubgroup32 = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotNull(fixture.Vulkan);
        using var harness = new ImageTestHarness(fixture.Vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(threads * 4);
        var registers = new uint[256];
        registers[6] = threads * 4;
        var buffers = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] };
        harness.Run(() => runner.Dispatch(registers, buffers, 1));
        var bytes = runner.ReadBack(output, 0, threads * 4);
        harness.AssertNoValidationMessages();
        var lanes = new uint[threads];
        for (var lane = 0; lane < lanes.Length; lane++)
        {
            lanes[lane] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lane * sizeof(uint)));
        }

        return lanes;
    }

    private bool Ready() => GatePrerequisites.Ready(fixture.Vulkan, shaderInt64: true);

    [Fact]
    public void RepeatedWritesInOneRegion_LeaveTheInactiveLanesTheirOldValues()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 7));
        Add(MoveVector(0, 3, 9));
        Add(Sop1(0, "SMovB32", 20, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        Add(Vop2(0, "VAddU32", 4, Operand(64), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(4)));
        Add(Vop2(0, "VAddU32", 3, Operand(2), Gen5Operand.Vector(4)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(3), Gen5Operand.Vector(4)));
        Add(Sop1(0, "SMovB32", Exec, Gen5Operand.Scalar(20)));
        Add(Vop2(0, "VLshlrevB32", 6, Operand(16), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(6), Gen5Operand.Vector(4)));

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
        {
            var expected = lane >= 40 ? 2 * lane + 132 + ((lane + 67) << 16) : 7u + (9u << 16);
            Assert.Equal(expected, lanes[lane]);
        }
    }

    [Fact]
    public void ALaneReadInsideARegion_SeesTheOldValueOfAnInactiveLane()
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAddU32", 4, Operand(100), Gen5Operand.Vector(2)));
        Add(MoveVector(0, 7, 0));
        Add(Vop2(0, "VAndB32", 6, Operand(31), Gen5Operand.Vector(2)));
        Add(Sop1(0, "SMovB32", 20, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLtU32", Operand(0), 6));
        Add(Vop2(0, "VAddU32", 4, Operand(1000), Gen5Operand.Vector(4)));
        Add(ReadLane(0, 21, 4, 0));
        Add(MoveVectorFromScalar(0, 7, 21));
        Add(Sop1(0, "SMovB32", Exec, Gen5Operand.Scalar(20)));
        Add(Vop2(0, "VLshlrevB32", 7, Operand(16), Gen5Operand.Vector(7)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(7), Gen5Operand.Vector(4)));

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
        {
            var firstOfWave = lane & ~31u;
            var expected = (lane & 31) == 0 ? lane + 100 : lane + 1100 + ((firstOfWave + 100) << 16);
            Assert.Equal(expected, lanes[lane]);
        }
    }

    [Fact]
    public void ARegionSplitByABranch_StillKeepsTheInactiveLanes()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 7));
        Add(Sop1(0, "SMovB32", 20, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        Add(Vop2(0, "VAddU32", 4, Operand(64), Gen5Operand.Vector(2)));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(4)));
        var join = Add(Sop1(0, "SMovB32", Exec, Gen5Operand.Scalar(20)));
        Point(skip, join);

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane >= 40 ? lane + 65 : 7u, lanes[lane]);
        }
    }
    // The same regions in a wave64 program: its two halves each merge their own inactive lanes.
    [Fact]
    public void HalfWaves_RepeatedWritesInOneRegion_LeaveTheInactiveLanesTheirOldValues()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 7));
        Add(MoveVector(0, 3, 9));
        Add(Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        Add(Vop2(0, "VAddU32", 4, Operand(64), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(4)));
        Add(Vop2(0, "VAddU32", 3, Operand(2), Gen5Operand.Vector(4)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(3), Gen5Operand.Vector(4)));
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(20)));
        Add(Vop2(0, "VLshlrevB32", 6, Operand(16), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(6), Gen5Operand.Vector(4)));

        var lanes = Run(waveSize: 64);
        for (uint lane = 0; lane < 64; lane++)
        {
            var expected = lane >= 40 ? 2 * lane + 132 + ((lane + 67) << 16) : 7u + (9u << 16);
            Assert.Equal(expected, lanes[lane]);
        }
    }

    [Fact]
    public void HalfWaves_ARegionSplitByABranch_StillKeepsTheInactiveLanes()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 7));
        Add(Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        Add(Vop2(0, "VAddU32", 4, Operand(64), Gen5Operand.Vector(2)));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(4)));
        var join = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(20)));
        Point(skip, join);

        var lanes = Run(waveSize: 64);
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane >= 40 ? lane + 65 : 7u, lanes[lane]);
        }
    }
}

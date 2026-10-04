// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class Wave64HalfMaskDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Vcc = 106;
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

    private uint CompareTo(string opcode, uint destination, Gen5Operand left, Gen5Operand right) =>
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3, opcode, [0u, 0u], [left, right], [Gen5Operand.Scalar(destination)],
            new Gen5Vop3Control(0, 0, 0, false, 0, destination)));

    private uint[] Run(uint localDataShareDwords = 0)
    {
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(2)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(
            Program([.. _instructions]),
            waveSize: 64);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, ThreadCountX = 8, ThreadCountY = 8, WaveSize = 64,
            LocalDataShareDwords = localDataShareDwords,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotNull(fixture.Vulkan);
        using var harness = new ImageTestHarness(fixture.Vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var bytes = runner.ReadBack(output, 0, 256);
        harness.AssertNoValidationMessages();
        var lanes = new uint[64];
        for (var lane = 0; lane < lanes.Length; lane++)
        {
            lanes[lane] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(lane * sizeof(uint)));
        }

        return lanes;
    }

    [Fact]
    public void ExplicitCompareMask_IsCollectedAcrossTheWave()
    {
        if (!Ready()) return;
        Lane();
        CompareTo("VCmpGeU32", 10, Gen5Operand.Vector(2), Operand(3));
        Add(MoveVectorFromScalar(0, 4, 10));
        Assert.All(Run(), x => Assert.Equal(0xFFFF_FFF8u, x));
    }

    [Fact]
    public void InitialExecContainsEveryLane()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVectorFromScalar(0, 4, 126));
        Assert.All(Run(), x => Assert.Equal(uint.MaxValue, x));
    }

    private bool Ready() => GatePrerequisites.Ready(fixture.Vulkan, shaderInt64: true);

    [Fact]
    public void MasksAcrossTheHalfBoundary_StayExactPerLaneAndWhenCounted()
    {
        if (!Ready()) return;
        Lane();
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(2), Operand(40));
        Add(Vop2(0, "VAndB32", 6, Operand(1), Gen5Operand.Vector(2)));
        Add(Vopc(0, "VCmpEqU32", Operand(1), 6));
        Add(Sop2(0, "SAndB64", 12, Gen5Operand.Scalar(10), Gen5Operand.Scalar(Vcc)));
        Add(Vop3(0, "VCndmaskB32", 4, Operand(0), Operand(1000), Gen5Operand.Scalar(12)));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(12)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Scalar(14), Gen5Operand.Vector(4)));

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal((lane & 1) != 0 && lane < 40 ? 1020u : 20u, lanes[lane]);
        }
    }

    [Theory]
    [InlineData(48u, 77u)]
    [InlineData(8u, 77u)]
    [InlineData(64u, 5u)]
    public void AnExecBranch_IsDecidedForTheWholeWave(uint firstActiveLane, uint expected)
    {
        if (!Ready()) return;
        Lane();
        Add(MoveScalar(0, 20, 5));
        Add(Vopc(0, "VCmpxLeU32", Operand(firstActiveLane), 2));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(MoveScalar(0, 20, 77));
        var join = Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Point(skip, join);
        Add(MoveVectorFromScalar(0, 4, 20));

        Assert.All(Run(), value => Assert.Equal(expected, value));
    }

    [Theory]
    [InlineData(50u, 9u)]
    [InlineData(10u, 9u)]
    [InlineData(64u, 3u)]
    public void AMaskWhoseSccIsRead_CountsBothHalves(uint threshold, uint expected)
    {
        if (!Ready()) return;
        Lane();
        Add(MoveScalar(0, 20, 3));
        CompareTo("VCmpGeU32", 10, Gen5Operand.Vector(2), Operand(threshold));
        Add(Sop2(0, "SAndB64", 12, Gen5Operand.Scalar(10), Gen5Operand.Scalar(Exec)));
        var skip = Add(Branch(0, "SCbranchScc0", 0));
        Add(MoveScalar(0, 20, 9));
        var join = Add(MoveVectorFromScalar(0, 4, 20));
        Point(skip, join);

        Assert.All(Run(), value => Assert.Equal(expected, value));
    }

    [Theory]
    [InlineData(32u)]
    [InlineData(64u)]
    public void SharedMemoryWrittenByOneHalf_IsReadByTheOther(uint localDataShareDwords)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VLshlrevB32", 3, Operand(1), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddU32", 3, Gen5Operand.Vector(2), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(2)));
        Add(Vopc(0, "VCmpxGtU32", Operand(32), 2));
        Add(BufferAccess(0, "BufferLoadDword", 4, vectorData: 8, offsetEnabled: true, vectorAddress: 6));
        Add(Vop2(0, "VAddU32", 3, Gen5Operand.Vector(8), Gen5Operand.Vector(3)));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(3)], []));
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(Vop2(0, "VAndB32", 7, Operand(31), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VLshlrevB32", 7, Operand(2), Gen5Operand.Vector(7)));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(7)], [4]));

        var lanes = Run(localDataShareDwords);
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal((lane & 31) * 3, lanes[lane]);
        }
    }

    [Fact]
    public void AnExecLoop_RunsEveryLaneItsOwnCount()
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAndB32", 3, Operand(7), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddU32", 3, Operand(1), Gen5Operand.Vector(3)));
        Add(MoveVector(0, 4, 0));
        Add(Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(Exec)));
        var header = Add(Vopc(0, "VCmpxGtU32", Gen5Operand.Vector(3), 4));
        var exit = Add(Branch(0, "SCbranchExecz", 0));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(4)));
        var back = Add(Branch(0, "SBranch", 0));
        var done = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(20)));
        Point(exit, done);
        Point(back, header);

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal((lane & 7) + 1, lanes[lane]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASingleBlockLoop_GroupsBothWaveHalvesAndCanBeSkipped(bool skip)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAndB32", 3, Operand(3), Gen5Operand.Vector(2)));
        Add(MoveVector(0, 4, 0));
        Add(Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(Exec)));
        uint? bypass = skip ? Add(Branch(0, "SBranch", 0)) : null;
        var header = Add(ReadFirstLane(0, 22, 3));
        Add(Vopc(0, "VCmpxEqU32", Gen5Operand.Scalar(22), 3));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(3)));
        Add(Sop2(0, "SAndn2B64", 20, Gen5Operand.Scalar(20), Gen5Operand.Scalar(Exec)));
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(20)));
        var back = Add(Branch(0, "SCbranchScc1", 0));
        var done = Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Point(back, header);
        if (bypass is { } branch) Point(branch, done);

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
            Assert.Equal(skip ? 0u : (lane & 3) + 1, lanes[lane]);
    }

    [Theory]
    [InlineData(40u, 140u)]
    [InlineData(3u, 103u)]
    [InlineData(64u, 100u)]
    public void ReadFirstLane_TakesTheFirstActiveLaneOfTheWholeWave(uint firstActiveLane, uint expected)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAddU32", 3, Operand(100), Gen5Operand.Vector(2)));
        Add(Vopc(0, "VCmpxLeU32", Operand(firstActiveLane), 2));
        Add(ReadFirstLane(0, 20, 3));
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(MoveVectorFromScalar(0, 4, 20));

        Assert.All(Run(), value => Assert.Equal(expected, value));
    }

    [Fact]
    public void ADemonsSoulsIndexRemap_CompilesOnTheDevice()
    {
        if (!Ready()) return;
        uint[] words =
        [
            0xBEFC03FFu, 0xA65CD91Eu, 0xBF960000u, 0xBFA00003u, 0xD7460002u, 0x04010C0Cu, 0xF4201A84u, 0xFA000000u,
            0xBF8CC07Fu, 0x7DA8046Au, 0xBF88002Au, 0xF4200304u, 0xFA000004u, 0xBF8CC07Fu, 0x7E000C0Cu, 0xBF070C80u,
            0x8588807Eu, 0x7E005700u, 0x100000FFu, 0x4F800000u, 0x7E060F00u, 0xD5766A00u, 0x0202060Cu, 0x7D8A0280u,
            0x4C020080u, 0x02000101u, 0xD56A0001u, 0x00020700u, 0x4C000303u, 0x4A020303u, 0x02000101u, 0xD56A0000u,
            0x00020500u, 0xD5690001u, 0x0002000Cu, 0x4C060302u, 0x7D8C02F9u, 0x06068A02u, 0x7D86060Cu, 0x87EA6A0Au,
            0x50000080u, 0xD5286A00u, 0x002A00C1u, 0xD5010000u, 0x002200C1u, 0xD5690000u, 0x0002000Cu, 0x4C000102u,
            0xE0002000u, 0x80000000u, 0xBF8C3F70u, 0xE0102000u, 0x80010002u, 0xBF810000u,
        ];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new SharpEmu.HLE.CpuContext(new WordMemory(bytes), SharpEmu.HLE.Generation.Gen5),
            0x1000, out var program, out var decodeError), decodeError);
        var (plan, resources, layout) = Prepare(program, userDataCount: 12);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, WaveSize = 64, EnableExecGuardElision = false,
            ComputeSystemRegisters = new Gen5ComputeSystemRegisters(12, null, null, null),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotNull(fixture.Vulkan);
        using var harness = new ImageTestHarness(fixture.Vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        harness.AssertNoValidationMessages();
    }

    private sealed class WordMemory(byte[] bytes) : SharpEmu.HLE.ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    [Fact]
    public void ASavedExec_RestoresAfterAHalfMaskedBody()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 1));
        CompareTo("VCmpGeU32", 10, Gen5Operand.Vector(2), Operand(28));
        Add(Sop1(0, "SAndSaveexecB64", 12, Gen5Operand.Scalar(10)));
        Add(MoveVector(0, 4, 2));
        Add(Sop2(0, "SOrB64", Exec, Gen5Operand.Scalar(Exec), Gen5Operand.Scalar(12)));
        Add(Vop2(0, "VAddU32", 4, Operand(10), Gen5Operand.Vector(4)));

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane >= 28 ? 12u : 11u, lanes[lane]);
        }
    }
}

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

public class Wave64HalfMaskDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    protected virtual bool HostSubgroup32 => false;

    protected const uint Vcc = 106;
    protected const uint Exec = 126;
    protected static readonly Gen5Operand AllLanes = Gen5Operand.Source(193);

    private readonly List<Gen5ShaderInstruction> _instructions = [];
    private uint _pc;

    protected uint Add(Gen5ShaderInstruction instruction)
    {
        var pc = _pc;
        _instructions.Add(instruction with { Pc = pc });
        _pc += 8;
        return pc;
    }

    protected void Point(uint branchPc, uint targetPc)
    {
        var index = _instructions.FindIndex(instruction => instruction.Pc == branchPc);
        var offset = (short)(((int)targetPc - (int)branchPc - 4) / 4);
        _instructions[index] = _instructions[index] with { Words = [unchecked((uint)(ushort)offset)] };
    }

    protected void Lane(uint rowShift = 3)
    {
        Add(Vop2(0, "VLshlrevB32", 2, Operand(rowShift), Gen5Operand.Vector(1)));
        Add(Vop2(0, "VAddI32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(2)));
    }

    protected uint CompareTo(string opcode, uint destination, Gen5Operand left, Gen5Operand right) =>
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop3, opcode, [0u, 0u], [left, right], [Gen5Operand.Scalar(destination)],
            new Gen5Vop3Control(0, 0, 0, false, 0, destination)));

    // fixedThreadsX and fixedThreadsY put a thread count that can end inside the group in the module. With
    // dispatchLimits the thread count comes with each dispatch instead, and the pipeline runs once per entry.
    protected uint[] Run(uint localDataShareDwords = 0, uint sizeX = 8, uint sizeY = 8, bool? laneLocal = null,
        uint? fixedThreadsX = null, uint? fixedThreadsY = null, (uint X, uint Y)[]? dispatchLimits = null)
    {
        var threads = sizeX * sizeY;
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(2)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var program = Program([.. _instructions]);
        if (laneLocal is { } expected)
        {
            Assert.Equal(expected, Gen5WaveLaneLocalAnalysis.IsLaneLocal(program, new HashSet<uint>(), out var reason));
            Assert.True(expected || reason.Length != 0);
        }

        var (plan, resources, layout) = Prepare(program, usesDispatchThreadLimits: dispatchLimits is not null);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = sizeX, LocalSizeY = sizeY, ThreadCountX = fixedThreadsX ?? sizeX, ThreadCountY = fixedThreadsY ?? sizeY,
            WaveSize = 64, LocalDataShareDwords = localDataShareDwords, HostComputeSubgroup32 = HostSubgroup32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        if (HostSubgroup32 && laneLocal is { } oneLanePerInvocation)
        {
            var halfWavesEnabled = Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_LANE_LOCAL") == "1";
            Assert.Equal(oneLanePerInvocation && halfWavesEnabled ? sizeX : (threads + 63) / 64 * 32, LocalSizeX(shader.Spirv));
        }

        Assert.NotNull(fixture.Vulkan);
        using var harness = new ImageTestHarness(fixture.Vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(threads * 4);
        var registers = new uint[256];
        registers[6] = threads * 4;
        var buffers = new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] };
        if (dispatchLimits is null)
        {
            harness.Run(() => runner.Dispatch(registers, buffers, 1));
        }
        else
        {
            foreach (var (x, y) in dispatchLimits)
            {
                harness.Run(() => runner.Dispatch(registers, buffers, 1, dispatchThreadLimits: [x, y, 1]));
            }
        }

        var bytes = runner.ReadBack(output, 0, threads * 4);
        harness.AssertNoValidationMessages();
        var lanes = new uint[threads];
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
    public void ANarrowedRegionKeepsTheValuesItsInactiveLanesShowLater()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 4, 7));
        Add(Sop1(0, "SMovB64", 20, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(40), 2));
        Add(Vop2(0, "VAddU32", 3, Operand(64), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddU32", 4, Operand(1), Gen5Operand.Vector(3)));
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(20)));

        var lanes = Run();
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane >= 40 ? lane + 65 : 7u, lanes[lane]);
        }
    }

    [Fact]
    public void InitialExecContainsEveryLane()
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVectorFromScalar(0, 4, 126));
        Assert.All(Run(), x => Assert.Equal(uint.MaxValue, x));
    }

    private static uint LocalSizeX(byte[] spirv)
    {
        const uint ExecutionMode = 16;
        const uint LocalSize = 17;
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var count = (int)(word >> 16);
            if ((word & 0xFFFF) == ExecutionMode &&
                BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + 2 * sizeof(uint))) == LocalSize)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + 3 * sizeof(uint)));
            }

            offset += Math.Max(count, 1) * sizeof(uint);
        }

        throw new InvalidOperationException("no LocalSize execution mode");
    }

    protected bool Ready() => GatePrerequisites.Ready(fixture.Vulkan, shaderInt64: true);

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
        ExecBranch(firstActiveLane);

        Assert.All(Run(), value => Assert.Equal(expected, value));
    }

    // The join sets EXEC to every lane, or restores the EXEC saved before the compare, as compiled code does.
    protected void ExecBranch(uint firstActiveLane, bool restoreSavedExec = false)
    {
        Lane();
        Add(MoveScalar(0, 20, 5));
        if (restoreSavedExec) Add(Sop1(0, "SMovB64", 30, Gen5Operand.Scalar(Exec)));
        Add(Vopc(0, "VCmpxLeU32", Operand(firstActiveLane), 2));
        var skip = Add(Branch(0, "SCbranchExecz", 0));
        Add(MoveScalar(0, 20, 77));
        var join = Add(Sop1(0, "SMovB64", Exec, restoreSavedExec ? Gen5Operand.Scalar(30) : AllLanes));
        Point(skip, join);
        Add(MoveVectorFromScalar(0, 4, 20));
    }

    // A wave64 program can declare a group of fewer than 64 threads (48 x 1 x 1): one wave whose top lanes never run.
    // EXEC starts as every thread of the group, in both halves of the wave.
    [Theory]
    [InlineData(48u)]
    [InlineData(33u)]
    [InlineData(63u)]
    public void AGroupSmallerThanTheWave_StartsWithEveryThreadInExec(uint threads)
    {
        if (!Ready()) return;
        Lane();
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(Exec)));
        Add(MoveVectorFromScalar(0, 4, 14));

        Assert.All(Run(sizeX: threads, sizeY: 1), value => Assert.Equal(threads, value));
    }

    // The lanes of EXEC below each lane, counted in both of its words: every lane's own index.
    [Fact]
    public void AGroupSmallerThanTheWave_CountsTheLanesBelowEachLaneAcrossTheHalves()
    {
        if (!Ready()) return;
        Lane();
        Add(Vop3(0, "VMbcntLoU32B32", 4, Gen5Operand.Scalar(Exec), Operand(0)));
        Add(Vop3(0, "VMbcntHiU32B32", 4, Gen5Operand.Scalar(Exec + 1), Gen5Operand.Vector(4)));

        var lanes = Run(sizeX: 48, sizeY: 1);
        for (uint lane = 0; lane < 48; lane++)
        {
            Assert.Equal(lane, lanes[lane]);
        }
    }

    [Fact]
    public void AGroupSmallerThanTheWave_CollectsACompareAcrossTheHalves()
    {
        if (!Ready()) return;
        Lane();
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(2), Operand(40));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(10)));
        Add(MoveVectorFromScalar(0, 4, 14));

        Assert.All(Run(sizeX: 48, sizeY: 1), value => Assert.Equal(40u, value));
    }

    [Theory]
    [InlineData(40u, 77u)]
    [InlineData(8u, 77u)]
    [InlineData(48u, 5u)]
    public void AnExecBranchInAGroupSmallerThanTheWave_IsDecidedForTheWholeWave(uint firstActiveLane, uint expected)
    {
        if (!Ready()) return;
        ExecBranch(firstActiveLane);

        Assert.All(Run(sizeX: 48, sizeY: 1), value => Assert.Equal(expected, value));
    }

    // A group that is not full runs only the lanes below the thread count, and EXEC starts as those lanes.
    [Theory]
    [InlineData(3u)]
    [InlineData(5u)]
    public void APartialWave_StartsWithTheLanesThatRun(uint rows)
    {
        if (!Ready()) return;
        Lane();
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(Exec)));
        Add(MoveVectorFromScalar(0, 4, 14));

        var lanes = Run(dispatchLimits: [(8, rows)]);
        for (uint lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane < rows * 8 ? rows * 8 : 0u, lanes[lane]);
        }
    }

    // 492 items leave 44 threads to the last wave, in groups of 64 threads or of 256, whether the program fixes the count or
    // the dispatch supplies it. That wave starts with those 44 lanes in EXEC, and each lane counts the running lanes below it.
    [Theory]
    [InlineData(64u, false)]
    [InlineData(64u, true)]
    [InlineData(256u, false)]
    [InlineData(256u, true)]
    public void AWaveOf44RunningLanes_StartsWithTheLanesThatRun(uint groupThreads, bool countFromTheDispatch)
    {
        if (!Ready()) return;
        Lane();
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(Exec)));
        Add(Vop3(0, "VMbcntLoU32B32", 3, Gen5Operand.Scalar(Exec), Operand(0)));
        Add(Vop3(0, "VMbcntHiU32B32", 3, Gen5Operand.Scalar(Exec + 1), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VLshlrevB32", 3, Operand(8), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VOrB32", 4, Gen5Operand.Scalar(14), Gen5Operand.Vector(3)));

        var running = groupThreads - 20;
        var lanes = countFromTheDispatch
            ? Run(sizeX: groupThreads, sizeY: 1, dispatchLimits: [(running, 1)])
            : Run(sizeX: groupThreads, sizeY: 1, fixedThreadsX: running);
        for (uint thread = 0; thread < groupThreads; thread++)
        {
            var waveLanes = Math.Min(running - (thread & ~63u), 64u);
            Assert.Equal(thread < running ? waveLanes | ((thread & 63) << 8) : 0u, lanes[thread]);
        }
    }

    // The second dispatch runs three rows: none of the upper half of the wave runs, and none of the lanes that run passes
    // the compare. The first dispatch ran the whole wave; what it left where the halves exchange their masks must not
    // count as lanes of the second, or EXEC names lanes that can never clear their bit.
    [Fact]
    public void AnExecBranchInAPartialWave_SeesOnlyTheLanesThatRun()
    {
        if (!Ready()) return;
        ExecBranch(firstActiveLane: 32, restoreSavedExec: true);

        var lanes = Run(dispatchLimits: [(8, 8), (8, 3)]);
        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(lane < 24 ? 5u : 77u, lanes[lane]);
        }
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
    [InlineData(0u)]
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
            LocalSizeX = 64, WaveSize = 64, EnableExecGuardElision = false, HostComputeSubgroup32 = HostSubgroup32,
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

    // A minimum folded through the LDS, the way a 64-lane group reduces a value to one: every lane writes its value, then the
    // lower half of the remaining lanes takes the minimum of its own slot and the slot half a span away, until lane 0 holds
    // the minimum of the whole wave. The first fold reads what the other half of the wave wrote.
    [Theory]
    [InlineData(45u, 8u)]
    [InlineData(33u, 8u)]
    [InlineData(7u, 8u)]
    [InlineData(20u, 8u)]
    [InlineData(45u, 64u)]
    [InlineData(33u, 64u)]
    [InlineData(7u, 64u)]
    public void AMinimumFoldedThroughSharedMemory_SeesEveryLane(uint smallestLane, uint threadsX)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAddU32", 3, Operand(64 - smallestLane), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAndB32", 3, Operand(63), Gen5Operand.Vector(3)));
        Add(Vop2(0, "VAddU32", 3, Operand(5), Gen5Operand.Vector(3)));
        Add(Vop1(0, "VCvtF32U32", 10, Gen5Operand.Vector(3)));
        Add(Vop2(0, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(2)));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(10)], []));
        foreach (var half in new uint[] { 32, 16, 8, 4, 2 })
        {
            // The guest writes the three widest masks with S_BFM_B64 and the others as literals.
            Add(half >= 8
                ? Sop2(0, "SBfmB64", Exec, Operand(half), Operand(0))
                : Sop1(0, "SMovB64", Exec, Operand((1u << (int)half) - 1)));
            Add(DataShare(0, "DsRead2B32", false, [Gen5Operand.Vector(6)], [11, 12], 0, half));
            Add(Vop2(0, "VMinF32", 11, Gen5Operand.Vector(11), Gen5Operand.Vector(12)));
            Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(11)], []));
        }

        Add(Sop1(0, "SMovB64", Exec, Operand(1)));
        Add(DataShare(0, "DsReadB64", false, [Gen5Operand.Vector(6)], [11, 12]));
        Add(Vop2(0, "VMinF32", 11, Gen5Operand.Vector(11), Gen5Operand.Vector(12)));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(11)], []));
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(MoveVector(0, 7, 0));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(7)], [13]));
        Add(Vop1(0, "VCvtU32F32", 4, Gen5Operand.Vector(13)));

        Assert.All(Run(64, threadsX, 64 / threadsX), value => Assert.Equal(5u, value));
    }

    // The shape of a 64-lane group that reduces a list to one value: an outer loop hands every lane one item per pass, two
    // tests narrow EXEC and save it, an inner loop runs each lane its own count under a counter the wave shares and drops a
    // lane early, the saved masks come back in turn, and the per-lane minimum is folded through the LDS at the end.
    [Theory]
    [InlineData(150u, 8u)]
    [InlineData(150u, 64u)]
    [InlineData(40u, 64u)]
    [InlineData(64u, 64u)]
    [InlineData(97u, 64u)]
    public void ANestedLoopReduction_SeesEveryItemOfEveryLane(uint items, uint threadsX)
    {
        if (!Ready()) return;
        const uint big = 100000;
        Lane();
        Add(Vop2(0, "VAddU32", 24, Operand(0), Gen5Operand.Vector(2)));
        Add(MoveVector(0, 21, big));
        Add(MoveScalar(0, 56, items));
        Add(Sop1(0, "SMovB64", 58, Gen5Operand.Scalar(Exec)));
        var top = Add(Vopc(0, "VCmpxGtU32", Gen5Operand.Scalar(56), 24));
        var exitOuter = Add(Branch(0, "SCbranchExecz", 0));
        Add(Sop1(0, "SMovB64", 34, Gen5Operand.Scalar(Exec)));
        Add(Vop2(0, "VAndB32", 3, Operand(4), Gen5Operand.Vector(24)));
        Add(Vopc(0, "VCmpxEqU32", Operand(0), 3));
        var skipFirst = Add(Branch(0, "SCbranchExecz", 0));
        Add(Sop1(0, "SMovB64", 38, Gen5Operand.Scalar(Exec)));
        Add(MoveVector(0, 8, big));
        Add(Vop2(0, "VAndB32", 3, Operand(8), Gen5Operand.Vector(24)));
        Add(Vopc(0, "VCmpxEqU32", Operand(0), 3));
        var skipSecond = Add(Branch(0, "SCbranchExecz", 0));
        Add(MoveScalar(0, 57, 0));
        Add(Sop1(0, "SMovB64", 52, Gen5Operand.Scalar(Exec)));
        Add(MoveVector(0, 5, 0));
        Add(Vop2(0, "VAndB32", 30, Operand(3), Gen5Operand.Vector(24)));
        var inner = Add(Vopc(0, "VCmpxLtU32", Gen5Operand.Scalar(57), 30));
        var exitInner = Add(Branch(0, "SCbranchExecz", 0));
        Add(Sop1(0, "SMovB64", 54, Gen5Operand.Scalar(Exec)));
        Add(Vop2(0, "VAddU32", 5, Operand(1), Gen5Operand.Vector(5)));
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(54)));
        Add(Sop2(0, "SAddI32", 57, Gen5Operand.Scalar(57), Operand(1)));
        Add(Vopc(0, "VCmpLeU32", Operand(2), 5));
        Add(Sop2(0, "SAndn2B64", Exec, Gen5Operand.Scalar(54), Gen5Operand.Scalar(Vcc)));
        var again = Add(Branch(0, "SCbranchExecnz", 0));
        var afterInner = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(52)));
        Add(Vop2(0, "VXorB32", 9, Operand(42), Gen5Operand.Vector(24)));
        Add(Vop2(0, "VAndB32", 9, Operand(63), Gen5Operand.Vector(9)));
        Add(Vop2(0, "VLshlrevB32", 9, Operand(2), Gen5Operand.Vector(9)));
        Add(Vop2(0, "VAddU32", 8, Gen5Operand.Vector(5), Gen5Operand.Vector(9)));
        var afterSecond = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(38)));
        Add(Vop2(0, "VMinU32", 21, Gen5Operand.Vector(21), Gen5Operand.Vector(8)));
        var afterFirst = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(34)));
        Add(Vop2(0, "VAddU32", 24, Operand(64), Gen5Operand.Vector(24)));
        var loop = Add(Branch(0, "SBranch", 0));
        var done = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(58)));
        Point(exitOuter, done);
        Point(skipFirst, afterFirst);
        Point(skipSecond, afterSecond);
        Point(exitInner, afterInner);
        Point(again, inner);
        Point(loop, top);

        Add(Vop2(0, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(2)));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(21)], []));
        foreach (var half in new uint[] { 32, 16, 8, 4, 2 })
        {
            Add(half >= 8
                ? Sop2(0, "SBfmB64", Exec, Operand(half), Operand(0))
                : Sop1(0, "SMovB64", Exec, Operand((1u << (int)half) - 1)));
            Add(DataShare(0, "DsRead2B32", false, [Gen5Operand.Vector(6)], [11, 12], 0, half));
            Add(Vop2(0, "VMinU32", 11, Gen5Operand.Vector(11), Gen5Operand.Vector(12)));
            Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(11)], []));
        }

        Add(Sop1(0, "SMovB64", Exec, Operand(1)));
        Add(DataShare(0, "DsReadB64", false, [Gen5Operand.Vector(6)], [11, 12]));
        Add(Vop2(0, "VMinU32", 11, Gen5Operand.Vector(11), Gen5Operand.Vector(12)));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(11)], []));
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(MoveVector(0, 7, 0));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(7)], [4]));

        var expected = big;
        for (uint item = 0; item < items; item++)
        {
            if ((item & 4) != 0 || (item & 8) != 0) continue;
            expected = Math.Min(expected, (((item ^ 42) & 63) << 2) + Math.Min(item & 3, 2u));
        }

        Assert.All(Run(64, threadsX, 64 / threadsX), value => Assert.Equal(expected, value));
    }

    // The shape of a 256-thread group that counts the items passing a test: thread 0 clears a count in the LDS, every
    // thread takes one item per pass and the passing ones add one to the count under a saved EXEC, and after a group
    // barrier every thread reads the count. The passing items sit in different waves and passes.
    [Theory]
    [InlineData(300u, 0x1FFu, 290u)]
    [InlineData(300u, 0x1FFu, 70u)]
    [InlineData(200u, 0xFFu, 199u)]
    [InlineData(600u, 0x3Fu, 5u)]
    [InlineData(1000u, 0x7Fu, 100u)]
    public void ACountThroughSharedMemoryInAGroupOfFourWaves_SeesEveryThread(uint items, uint mask, uint value)
    {
        if (!Ready()) return;
        Lane();
        Add(MoveVector(0, 7, 0));
        Add(MoveVector(0, 9, 1));
        CompareTo("VCmpEqU32", 40, Operand(0), Gen5Operand.Vector(2));
        Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(40)));
        var noClear = Add(Branch(0, "SCbranchExecz", 0));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(7), Gen5Operand.Vector(7)], []));
        var afterClear = Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Point(noClear, afterClear);
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        Add(Vop2(0, "VAddU32", 13, Operand(0), Gen5Operand.Vector(2)));
        Add(MoveScalar(0, 15, items));
        var top = Add(Vopc(0, "VCmpxGtU32", Gen5Operand.Scalar(15), 13));
        var exit = Add(Branch(0, "SCbranchExecz", 0));
        Add(Sop1(0, "SMovB64", Vcc, Gen5Operand.Scalar(Exec)));
        Add(Vop2(0, "VAndB32", 3, Operand(mask), Gen5Operand.Vector(13)));
        Add(Vopc(0, "VCmpxEqU32", Operand(value), 3));
        var skipAdd = Add(Branch(0, "SCbranchExecz", 0));
        Add(DataShare(0, "DsAddU32", false, [Gen5Operand.Vector(7), Gen5Operand.Vector(9)], []));
        var restore = Add(Sop1(0, "SMovB64", Exec, Gen5Operand.Scalar(Vcc)));
        Add(Vop2(0, "VAddU32", 13, Operand(256), Gen5Operand.Vector(13)));
        var loop = Add(Branch(0, "SBranch", 0));
        var done = Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        Point(exit, done);
        Point(skipAdd, restore);
        Point(loop, top);
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(DataShare(0, "DsReadB32", false, [Gen5Operand.Vector(7)], [4]));

        var expected = 0u;
        for (uint item = 0; item < items; item++)
        {
            if ((item & mask) == value) expected++;
        }

        Assert.All(Run(64, 256, 1), count => Assert.Equal(expected, count));
    }

    // A group of four waves: each wave's masks, counts and lane reads cover the 64 lanes of that wave and no other.
    private uint[] RunGroupOfFourWaves(uint localDataShareDwords = 0) => Run(localDataShareDwords, 256, 1);

    [Fact]
    public void AWaveOfAGroupOfFourWaves_CountsACompareOverBothHalves()
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAndB32", 3, Operand(63), Gen5Operand.Vector(2)));
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(3), Operand(40));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(10)));
        Add(MoveVectorFromScalar(0, 4, 14));

        Assert.All(RunGroupOfFourWaves(), value => Assert.Equal(40u, value));
    }

    [Theory]
    [InlineData(40u)]
    [InlineData(3u)]
    public void ReadFirstLane_InAGroupOfFourWaves_TakesTheFirstActiveLaneOfItsWave(uint firstActiveLane)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAndB32", 3, Operand(63), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddU32", 5, Operand(100), Gen5Operand.Vector(2)));
        Add(Vopc(0, "VCmpxLeU32", Operand(firstActiveLane), 3));
        Add(ReadFirstLane(0, 20, 5));
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(MoveVectorFromScalar(0, 4, 20));

        var lanes = RunGroupOfFourWaves();
        for (uint thread = 0; thread < 256; thread++)
        {
            Assert.Equal(100 + (thread & ~63u) + firstActiveLane, lanes[thread]);
        }
    }

    [Theory]
    [InlineData(40u)]
    [InlineData(7u)]
    public void ReadLane_InAGroupOfFourWaves_ReadsTheLaneInEitherHalf(uint lane)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAddU32", 5, Operand(100), Gen5Operand.Vector(2)));
        Add(ReadLane(0, 20, 5, lane));
        Add(MoveVectorFromScalar(0, 4, 20));

        var lanes = RunGroupOfFourWaves();
        for (uint thread = 0; thread < 256; thread++)
        {
            Assert.Equal(100 + (thread & ~63u) + lane, lanes[thread]);
        }
    }

    [Fact]
    public void Mbcnt_InAGroupOfFourWaves_CountsTheLanesBelowAcrossTheHalves()
    {
        if (!Ready()) return;
        Lane();
        Add(Vop3(0, "VMbcntLoU32B32", 4, Gen5Operand.Scalar(Exec), Operand(0)));
        Add(Vop3(0, "VMbcntHiU32B32", 4, Gen5Operand.Scalar(Exec + 1), Gen5Operand.Vector(4)));

        var lanes = RunGroupOfFourWaves();
        for (uint thread = 0; thread < 256; thread++)
        {
            Assert.Equal(thread & 63, lanes[thread]);
        }
    }

    // Each wave's first lane stores the wave's count in the wave's LDS slot, and every thread adds the four slots: the
    // way a group sums what its waves found. A wave that counts each half alone leaves out the lanes of the other half.
    [Theory]
    [InlineData(40u)]
    [InlineData(64u)]
    [InlineData(20u)]
    public void AWaveCountInSharedMemory_InAGroupOfFourWaves_HoldsTheWholeWave(uint passing)
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAndB32", 3, Operand(63), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VLshrrevB32", 6, Operand(6), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VLshlrevB32", 6, Operand(2), Gen5Operand.Vector(6)));
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(3), Operand(passing));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(10)));
        Add(MoveVectorFromScalar(0, 7, 14));
        Add(Vopc(0, "VCmpxEqU32", Operand(0), 3));
        Add(DataShare(0, "DsWriteB32", false, [Gen5Operand.Vector(6), Gen5Operand.Vector(7)], []));
        Add(Sop1(0, "SMovB64", Exec, AllLanes));
        Add(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        Add(MoveVector(0, 8, 0));
        Add(DataShare(0, "DsRead2B32", false, [Gen5Operand.Vector(8)], [11, 12], 0, 1));
        Add(DataShare(0, "DsRead2B32", false, [Gen5Operand.Vector(8)], [13, 14], 2, 3));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(11), Gen5Operand.Vector(12)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(13), Gen5Operand.Vector(4)));
        Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(14), Gen5Operand.Vector(4)));

        Assert.All(RunGroupOfFourWaves(64), value => Assert.Equal(4 * passing, value));
    }

    // Two rows of 32 threads make a wave. With 20 threads of every row to run, each wave starts with 20 lanes of each half.
    [Fact]
    public void AGroupOfFourWavesWithShortRows_StartsEachWaveWithTheLanesThatRun()
    {
        if (!Ready()) return;
        Lane(rowShift: 5);
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(Exec)));
        Add(MoveVectorFromScalar(0, 4, 14));

        var lanes = Run(sizeX: 32, sizeY: 8, dispatchLimits: [(20, 8)]);
        for (uint thread = 0; thread < 256; thread++)
        {
            Assert.Equal((thread & 31) < 20 ? 40u : 0u, lanes[thread]);
        }
    }

    // Two rows of 32 threads make a wave. With five rows to run, the third wave runs its lower half alone and the fourth
    // does not run.
    [Fact]
    public void APartialWaveOfAGroupOfFourWaves_StartsWithTheLanesThatRun()
    {
        if (!Ready()) return;
        Lane(rowShift: 5);
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(Exec)));
        Add(MoveVectorFromScalar(0, 4, 14));

        var lanes = Run(sizeX: 32, sizeY: 8, dispatchLimits: [(32, 5)]);
        for (uint thread = 0; thread < 256; thread++)
        {
            Assert.Equal(thread < 128 ? 64u : thread < 160 ? 32u : 0u, lanes[thread]);
        }
    }

    // The same five rows: each wave counts a compare over the lanes that run, and the third wave, with no upper half that
    // runs, exchanges its masks without one.
    [Fact]
    public void APartialWaveOfAGroupOfFourWaves_CountsACompareOverTheLanesThatRun()
    {
        if (!Ready()) return;
        Lane(rowShift: 5);
        Add(Vop2(0, "VAndB32", 3, Operand(63), Gen5Operand.Vector(2)));
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(3), Operand(40));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(10)));
        Add(MoveVectorFromScalar(0, 4, 14));

        var lanes = Run(sizeX: 32, sizeY: 8, dispatchLimits: [(32, 5)]);
        for (uint thread = 0; thread < 256; thread++)
        {
            Assert.Equal(thread < 128 ? 40u : thread < 160 ? 32u : 0u, lanes[thread]);
        }
    }

    // A loop that waits for EXEC to empty ends in a group that does not run whole: the lanes that do not run are not in
    // EXEC. Sixteen threads of the second wave run, all in its lower half, and the other two waves do not run.
    [Fact]
    public void AWaterfallLoopInAPartialGroup_VisitsEveryValueOfTheLanesThatRun()
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

        var lanes = Run(sizeX: 16, sizeY: 16, laneLocal: true, dispatchLimits: [(16, 5)]);
        for (uint index = 0; index < 256; index++)
        {
            Assert.Equal(index < 80 ? (index & 3) + 100 : 0u, lanes[index]);
        }
    }

    // A group of 96 threads is one whole wave and one that has no upper half.
    [Fact]
    public void AGroupWithAHalfLastWave_CountsEachWaveOverItsOwnLanes()
    {
        if (!Ready()) return;
        Lane();
        Add(Vop2(0, "VAndB32", 3, Operand(63), Gen5Operand.Vector(2)));
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(3), Operand(40));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(10)));
        Add(Sop1(0, "SBcnt1I32B64", 15, Gen5Operand.Scalar(Exec)));
        Add(Sop2(0, "SLshlB32", 15, Gen5Operand.Scalar(15), Operand(8)));
        Add(Sop2(0, "SOrB32", 14, Gen5Operand.Scalar(14), Gen5Operand.Scalar(15)));
        Add(MoveVectorFromScalar(0, 4, 14));

        var lanes = Run(sizeX: 96, sizeY: 1);
        for (uint thread = 0; thread < 96; thread++)
        {
            Assert.Equal(thread < 64 ? 40u | (64u << 8) : 32u | (32u << 8), lanes[thread]);
        }
    }

    // A group of 16 threads fills a quarter of its wave, and no lane of the upper half exists.
    [Fact]
    public void AGroupOfSixteenThreads_CountsOnlyItsLanes()
    {
        if (!Ready()) return;
        Lane();
        CompareTo("VCmpLtU32", 10, Gen5Operand.Vector(2), Operand(10));
        Add(Sop1(0, "SBcnt1I32B64", 14, Gen5Operand.Scalar(10)));
        Add(Sop1(0, "SBcnt1I32B64", 15, Gen5Operand.Scalar(Exec)));
        Add(Sop2(0, "SLshlB32", 15, Gen5Operand.Scalar(15), Operand(8)));
        Add(Sop2(0, "SOrB32", 14, Gen5Operand.Scalar(14), Gen5Operand.Scalar(15)));
        Add(MoveVectorFromScalar(0, 4, 14));

        Assert.All(Run(sizeX: 16, sizeY: 1), value => Assert.Equal(10u | (16u << 8), value));
    }

    // A compiler spills a scalar to a VGPR lane with V_WRITELANE and reads it back with V_READLANE whichever threads run:
    // the lane keeps the value although its own thread does not run, in either wave64 lowering. Six threads run here, so neither the invocation
    // that holds lanes 20 and 52 nor the one that holds lanes 18 and 50 runs.
    [Theory]
    [InlineData(3u)]
    [InlineData(20u)]
    [InlineData(50u)]
    public void AScalarSpilledToALaneOfAPartialWave_ReadsBack(uint lane)
    {
        if (!Ready()) return;
        Lane();
        Add(MoveScalar(0, 20, 0x1234));
        Add(WriteLane(0, 40, 20, lane));
        // The reload sits in a later block, as a spill and its reload usually do.
        var jump = Add(Branch(0, "SBranch", 0));
        var reload = Add(ReadLane(0, 21, 40, lane));
        Point(jump, reload);
        Add(MoveVectorFromScalar(0, 4, 21));

        var lanes = Run(sizeX: 64, sizeY: 1, dispatchLimits: [(6, 1)]);
        for (uint thread = 0; thread < 64; thread++)
        {
            Assert.Equal(thread < 6 ? 0x1234u : 0u, lanes[thread]);
        }
    }
}

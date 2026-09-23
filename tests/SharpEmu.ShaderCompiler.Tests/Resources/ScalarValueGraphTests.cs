// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ScalarValueGraphTests
{
    private static Gen5ShaderInstruction[] Descriptor(uint pc, uint register, uint dword1, uint dword2, uint dword3) =>
    [
        MoveScalar(pc, register + 1, dword1),
        MoveScalar(pc + 4, register + 2, dword2),
        MoveScalar(pc + 8, register + 3, dword3),
    ];

    [Fact]
    public void ImmediateFlatteningAndValueNumbering()
    {
        var program = Program(
            MoveScalar(0, 4, 0x1000),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, 4, destination: 8, immediateOffset: 0x20),
            ScalarLoad(12, 4, destination: 9, immediateOffset: 0x20),
            MoveScalar(16, 10, 16),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(28));
        var plan = Extract(program);

        Assert.Single(plan.TableReads);
        Assert.Empty(plan.DynamicReads);
        Assert.True(plan.Memory.Find(8)!.PlanningOnly);
        var memory = new TestWordMemory { Words = new uint[16] };
        memory.At(0x1020) = 0xFEEDBEEF;
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, Inputs([], memory.Read), out var table));
        Assert.Equal([0xFEEDBEEFu], table);
        Assert.Equal(1u, memory.Reads);
    }

    [Fact]
    public void RawScalarComponentAlignment()
    {
        var program = Program(
            MoveScalar(0, 4, 0x1003),
            MoveScalar(4, 5, 0),
            MoveScalar(8, 6, 2),
            ScalarLoad(12, 4, destination: 8, immediateOffset: 2, dynamicOffsetRegister: 6),
            MoveScalar(16, 9, 0),
            MoveScalar(20, 10, 16),
            MoveScalar(24, 11, 0),
            BufferLoad(28, 8),
            EndProgram(32));
        var plan = Extract(program);

        var memory = new TestWordMemory();
        memory.At(0x1000) = 0x12345678;
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, Inputs([], memory.Read), out var table));
        Assert.Equal([0x12345678u], table);
        Assert.Equal(1u, memory.Reads);
    }

    [Fact]
    public void DynamicReadRemainsExplicit()
    {
        var program = Program(
            MoveScalar(0, 4, 0x1000),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, 4, destination: 8, dynamicOffsetRegister: 2),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 16),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(28));
        var plan = Extract(program);

        Assert.Empty(plan.TableReads);
        var read = Assert.Single(plan.DynamicReads);
        Assert.Equal(ScalarValueKind.ScalarAddressWord, read.Kind);
    }

    [Fact]
    public void NestedResourceTableWalk()
    {
        var program = Program(
            MoveScalar(0, 4, 0x1000),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, 4, destination: 6),
            MoveScalar(12, 7, 0),
            ScalarLoad(16, 6, destination: 8),
            MoveScalar(20, 9, 0),
            MoveScalar(24, 10, 16),
            MoveScalar(28, 11, 0),
            BufferLoad(32, 8),
            EndProgram(36));
        var plan = Extract(program);

        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x1100 / 4] };
        memory.At(0x1000) = 0x2000;
        memory.At(0x2000) = 0xABCDEF01;
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, Inputs([], memory.Read), out var table));
        Assert.Equal([0x2000u, 0xABCDEF01u], table);
    }

    [Fact]
    public void ShaderBaseAndUserData()
    {
        var program = Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sop1, "SGetpcB64", [0u], [], [Gen5Operand.Scalar(4)], null),
            Sop2(4, "SAddU32", 6, Gen5Operand.Scalar(2), Operand(4)),
            MoveScalar(8, 7, 0),
            BufferLoad(12, 4),
            EndProgram(20));
        var plan = Extract(program, userDataCount: 3);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([0, 0, 0x20], shaderBase: 0x12345678ABCDEF00), out var result));
        Assert.Equal(0xABCDEF04u, result.Dwords[0]);
        Assert.Equal(0x12345678u, result.Dwords[1]);
        Assert.Equal(0x24u, result.Dwords[2]);
    }

    [Fact]
    public void CarryAndBitFields()
    {
        var program = Program(
            Sop2(0, "SAddU32", 4, Operand(0xFFFFFFFF), Operand(2)),
            Sop2(4, "SCselectB32", 5, Operand(1), Operand(0)),
            Sop2(8, "SBfeI32", 6, Operand(0xF0), Operand(4 | (4 << 16))),
            Sop2(12, "SBfmB32", 7, Operand(8), Operand(4)),
            BufferLoad(16, 4),
            EndProgram(24));
        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([]), out var result));
        Assert.Equal([1u, 1u, 0xFFFFFFFFu, 0xFF0u], result.Dwords);
    }

    private static Gen5ShaderProgram PhiProgram(uint first, uint second) => Program(
        Branch(0, "SCbranchScc0", 2),
        MoveScalar(4, 8, first),
        Branch(8, "SBranch", 1),
        MoveScalar(12, 8, second),
        MoveScalar(16, 9, 0),
        MoveScalar(20, 10, 16),
        MoveScalar(24, 11, 0),
        BufferLoad(28, 8),
        EndProgram(32));

    [Fact]
    public void InvariantAndDivergentPhi()
    {
        var invariant = Extract(PhiProgram(7, 7));
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(invariant, invariant.Info.Buffers[0].Source, Inputs([]), out var result));
        Assert.Equal(7u, result.Dwords[0]);

        var error = Assert.Throws<ResourcePlanException>(() => Extract(PhiProgram(7, 9)));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    [Fact]
    public void ControlDependentStandaloneLoadStaysExplicit()
    {
        var program = Program(
            Branch(0, "SCbranchScc0", 2),
            MoveScalar(4, 4, 0x1000),
            Branch(8, "SBranch", 1),
            MoveScalar(12, 4, 0x2000),
            MoveScalar(16, 5, 0),
            ScalarLoad(20, 4, destination: 8),
            EndProgram(28));
        var plan = Extract(program);

        Assert.Empty(plan.TableReads);
        Assert.False(plan.Memory.Find(20)!.PlanningOnly);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    [Fact]
    public void Runtime64BitDescriptorOperations()
    {
        var program = Program(
            Sop2(0, "SLshlB64", 4, Operand(0x1234), Operand(32)),
            MoveScalar(4, 6, 0),
            MoveScalar(8, 7, 0xFFFF),
            Sop2(12, "SAndB64", 4, Gen5Operand.Scalar(4), Gen5Operand.Scalar(6)),
            Sop2(16, "SAddU32", 4, Gen5Operand.Scalar(4), Operand(0xABCD)),
            Sop2(20, "SAddcU32", 5, Gen5Operand.Scalar(5), Operand(1)),
            MoveScalar(24, 6, 16),
            MoveScalar(28, 7, 0),
            BufferLoad(32, 4),
            EndProgram(40));
        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([]), out var result));
        Assert.Equal(0xABCDu, result.Dwords[0]);
        Assert.Equal(0x1235u, result.Dwords[1]);
    }

    [Fact]
    public void UniformFirstLaneSamplerLod()
    {
        var program = Program(
            Vop1(0, "VCvtF32U32", 1, Gen5Operand.Scalar(8)),
            Vop2(4, "VMulF32", 1, Operand(0x43800000), Gen5Operand.Vector(1)),
            Vop1(12, "VCvtU32F32", 1, Gen5Operand.Vector(1)),
            Vop2(16, "VMinU32", 1, Operand(0xFFF), Gen5Operand.Vector(1)),
            Vop2(24, "VAndB32", 1, Operand(0xFFF), Gen5Operand.Vector(1)),
            Vop3(32, "VLshlOrU32", 1, Gen5Operand.Vector(1), Operand(12), Gen5Operand.Vector(1)),
            ReadFirstLane(40, 12, 1),
            MoveScalar(44, 13, 0),
            MoveScalar(48, 14, 16),
            MoveScalar(52, 15, 0),
            BufferLoad(56, 12),
            EndProgram(64));
        var plan = Extract(program);
        var userData = new uint[9];

        userData[8] = 3;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(userData), out var result));
        Assert.Equal(0x00300300u, result.Dwords[0]);
        userData[8] = 20;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(userData), out result));
        Assert.Equal(0x00FFFFFFu, result.Dwords[0]);
    }

    [Fact]
    public void DivergentFirstLaneValue_IsRejected()
    {
        var program = Program(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            BufferLoad(16, 12),
            EndProgram(24));

        var error = Assert.Throws<ResourcePlanException>(() => Extract(program));
        Assert.Contains("not a valid runtime value", error.Message);
        Assert.Contains("pc=0x00000010", error.Message);
    }

    [Fact]
    public void DifferentGpuDependentFirstLaneReads_KeepSeparateIdentities()
    {
        var program = Program(
            ReadFirstLane(0, 12, 0),
            BufferLoad(4, 12),
            ReadFirstLane(12, 12, 1),
            BufferLoad(16, 12),
            EndProgram(24));
        var graph = ScalarValueGraph.Build(program, 0, 16);
        var firstSelector = graph.Accesses[0]!.Handle!.Operands[0];
        var secondSelector = graph.Accesses[1]!.Handle!.Operands[0];

        Assert.Equal(ScalarValueKind.FirstLane, firstSelector.Kind);
        Assert.Equal(ScalarValueKind.FirstLane, secondSelector.Kind);
        Assert.True(firstSelector.Operands[0].IsUndefined);
        Assert.True(secondSelector.Operands[0].IsUndefined);
        Assert.NotSame(firstSelector, secondSelector);
        Assert.False(graph.Equivalent(firstSelector, secondSelector));
    }

    [Fact]
    public void ConstantBufferBounds()
    {
        static Gen5ShaderProgram Read(int immediate) => Program(
            MoveScalar(0, 0, 0x3000),
            MoveScalar(4, 1, 0),
            MoveScalar(8, 2, 16),
            MoveScalar(12, 3, 0),
            ScalarBufferLoad(16, 0, destination: 8, immediateOffset: immediate),
            MoveScalar(24, 9, 0),
            MoveScalar(28, 10, 16),
            MoveScalar(32, 11, 0),
            BufferLoad(36, 8),
            EndProgram(44));

        var memory = new TestWordMemory { Base = 0x3000 };
        memory.At(0x300C) = 0xA5A5A5A5;
        var plan = Extract(Read(12), userDataCount: 0);
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, Inputs([], memory.Read), out var table));
        Assert.Equal([0xA5A5A5A5u], table);

        var overflow = Extract(Read(16), userDataCount: 0);
        Assert.False(RuntimeValueEvaluator.FlattenResourceTable(overflow, Inputs([], memory.Read), out _));
    }

    // A loop whose body runs under a divergent mask still reaches a fixpoint: the
    // undefined mask has one identity, so the values built over it stop changing.
    [Fact]
    public void MaskedLoop_Converges()
    {
        var program = Program(
            MoveScalar(0, 0, 4),
            Vop1(4, "VMovB32", 1, Operand(0)),
            Vopc(8, "VCmpGtU32", Gen5Operand.Scalar(0), 2),
            Sop1(12, "SAndSaveexecB32", 1, Gen5Operand.Scalar(106)),
            Vop2(16, "VAddI32", 1, Gen5Operand.Scalar(0), Gen5Operand.Vector(1)),
            MoveScalarRegister(20, 126, 1),
            Sop2(24, "SSubI32", 0, Gen5Operand.Scalar(0), Operand(1)),
            Sopc(28, "SCmpLgU32", Gen5Operand.Scalar(0), Operand(0)),
            Branch(32, "SCbranchScc1", -7),
            MoveScalar(36, 9, 0),
            MoveScalar(40, 10, 64),
            MoveScalar(44, 11, 0),
            BufferStore(48, 8),
            EndProgram(56));

        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([0, 0, 0, 0, 0, 0, 0, 0, 0x1000]), out var result));
        Assert.Equal(0x1000u, result.Dwords[0]);
        Assert.Equal(64u, result.Dwords[2]);
    }

    [Fact]
    public void UndefinedRuntimeValueFails()
    {
        var error = Assert.Throws<ResourcePlanException>(() => Extract(Program(BufferLoad(0, 20), EndProgram(8)), userDataCount: 16));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    // ---- Requirement A: lane provenance and uniform vector values ----

    [Fact]
    public void FixedLaneSaveRestore_PreservesDescriptorProvenance()
    {
        var program = Program(
            WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 2),
            WriteLane(8, vectorRegister: 18, scalarRegister: 85, lane: 5),
            MoveScalar(16, 84, 0xDEADBEEF),
            MoveScalar(24, 85, 0xBAD0CAFE),
            ReadLane(32, scalarRegister: 84, vectorRegister: 18, lane: 2),
            ReadLane(40, scalarRegister: 85, vectorRegister: 18, lane: 5),
            ScalarLoad(48, 84, destination: 4),
            EndProgram(56));
        var plan = Extract(program, userDataBase: 84, userDataCount: 2);

        var read = Assert.Single(plan.TableReads);
        var handle = read.Value.Operands[0];
        Assert.Equal(ScalarValueKind.UserData, handle.Operands[0].Kind);
        Assert.Equal(84u, handle.Operands[0].UserDataRegister);
        Assert.Equal(85u, handle.Operands[1].UserDataRegister);
        var expectedAddress = 0x0000_0004_413B_A5B0ul;
        var reader = (ulong address, out uint word) =>
        {
            word = 0x12345678;
            return address == expectedAddress;
        };
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, Inputs([0x413BA5B0, 4], new GuestWordReader(reader)), out var table));
        Assert.Equal([0x12345678u], table);
    }

    [Fact]
    public void FullVectorWrite_InvalidatesSavedLanes()
    {
        var program = Program(
            WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 2),
            Vop1(8, "VMovB32", 18, Gen5Operand.Scalar(0)),
            MoveScalar(12, 84, 0xDEADBEEF),
            ReadLane(16, scalarRegister: 84, vectorRegister: 18, lane: 2),
            ScalarLoad(24, 84, destination: 4),
            EndProgram(32));
        var plan = Extract(program, userDataBase: 84, userDataCount: 2);

        Assert.Empty(plan.TableReads);
        var access = plan.Accesses[plan.Memory.Count - 1]!;
        Assert.True(access.Handle!.Operands[0].IsUndefined);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    // The restored pointer meets the zeroed one at the join, so the read is not a
    // host-side table read; it stays an in-shader device-address read.
    [Fact]
    public void RestoredPointer_MeetsTheOtherPathAtTheJoin()
    {
        var program = Program(
            WriteLane(0, vectorRegister: 18, scalarRegister: 84, lane: 2),
            WriteLane(8, vectorRegister: 18, scalarRegister: 85, lane: 5),
            MoveScalar(16, 84, 0),
            MoveScalar(20, 85, 0),
            Branch(24, "SCbranchScc0", 4),
            ReadLane(28, scalarRegister: 84, vectorRegister: 18, lane: 2),
            ReadLane(36, scalarRegister: 85, vectorRegister: 18, lane: 5),
            ScalarLoad(44, 84, destination: 4),
            EndProgram(52));
        var plan = Extract(program, userDataBase: 84, userDataCount: 2);

        Assert.Empty(plan.TableReads);
        var access = plan.Accesses[plan.Memory.Count - 1]!;
        Assert.Equal(ScalarValueKind.Phi, access.Handle!.Operands[0].Kind);
        Assert.Null(plan.Graph.ResolveInvariantPhi(access.Handle.Operands[0]));
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    [Theory]
    [InlineData(32u, 0x1000u)]
    [InlineData(64u, 0u)]
    public void VectorCompareKeepsVccHighInWave32(uint waveSize, uint expected)
    {
        // Wave32 compilers use VCC_HI (s107) as an ordinary SGPR; a compare writes VCC_LO only.
        var program = Program(
            MoveScalar(0, 107, 0x1000),
            Vopc(4, "VCmpEqU32", Gen5Operand.Scalar(0), 1),
            MoveScalarRegister(8, 8, 107),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 16),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(32));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 64, waveSize: waveSize);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([]), out var result));
        Assert.Equal(expected, result.Dwords[0]);
    }

    [Fact]
    public void UniformVectorDerivedValue_IsAccepted()
    {
        var program = Program(
            Vop1(0, "VMovB32", 0, Gen5Operand.Scalar(4)),
            ReadFirstLane(4, 8, 0),
            MoveScalar(8, 9, 0),
            MoveScalar(12, 10, 16),
            MoveScalar(16, 11, 0),
            BufferLoad(20, 8),
            EndProgram(28));
        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([0, 0, 0, 0, 0x77]), out var result));
        Assert.Equal(0x77u, result.Dwords[0]);
    }

    [Fact]
    public void DivergentVectorValue_IsRejected()
    {
        var perLane = Program(
            BufferLoad(0, 0, dwords: 1),
            ReadFirstLane(8, 8, 4),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 16),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(32));
        var error = Assert.Throws<ResourcePlanException>(() => Extract(perLane));
        Assert.Contains("not a valid runtime value", error.Message);
        Assert.Contains("pc=0x00000018", error.Message);

        var laneCount = Program(
            Vop2(0, "VMbcntLoU32B32", 1, Operand(0xFFFFFFFF), Gen5Operand.Vector(0)),
            ReadFirstLane(8, 8, 1),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 16),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(32));
        Assert.Throws<ResourcePlanException>(() => Extract(laneCount));
    }

    [Fact]
    public void UserDataChangesBetweenDraws_ChangeTheDescriptorWithoutRebuildingThePlan()
    {
        var plan = Extract(Program(BufferLoad(0, 4), EndProgram(8)));
        var source = plan.Info.Buffers[0].Source;

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source, Inputs([0, 0, 0, 0, 0x1000, 1, 2, 3]), out var first));
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source, Inputs([0, 0, 0, 0, 0x2000, 4, 5, 6]), out var second));
        Assert.Equal([0x1000u, 1u, 2u, 3u], first.Dwords);
        Assert.Equal([0x2000u, 4u, 5u, 6u], second.Dwords);
        Assert.All(plan.DescriptorSources[(int)source].Dwords, dword => Assert.Equal(ScalarValueKind.UserData, dword.Kind));
    }

    [Fact]
    public void ShaderBaseChangesBetweenDraws_ChangeAnAddressDerivedDescriptor()
    {
        var program = Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sop1, "SGetpcB64", [0u], [], [Gen5Operand.Scalar(4)], null),
            Sop2(4, "SAddU32", 4, Gen5Operand.Scalar(4), Operand(0x100)),
            Sop2(8, "SAddcU32", 5, Gen5Operand.Scalar(5), Operand(0)),
            ScalarLoad(12, 4, destination: 8, count: 4),
            BufferLoad(20, 8),
            EndProgram(28));
        var plan = Extract(program);

        Assert.Equal(4, plan.TableReads.Count);
        foreach (var shaderBase in new ulong[] { 0x1_0000_0000, 0x2_0000_0000 })
        {
            var reader = (ulong address, out uint word) =>
            {
                word = (uint)(address >> 32);
                return address >= shaderBase + 0x104 && address < shaderBase + 0x114;
            };
            Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, Inputs([], new GuestWordReader(reader), shaderBase: shaderBase), out var table));
            Assert.Equal([(uint)(shaderBase >> 32), (uint)(shaderBase >> 32), (uint)(shaderBase >> 32), (uint)(shaderBase >> 32)], table);
        }
    }
}

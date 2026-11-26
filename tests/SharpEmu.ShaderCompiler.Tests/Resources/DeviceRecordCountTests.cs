// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DeviceRecordCountTests
{
    private static readonly uint[] UserData = [0x1000, 16u << 16, 0x2000, 0];
    private static readonly DispatchBufferFootprint.Dispatch Dispatch = new(2, 1, 1, 8, 1, 1, 64, 1, new(4, null, null, null));

    private static Gen5ShaderProgram CountProgram(bool dataIndex = false) => Program(
        MoveScalarRegister(0, 8, 2), Sop2(4, "SOrB32", 9, Gen5Operand.Scalar(3), Operand(4u << 16)),
        MoveScalar(8, 10, 1), MoveScalar(12, 11, 0x16204), MoveScalar(16, 3, 0x16204),
        ScalarBufferLoad(20, 8, 2),
        Vop3(28, "VLshlAddU32", 1, Gen5Operand.Scalar(4), Operand(3), Gen5Operand.Vector(0)),
        dataIndex ? Vop1(36, "VMovB32", 1, Gen5Operand.Scalar(2)) : Nop(36),
        BufferAccess(40, "BufferStoreDwordx4", 0, dwords: 4, indexEnabled: true, vectorAddress: 1), EndProgram(48));

    private static ShaderResourcePlan Plan(Gen5ShaderProgram program, bool enabled = true)
    {
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 4,
            deviceRecordCounts: enabled);
        if (plan.DeviceRecordCounts is { } counts) counts.QueueProof = action => { action(); return true; };
        return plan;
    }

    [Fact]
    public void PendingProofUsesTheExactCountThenInvalidatesItsCachedMaterialization()
    {
        var plan = Plan(CountProgram());
        var jobs = new Queue<Action>();
        var counts = plan.DeviceRecordCounts!;
        counts.QueueProof = action => { jobs.Enqueue(action); return true; };
        var reads = 0;
        bool Read(ulong address, out uint value) { reads++; value = 7; return true; }
        static bool Resident(ulong address, Span<byte> destination, bool clean)
        {
            for (var index = 0; index < destination.Length; index++)
                destination[index] = index % 4 == 0 ? (byte)7 : (byte)0;
            return true;
        }
        var cache = new ResourceMaterializationCache();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var inputs = InputsFor(Read);
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(112ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
        Assert.Equal(0, counts.ProofRevision);
        Assert.Single(jobs);
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Single(jobs); // Pending cache hits never enqueue the shape twice.
        jobs.Dequeue()();
        Assert.Equal(1, counts.ProofRevision);
        var oldReads = reads;
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(256ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
        Assert.Equal(oldReads, reads);
        Assert.Empty(jobs);
        Assert.True(cache.Materialize(plan, InputsFor(Read, mapped: false), Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(112ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
        Assert.Equal(oldReads + 1, reads);
        Assert.True(cache.Materialize(plan, inputs, Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(256ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
        Assert.Equal(oldReads + 1, reads);
    }

    [Fact]
    public void EvictionByPendingShapesCannotReuseAnUnmappedMaterialization()
    {
        var plan = Plan(CountProgram());
        static bool Read(ulong address, out uint value) { value = 7; return true; }
        static bool Resident(ulong address, Span<byte> destination, bool clean) => false;
        var cache = new ResourceMaterializationCache();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, InputsFor(Read), Resident, ref snapshot, ref specialization, out _));
        Assert.True(cache.Materialize(plan, InputsFor(Read), Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(256ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
        var revision = plan.DeviceRecordCounts!.ProofRevision;
        var jobs = new Queue<Action>();
        plan.DeviceRecordCounts.QueueProof = action => { jobs.Enqueue(action); return true; };
        for (uint groups = 3; groups <= 34; groups++)
            DeviceAddressRangePlanner.Evaluate(plan, InputsFor(Read, groups: groups));
        Assert.Equal(revision, plan.DeviceRecordCounts.ProofRevision);
        Assert.Equal(32, jobs.Count);
        Assert.True(cache.Materialize(plan, InputsFor(Read, mapped: false), Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(112ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
        Assert.Equal(33, jobs.Count);
    }

    [Fact]
    public void FullProofQueueFallsBackAndRetriesAdmissionLater()
    {
        var plan = Plan(CountProgram());
        static bool Read(ulong address, out uint value) { value = 7; return true; }
        static bool Resident(ulong address, Span<byte> destination, bool clean) => false;
        var cache = new ResourceMaterializationCache();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        plan.DeviceRecordCounts!.QueueProof = _ => false;
        Assert.True(cache.Materialize(plan, InputsFor(Read), Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(112ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
        plan.DeviceRecordCounts.QueueProof = action => { action(); return true; };
        Assert.True(cache.Materialize(plan, InputsFor(Read), Resident, ref snapshot, ref specialization, out _));
        Assert.Equal(256ul, Assert.Single(snapshot.DeviceAddressRanges).Size);
    }

    private static ResourceRuntimeInputs InputsFor(GuestWordReader reader, uint groups = 2, bool mapped = true, bool proof = true) => new()
    {
        UserData = UserData, ShaderBase = 0x8000, ReadMemory = reader, ReadCleanMemory = reader,
        IsRangeMapped = (_, _) => mapped,
        ComputeState = new(64, 8, 1, 1, false, 0, 1)
        {
            Dispatch = Dispatch with { GroupsX = groups }, DeviceCountProof = proof,
        },
    };

    [Fact]
    public void CountStaysInTheShaderAndResidencyUsesTheWholeDispatch()
    {
        var program = CountProgram();
        var ordinary = Plan(program, false);
        Assert.Single(ordinary.TableReads);
        var plan = Plan(program);
        Assert.NotNull(plan.DeviceRecordCounts);
        Assert.Empty(plan.TableReads);
        Assert.True(plan.Memory.Find(40)!.DeviceDescriptor);
        static bool NoRead(ulong address, out uint value) => throw new InvalidOperationException($"Unexpected count read: {address:X}");
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, InputsFor(NoRead), ref snapshot, ref specialization));
        var span = Assert.Single(snapshot.DeviceAddressRanges);
        Assert.Equal(0x1000ul, span.Base);
        Assert.Equal(256ul, span.Size);
        Assert.True(span.Written);

        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 4),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false, 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64, LocalSizeX = 8, ComputeSystemRegisters = Dispatch.Registers,
        }, out var compiled, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(compiled.Spirv);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void UnknownIndexUnmappedSpanAndOffSwitchReadTheActualCount(bool dataIndex, bool mapped, bool proof)
    {
        var reads = 0;
        bool Read(ulong address, out uint value)
        {
            Assert.Equal(0x2000ul, address);
            reads++; value = 7; return true;
        }
        var plan = Plan(CountProgram(dataIndex));
        var spans = DeviceAddressRangePlanner.Evaluate(plan, InputsFor(Read, mapped: mapped, proof: proof));
        var span = Assert.Single(spans);
        Assert.Equal(112ul, span.Size);
        Assert.Equal(1, reads);
        Assert.True(plan.Memory.Find(40)!.DeviceDescriptor);
    }

    [Fact]
    public void DispatchChangesCannotReuseThePreviousFootprint()
    {
        static bool NoRead(ulong address, out uint value) => throw new InvalidOperationException();
        var plan = Plan(CountProgram());
        Assert.Equal(256ul, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, InputsFor(NoRead))).Size);
        Assert.Equal(512ul, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, InputsFor(NoRead, groups: 4))).Size);
    }

    [Fact]
    public void ReturningToACachedShapeRechecksItsMapping()
    {
        var reads = 0;
        bool Read(ulong address, out uint value) { reads++; value = 7; return true; }
        var plan = Plan(CountProgram());
        Assert.Equal(256ul, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, InputsFor(Read))).Size);
        Assert.Equal(512ul, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, InputsFor(Read, groups: 4))).Size);
        Assert.Equal(0, reads);
        Assert.Equal(112ul, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, InputsFor(Read, mapped: false))).Size);
        Assert.Equal(1, reads);
        Assert.Equal(256ul, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, InputsFor(Read))).Size);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void UnmodifiedDescriptorAndCountStayEligibleAcrossLoopHeaders()
    {
        var instructions = CountProgram().Instructions.Where(instruction => instruction.Pc < 36).ToList();
        instructions.AddRange([
            MoveScalar(36, 6, 0),
            BufferAccess(40, "BufferStoreDwordx4", 0, dwords: 4, indexEnabled: true, vectorAddress: 1),
            Sop2(48, "SAddI32", 6, Gen5Operand.Scalar(6), Operand(1)),
            Sopc(52, "SCmpLtU32", Gen5Operand.Scalar(6), Operand(2)),
            Branch(56, "SCbranchScc1", -5), EndProgram(60),
        ]);
        var plan = Plan(Program(instructions.ToArray()));
        Assert.NotNull(plan.DeviceRecordCounts);
        Assert.Empty(plan.TableReads);
        Assert.True(plan.Memory.Find(40)!.DeviceDescriptor);
        static bool NoRead(ulong address, out uint value) => throw new InvalidOperationException();
        Assert.Equal(256ul, Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, InputsFor(NoRead))).Size);
    }

    [Theory]
    [InlineData(0u)] // The descriptor base changes each iteration.
    [InlineData(2u)] // The loaded count changes each iteration.
    public void ChangingDescriptorWordsAcrossLoopHeadersRemainIneligible(uint changedRegister)
    {
        var instructions = CountProgram().Instructions.Where(instruction => instruction.Pc < 36).ToList();
        instructions.AddRange([
            MoveScalar(36, 6, 0),
            BufferAccess(40, "BufferStoreDwordx4", 0, dwords: 4, indexEnabled: true, vectorAddress: 1),
            Sop2(48, "SAddI32", changedRegister, Gen5Operand.Scalar(changedRegister), Operand(16)),
            Sop2(52, "SAddI32", 6, Gen5Operand.Scalar(6), Operand(1)),
            Sopc(56, "SCmpLtU32", Gen5Operand.Scalar(6), Operand(2)),
            Branch(60, "SCbranchScc1", -6), EndProgram(64),
        ]);
        var graph = ScalarValueGraph.Build(Program(instructions.ToArray()), 0, 4);
        Assert.Null(DeviceRecordCountPlan.Create(graph, ShaderStage.Compute));
    }

    [Fact]
    public void UnsupportedDescriptorModeSelectsTheOrdinaryPlan()
    {
        var plan = Plan(CountProgram());
        Assert.False(plan.DeviceRecordCounts!.SupportsDescriptors(plan, new()
        {
            UserData = [0x1000, (16u << 16) | 0x80000000, 0x2000, 0],
        }));
        Assert.False(Plan(CountProgram(), false).Memory.Find(40)!.DeviceDescriptor);
    }

    [Fact]
    public void UnknownBranchesIncludeBothStoreAddresses()
    {
        var program = Program(ScalarLoad(0, 0, 8), Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(0)),
            Branch(12, "SCbranchScc1", 2), Vop1(16, "VMovB32", 1, Operand(20)), Branch(20, "SBranch", 1),
            Vop1(24, "VMovB32", 1, Operand(40)),
            BufferAccess(28, "BufferStoreDword", 0, indexEnabled: true, vectorAddress: 1), EndProgram(36));
        Assert.True(DispatchBufferFootprint.TryEvaluate(program, [0x1000, 4u << 16, 100, 0x16204], 0,
            Dispatch with { GroupsX = 1 }, new HashSet<uint> { 28 }, out var spans));
        Assert.Equal(164ul, spans[28].Size);
    }

    [Theory]
    [InlineData("SCbranchScc1", 1u)]
    [InlineData("SCbranchScc0", 0u)]
    public void KnownUntakenBranchesStillIncludeFallthroughStores(string opcode, uint compared)
    {
        var program = Program(Sopc(0, "SCmpEqU32", Operand(compared), Operand(0)),
            Branch(4, opcode, 4), Vop1(8, "VMovB32", 1, Operand(20)),
            BufferAccess(12, "BufferStoreDword", 0, indexEnabled: true, vectorAddress: 1),
            EndProgram(20), EndProgram(24));
        Assert.True(DispatchBufferFootprint.TryEvaluate(program, [0x1000, 4u << 16, 100, 0x16204], 0,
            Dispatch with { GroupsX = 1 }, new HashSet<uint> { 12 }, out var spans));
        Assert.Equal(84ul, spans[12].Size);
    }

    [Fact]
    public void SdwaComparePoisonsItsActualScalarDestination()
    {
        var compare = Vopc(4, "VCmpLtU32", Gen5Operand.Vector(2), 3) with
        {
            Control = new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, false, 8),
            Destinations = [Gen5Operand.Scalar(8)],
        };
        var program = Program(MoveScalar(0, 8, 0), compare, Vop1(8, "VMovB32", 1, Gen5Operand.Scalar(8)),
            BufferAccess(12, "BufferStoreDword", 0, indexEnabled: true, vectorAddress: 1), EndProgram(20));
        Assert.False(DispatchBufferFootprint.TryEvaluate(program, [0x1000, 4u << 16, 100, 0x16204], 0,
            Dispatch with { GroupsX = 1 }, new HashSet<uint> { 12 }, out _));
    }

    [Fact]
    public void PartialExecWritesPreserveInactiveLanesInTheBound()
    {
        var program = Program(Vop1(0, "VMovB32", 1, Operand(100)),
            Sop1(4, "SMovB64", 126, Operand(1)), Vop1(8, "VMovB32", 1, Operand(0)),
            Sop1(12, "SMovB64", 126, Gen5Operand.Source(193)),
            BufferAccess(16, "BufferStoreDword", 0, indexEnabled: true, vectorAddress: 1), EndProgram(24));
        Assert.True(DispatchBufferFootprint.TryEvaluate(program, [0x1000, 4u << 16, 200, 0x16204], 0,
            Dispatch with { GroupsX = 1 }, new HashSet<uint> { 16 }, out var spans));
        Assert.Equal(404ul, spans[16].Size);
    }

    [Fact]
    public void ReciprocalRoundingAlternativesSurviveIntegerConversion()
    {
        var program = Program(Vop1(0, "VCvtF32U32", 1, Operand(8)),
            Vop1(4, "VRcpIflagF32", 1, Gen5Operand.Vector(1)),
            Vop2(8, "VMulF32", 1, Gen5Operand.Vector(1), Operand(BitConverter.SingleToUInt32Bits(32))),
            Vop1(12, "VCvtU32F32", 1, Gen5Operand.Vector(1)), ReadFirstLane(16, 6, 1),
            Vop1(20, "VMovB32", 1, Gen5Operand.Scalar(6)),
            BufferAccess(24, "BufferStoreDword", 0, indexEnabled: true, vectorAddress: 1), EndProgram(32));
        Assert.True(DispatchBufferFootprint.TryEvaluate(program, [0x1000, 4u << 16, 100, 0x16204], 0,
            Dispatch with { GroupsX = 1 }, new HashSet<uint> { 24 }, out var spans));
        Assert.Equal(20ul, spans[24].Size);
    }
}

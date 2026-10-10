// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Planning of bounded runtime V# candidate tables: a canonical unsigned induction loop
// that reads its descriptor from a static SRT must yield a table; every unproven or
// out-of-range shape must be rejected and stay on the old lowering.
public sealed class BufferCandidateTablePlannerTests
{
    // preheader: s0..s3 = SRT V#, s10 = 0; branch to header
    // header: s12 = s10 * stride; dwordx4 read; formatted load; s10 += 1; guard loop
    private static Gen5ShaderProgram CandidateProgram(
        uint stride = 16,
        uint records = 4,
        string guard = "SCmpLtU32",
        uint guardRegister = 10,
        uint limit = 4,
        bool runtimeLimit = false,
        uint descriptorOffset = 0,
        bool secondField = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction);
            pc += (uint)instruction.Words.Count * sizeof(uint);
        }

        Add(MoveScalar(pc, 0, 0x3000));
        Add(MoveScalar(pc, 1, (stride & 0x3FFF) << 16));
        Add(MoveScalar(pc, 2, records));
        Add(MoveScalar(pc, 3, 0));
        Add(MoveScalar(pc, 10, 0));
        Add(Branch(pc, "SBranch", 0));
        var headerPc = pc;

        Add(Sop2(pc, "SMulI32", 12, Gen5Operand.Scalar(10), Operand(stride)));
        Add(ScalarBufferLoad(pc, 0, destination: 16, count: secondField ? 8u : 4u, immediateOffset: (int)descriptorOffset, dynamicOffsetRegister: 12));
        Add(BufferLoad(pc, 16, formatted: true));
        if (secondField)
            Add(BufferLoad(pc, 20, formatted: true));
        Add(Sop2(pc, "SAddU32", 10, Gen5Operand.Scalar(10), Operand(1)));
        var bound = runtimeLimit ? Gen5Operand.Scalar(7) : Operand(limit);
        Add(Sopc(pc, guard, Gen5Operand.Scalar(guardRegister), bound));
        var branchPc = pc;
        var target = (int)headerPc;
        Add(Branch(branchPc, "SCbranchScc1", (short)((target - (int)(branchPc + 4)) / 4)));
        Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    private static MemoryAccessInfo BufferAccessOf(ShaderResourcePlan plan)
    {
        foreach (var access in plan.Memory.Entries)
        {
            if (access.Opcode.StartsWith("BufferLoadFormat", System.StringComparison.Ordinal))
            {
                return access;
            }
        }

        throw new Xunit.Sdk.XunitException("the program has no formatted buffer load");
    }

    [Fact]
    public void TwoDescriptorFields_DoNotShareACandidateTable()
    {
        var plan = Extract(CandidateProgram(stride: 192, records: 4, descriptorOffset: 160, secondField: true));
        Assert.Equal(2, plan.BufferCandidateTables.Count);
        Assert.Equal(new uint[] { 160, 176 }, plan.BufferCandidateTables.Select(table => table.BaseOffset));
        Assert.All(plan.BufferCandidateTables, table => Assert.Single(table.MemoryIndices));
        var memory = new TestWordMemory { Base = 0x3000, Words = new uint[0x1000 / 4], RequireAlignment = true };
        for (var record = 0; record < 4; record++)
        for (var field = 0; field < 2; field++)
        {
            var address = 0x3000ul + (ulong)record * 192 + 160 + (ulong)field * 16;
            memory.At(address) = 0x4000u + (uint)(record * 2 + field) * 0x100;
            memory.At(address + 4) = 16u << 16;
            memory.At(address + 8) = 4;
            memory.At(address + 12) = 0;
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([], readCleanMemory: memory.Read), ref snapshot, ref specialization));
        Assert.Equal(2, specialization.BufferCandidateTables.Count);
        for (var field = 0; field < 2; field++)
        {
            var table = specialization.BufferCandidateTables[field];
            Assert.Equal(4u, table.CandidateCount);
            for (var record = 0; record < 4; record++)
                Assert.Equal(0x4000u + (uint)(record * 2 + field) * 0x100,
                    snapshot.Buffers[(int)table.FirstCandidate + record][0]);
        }
    }

    [Fact]
    public void DescriptorOffsetBeyondTheAddressSpace_IsRejected()
    {
        var plan = Extract(CandidateProgram(stride: 192, records: 4, descriptorOffset: uint.MaxValue - 15));
        Assert.Empty(plan.BufferCandidateTables);
    }

    [Fact]
    public void DescriptorInsideAStridedRecord_KeepsItsFieldOffset()
    {
        var plan = Extract(CandidateProgram(stride: 192, records: 4, descriptorOffset: 160));
        var table = Assert.Single(plan.BufferCandidateTables);
        Assert.Equal(160u, table.BaseOffset);
        Assert.Equal(160u, table.MinOffset);
        Assert.Equal(752u, table.MaxOffset);
        Assert.Equal(352u, table.StaticCandidateOffset(1));
    }

    [Fact]
    public void CanonicalUnsignedLoop_ProducesCandidateTable()
    {
        var plan = Extract(CandidateProgram());

        var table = Assert.Single(plan.BufferCandidateTables);
        Assert.True(table.IsStaticallyBounded);
        Assert.Equal(4, table.Count);
        Assert.Equal(16u, table.Stride);
        Assert.Equal(0u, table.MinOffset);
        Assert.Equal(64u, table.MaxOffset);
        Assert.NotEqual(DescriptorConstants.NoIndex, table.SourceSrtResource);
        Assert.Equal(64u, table.SrtByteExtent);
        Assert.Equal(BufferDescriptorProvenance.Runtime, BufferAccessOf(plan).BufferDescriptor!.Provenance);
        Assert.False(plan.Info.UsesDeviceAddresses);
    }

    [Fact]
    public void RuntimeLimit_ProducesCandidateTableWithoutStaticCount()
    {
        var plan = Extract(CandidateProgram(runtimeLimit: true));

        var table = Assert.Single(plan.BufferCandidateTables);
        Assert.False(table.IsStaticallyBounded);
        Assert.Equal(-1, table.Count);
        Assert.NotNull(table.Limit);
    }

    [Fact]
    public void PhiWithoutProvableGuard_IsRejected()
    {
        var plan = Extract(CandidateProgram(guardRegister: 7));

        Assert.Empty(plan.BufferCandidateTables);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    [Fact]
    public void SignedGuard_IsRejected()
    {
        var plan = Extract(CandidateProgram(guard: "SCmpLtI32"));

        Assert.Empty(plan.BufferCandidateTables);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    [Fact]
    public void StrideSmallerThanDescriptor_IsRejected()
    {
        var plan = Extract(CandidateProgram(stride: 8));

        Assert.Empty(plan.BufferCandidateTables);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    [Fact]
    public void RangeExceedingSrtExtent_IsRejected()
    {
        var plan = Extract(CandidateProgram(records: 2));

        Assert.Empty(plan.BufferCandidateTables);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    [Fact]
    public void CandidateCountBeyondTheCap_IsRejected()
    {
        var plan = Extract(CandidateProgram(records: 0x100, limit: 0x100));

        Assert.Empty(plan.BufferCandidateTables);
        Assert.True(plan.Info.UsesDeviceAddresses);
    }

    private static TestWordMemory CandidateMemory()
    {
        var memory = new TestWordMemory { Base = 0x3000, Words = new uint[0x2000 / 4], RequireAlignment = true };
        for (var index = 0; index < 4; index++)
        {
            var address = 0x3000 + (ulong)index * 16;
            memory.At(address + 0) = 0x4000u + (uint)index * 0x100;
            memory.At(address + 4) = 16u << 16;
            memory.At(address + 8) = 4;
            memory.At(address + 12) = 0;
        }

        return memory;
    }

    [Fact]
    public void CanonicalTable_MaterializesCandidatesTransactionally()
    {
        var plan = Extract(CandidateProgram());
        var memory = CandidateMemory();
        var inputs = Inputs([], readCleanMemory: memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));

        var table = Assert.Single(specialization.BufferCandidateTables);
        Assert.Equal(4u, table.CandidateCount);
        Assert.Equal(plan.Info.Buffers.Count + 4, specialization.Buffers.Count);
        Assert.Equal(4u, snapshot.FlattenedResourceTable[(int)table.MappingOffset]);

        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(plan.Info.Buffers.Count + 4, applied.Info.Buffers.Count);
        var info = Assert.Single(applied.Info.BufferCandidateTables);
        Assert.Equal(4u, info.CandidateCount);
        Assert.Equal((uint)plan.Info.Buffers.Count, info.FirstCandidate);

        // One unreadable candidate must leave the previous snapshot and specialization intact.
        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;
        memory.FailAddress = 0x3004;
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Same(priorSnapshot, snapshot);
        Assert.Same(priorSpecialization, specialization);
    }

    [Fact]
    public void IdenticalCandidatesShareOneBindingAndProbeKey()
    {
        var plan = Extract(CandidateProgram());
        var memory = CandidateMemory();
        for (var index = 1; index < 4; index++)
        {
            var address = 0x3000 + (ulong)index * 16;
            memory.At(address + 0) = memory.At(0x3000);
        }

        var inputs = Inputs([], readCleanMemory: memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));

        var table = Assert.Single(specialization.BufferCandidateTables);
        Assert.Equal(1u, table.CandidateCount);
        Assert.Equal(1u, snapshot.FlattenedResourceTable[(int)table.MappingOffset]);
    }

    [Fact]
    public void DistinctDescriptorsSharingAProbeKey_AreRejected()
    {
        var plan = Extract(CandidateProgram());
        var memory = CandidateMemory();
        memory.At(0x3010) = memory.At(0x3000);
        memory.At(0x3018) = 8;

        var inputs = Inputs([], readCleanMemory: memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceBranchTests
{
    private static Gen5ShaderProgram ConditionalBuffers(string branch = "SCbranchScc1", bool shared = false) => Program(
        Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
        Branch(4, branch, 3), BufferLoad(8, 0), Branch(16, "SBranch", 2),
        BufferLoad(20, shared ? 0u : 4u), EndProgram(28));

    private static uint[] UserData(uint condition) => [0x1000, 0, 256, 0, 0x2000, 0, 256, 0, condition];

    private static bool CleanRead(ulong address, out uint word) { word = 0; return false; }

    private static ResourceSnapshot Materialize(ShaderResourcePlan plan, uint[] userData, GuestWordReader? cleanReader)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: cleanReader), ref snapshot, ref specialization));
        return snapshot;
    }

    [Theory]
    [InlineData("SCbranchScc1", 0u, 0)]
    [InlineData("SCbranchScc1", 1u, 1)]
    [InlineData("SCbranchScc0", 0u, 1)]
    [InlineData("SCbranchScc0", 1u, 0)]
    public void OnlyTakenUniformBranchMaterializesItsBuffer(string branch, uint condition, int activeBuffer)
    {
        var plan = Extract(ConditionalBuffers(branch));
        Assert.NotEmpty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(condition), CleanRead);
        Assert.Equal(activeBuffer == 0 ? 0x1000u : 0x2000u, snapshot.Buffers[activeBuffer][0]);
        Assert.All(snapshot.Buffers[1 - activeBuffer], word => Assert.Equal(0u, word));
    }

    [Fact]
    public void ChangingTheConditionDoesNotReusePreviousDrawActivity()
    {
        var plan = Extract(ConditionalBuffers());
        var first = Materialize(plan, UserData(0), CleanRead);
        var second = Materialize(plan, UserData(1), CleanRead);
        Assert.Equal(0x1000u, first.Buffers[0][0]);
        Assert.Equal(0u, first.Buffers[1][0]);
        Assert.Equal(0u, second.Buffers[0][0]);
        Assert.Equal(0x2000u, second.Buffers[1][0]);
    }

    [Fact]
    public void MissingCleanReaderKeepsBothPaths()
    {
        var snapshot = Materialize(Extract(ConditionalBuffers()), UserData(1), null);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void ResourceSharedByBothBranchesStaysActive(uint condition)
    {
        var snapshot = Materialize(Extract(ConditionalBuffers(shared: true)), UserData(condition), CleanRead);
        Assert.Equal(0x1000u, Assert.Single(snapshot.Buffers)[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShaderWritesAndAtomicsDisablePruning(bool atomic)
    {
        var instructions = ConditionalBuffers().Instructions.ToArray();
        instructions[2] = atomic ? BufferAtomicAdd(8, 0) : BufferStore(8, 0);
        var plan = Extract(Program(instructions));
        Assert.Empty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(1), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    [Fact]
    public void UnknownBranchConditionKeepsBothPaths()
    {
        var plan = Extract(ConditionalBuffers("SCbranchCdbguser"));
        Assert.Empty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(1), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    [Fact]
    public void UnreadablePredicateDoesNotFallBackToGeneralMemory()
    {
        var plan = Extract(Program(ScalarLoad(0, 12, 8),
            Sopc(8, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)), Branch(12, "SCbranchScc1", 3),
            BufferLoad(16, 0), Branch(24, "SBranch", 2), BufferLoad(28, 4), EndProgram(36)));
        uint[] userData = [.. UserData(1), 0, 0, 0, 0x3000, 0];
        var generalReads = 0;
        bool GeneralRead(ulong address, out uint word) { generalReads++; word = 1; return true; }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, GeneralRead, CleanRead), ref snapshot, ref specialization));
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
        // Flattened reads still use their original reader after the predicate check declines.
        Assert.Equal(plan.TableReads.Count, generalReads);
    }

    [Fact]
    public void InactiveIndirectImageDoesNotReadItsTable()
    {
        var imageInstructions = ResourceTrackerTests.IndirectImageProgram(false).Instructions;
        var endAddress = imageInstructions[^1].Pc;
        var plan = Extract(Program([
            Sopc(0xFF8, "SCmpEqU32", Gen5Operand.Scalar(30), Operand(1)),
            Branch(0xFFC, "SCbranchScc1", checked((short)((endAddress - 0x1000) / 4))),
            .. imageInstructions]));
        Assert.NotEmpty(plan.ResourceBranches);
        var userData = new uint[31];
        uint[] descriptors = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        descriptors.CopyTo(userData, 0);
        userData[30] = 1;
        var reads = 0;
        bool Unreadable(ulong address, out uint word) { reads++; word = 0; return false; }
        var inactive = Materialize(plan, userData, Unreadable);
        Assert.All(Assert.Single(inactive.Images), word => Assert.Equal(0u, word));
        Assert.Equal(0, reads);

        userData[30] = 0;
        var memory = ResourceTrackerTests.LinearMemory();
        var descriptor = ResourceTrackerTests.ImageDescriptor();
        ResourceTrackerTests.WriteImage(memory, 0x2000, descriptor);
        ResourceTrackerTests.WriteImage(memory, 0x2020, descriptor);
        var active = Materialize(plan, userData, memory.Read);
        Assert.Equal(descriptor, Assert.Single(active.Images));
    }

    [Fact]
    public void LoopWithChangingPredicateKeepsAllReachableResources()
    {
        var plan = Extract(Program(MoveScalar(0, 8, 0),
            Sopc(4, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(8, "SCbranchScc1", 4), BufferLoad(12, 0),
            MoveScalar(20, 8, 1), Branch(24, "SBranch", -6),
            BufferLoad(28, 4), EndProgram(36)));
        var snapshot = Materialize(plan, UserData(0), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }

    [Fact]
    public void IndirectControlFlowDisablesPruning()
    {
        var instructions = ConditionalBuffers().Instructions.ToArray();
        instructions[3] = Sop1(16, "SSetpcB64", 12, Gen5Operand.Scalar(12));
        Assert.Empty(Extract(Program(instructions)).ResourceBranches);
    }

    [Fact]
    public void UnknownInnerBranchKeepsBothSuccessors()
    {
        var plan = Extract(Program(
            Sopc(0, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(4, "SCbranchScc1", 6), Branch(8, "SCbranchCdbguser", 3),
            BufferLoad(12, 0), Branch(20, "SBranch", 2), BufferLoad(24, 4), EndProgram(32)));
        Assert.NotEmpty(plan.ResourceBranches);
        var snapshot = Materialize(plan, UserData(0), CleanRead);
        Assert.Equal(0x1000u, snapshot.Buffers[0][0]);
        Assert.Equal(0x2000u, snapshot.Buffers[1][0]);
    }
}

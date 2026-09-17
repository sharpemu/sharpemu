// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceSnapshotStorageTests
{
    [Fact]
    public void WrittenRangeWordsFollowEvaluatedTableWithoutReplacingIt()
    {
        var plan = Extract(Program(
            ScalarLoad(0, 0, 4),
            Vop1(8, "VMovB32", 0, Operand(0)),
            GlobalAccess(12, "GlobalStoreDword", 0, offset: 8),
            EndProgram(20)));
        var memory = new TestWordMemory { Words = [0xAABBCCDD] };
        var inputs = Inputs([0x1000, 0], memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));

        Assert.Single(plan.TableReads);
        Assert.Equal(1, plan.WrittenRangeCount);
        Assert.Equal(new uint[] { 0xAABBCCDD, 0x1008, 0, 4 }, snapshot.FlattenedResourceTable);
        Assert.True(RuntimeValueEvaluator.FlattenResourceTable(plan, inputs, out var tableOnly));
        Assert.Equal(new uint[] { 0xAABBCCDD }, tableOnly);
        tableOnly[0] = 0;
        Assert.Equal(0xAABBCCDDu, snapshot.FlattenedResourceTable[0]);
    }

    [Fact]
    public void IndirectMappingsFollowWrittenRangesAndKeepEarlierSnapshotsIndependent()
    {
        var original = ResourceTrackerTests.IndirectImageProgram(false);
        var plan = Extract(Program([
            .. original.Instructions.Take(original.Instructions.Count - 1),
            Vop1(0x2000, "VMovB32", 0, Operand(0)),
            GlobalAccess(0x2004, "GlobalStoreDword", 0, offset: 8),
            EndProgram(0x200C),
        ]));
        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        var memory = ResourceTrackerTests.LinearMemory();
        var firstImage = ResourceTrackerTests.ImageDescriptor();
        var secondImage = firstImage.ToArray();
        secondImage[0]++;
        ResourceTrackerTests.WriteImage(memory, 0x2000, firstImage);
        ResourceTrackerTests.WriteImage(memory, 0x2020, secondImage);
        memory.At(0x1000 + 36) = 1;
        var inputs = Inputs(userData, readCleanMemory: memory.Read);
        var first = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref first, ref specialization));

        Assert.Equal(1, plan.WrittenRangeCount);
        var range = Assert.Single(first.DeviceAddressRanges);
        var rangeOffset = plan.TableReads.Count;
        Assert.Equal((uint)range.Base, first.FlattenedResourceTable[rangeOffset]);
        Assert.Equal((uint)(range.Base >> 32), first.FlattenedResourceTable[rangeOffset + 1]);
        Assert.Equal((uint)range.Size, first.FlattenedResourceTable[rangeOffset + 2]);
        var mappingOffset = checked((int)specialization.Images[0].IndirectMappingOffset);
        Assert.Equal(plan.FlattenedTableReservedCount, mappingOffset);
        Assert.Equal(new uint[] { 2, 0, 0, 1, 1 }, first.FlattenedResourceTable[mappingOffset..]);
        Assert.Equal(2, first.Images.Length);

        var originalTable = first.FlattenedResourceTable.ToArray();
        var next = new ResourceSnapshot();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref next, ref specialization));
        next.FlattenedResourceTable[mappingOffset] = 0;
        next.Images[1][0] = 0;
        Assert.Equal(originalTable, first.FlattenedResourceTable);
        Assert.Equal(secondImage[0], first.Images[1][0]);
    }
}

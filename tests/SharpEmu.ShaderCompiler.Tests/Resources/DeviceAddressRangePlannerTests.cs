// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DeviceAddressRangePlannerTests
{
    [Fact]
    public void BoundedOffsets_GiveTheLargestExtent()
    {
        var plan = Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalLoadDword", 0, offset: 8),
            GlobalAccess(12, "GlobalLoadDwordx4", 0, offset: 32, dwords: 4),
            EndProgram(20)));

        var range = Assert.Single(plan.DeviceAddressRanges);
        Assert.True(range.Bounded);
        Assert.True(range.Plannable);
        Assert.False(range.Written);
        Assert.Equal(8, range.FirstByte);
        Assert.Equal(48, range.EndByte);
        Assert.Equal(40ul, range.Extent);
        Assert.Equal(2, range.MemoryIndices.Count);
        var evaluated = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1000, 0])));
        Assert.True(evaluated.Planned);
        Assert.Equal(0x1008ul, evaluated.Base);
        Assert.Equal(40ul, evaluated.Size);
    }

    // A store 8 bytes below the handle starts the range there; the range covers the
    // lowest byte through the end of the highest access.
    [Fact]
    public void NegativeOffset_StartsTheRangeAtTheLowestByte()
    {
        var plan = Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalStoreDword", 0, offset: -8),
            GlobalAccess(12, "GlobalLoadDwordx2", 0, offset: 8, dwords: 2),
            EndProgram(20)));

        var range = Assert.Single(plan.DeviceAddressRanges);
        Assert.True(range.Bounded);
        Assert.True(range.Written);
        Assert.Equal(-8, range.FirstByte);
        Assert.Equal(16, range.EndByte);
        Assert.Equal(24ul, range.Extent);
        var evaluated = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x2008, 0])));
        Assert.True(evaluated.Planned);
        Assert.Equal(0x2000ul, evaluated.Base);
        Assert.Equal(24ul, evaluated.Size);

        var storeOnly = Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalStoreDword", 0, offset: -8),
            EndProgram(12)));
        var single = Assert.Single(DeviceAddressRangePlanner.Evaluate(storeOnly, Inputs([0x2008, 0])));
        Assert.Equal(0x2000ul, single.Base);
        Assert.Equal(4ul, single.Size);
    }

    [Fact]
    public void RuntimeOffset_ExtendsToTheMappedEndUpToTheCap()
    {
        var plan = Extract(Program(
            GlobalAccess(0, "GlobalLoadDword", 0, offset: 8, vectorAddress: 0),
            EndProgram(8)));

        var range = Assert.Single(plan.DeviceAddressRanges);
        Assert.False(range.Bounded);
        Assert.True(range.Plannable);
        var evaluated = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1000, 0])));
        Assert.True(evaluated.Planned);
        Assert.Equal(DeviceAddressRangePlanner.MaxRangeBytes, evaluated.Size);
    }

    [Fact]
    public void UnplannableHandle_DoesNotFailThePlan()
    {
        var plan = Extract(Program(GlobalAccess(0, "FlatLoadDword", 0, vectorAddress: 2), EndProgram(8)));

        Assert.True(plan.Info.UsesDeviceAddresses);
        var range = Assert.Single(plan.DeviceAddressRanges);
        Assert.False(range.Plannable);
        var evaluated = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([])));
        Assert.False(evaluated.Planned);
        Assert.Equal(0ul, evaluated.Size);
    }

    [Fact]
    public void WrittenUnplannableHandle_IsReportedForTheHost()
    {
        var plan = Extract(Program(GlobalAccess(0, "FlatStoreDword", 0, vectorAddress: 2), EndProgram(8)));

        var range = Assert.Single(plan.DeviceAddressRanges);
        Assert.True(range.Written);
        Assert.False(range.Plannable);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([]), ref snapshot, ref specialization));
        var reported = Assert.Single(snapshot.DeviceAddressRanges);
        Assert.True(reported.Written);
        Assert.False(reported.Planned);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DeviceAddressRangePlannerTests
{
    // flat_store through (aperture_hi << 32) | offset: the high dword comes from an
    // aperture operand, so the store targets LDS or scratch and owns no device range.
    [Theory]
    [InlineData(Gen5InlineConstants.SharedBase, FlatAddressSpace.Shared)]
    [InlineData(Gen5InlineConstants.PrivateBase, FlatAddressSpace.Private)]
    public void FlatAddressFromAnAperture_IsLocalAndHasNoDeviceRange(uint aperture, FlatAddressSpace expected)
    {
        var plan = Extract(Program(
            Sop1(0, "SMovB32", 1, Gen5Operand.Source(aperture)),
            Vop1(4, "VMovB32", 1, Gen5Operand.Scalar(1)),
            Vop1(8, "VMovB32", 0, Operand(16)),
            GlobalAccess(12, "FlatStoreDword", 0, vectorAddress: 0),
            EndProgram(20)));

        Assert.Empty(plan.DeviceAddressRanges);
        Assert.True(plan.Memory.TryGetIndex(12, 0, out var index));
        Assert.Equal(expected, plan.Memory[index].AddressSpace);
    }

    // A high dword clamped between the hard-coded PS5 aperture bounds
    // (v_med3_u32 0x80000000, 0x70000000, sp) always lands in LDS or scratch.
    [Fact]
    public void FlatAddressClampedIntoTheApertures_IsLocal()
    {
        var plan = Extract(Program(
            Vop3(0, "VMed3U32", 1, Operand(0x8000_0000), Operand(0x7000_0000), Gen5Operand.Vector(7)),
            Vop1(8, "VMovB32", 0, Operand(16)),
            GlobalAccess(12, "FlatStoreDword", 0, vectorAddress: 0),
            EndProgram(20)));

        Assert.Empty(plan.DeviceAddressRanges);
        Assert.True(plan.Memory.TryGetIndex(12, 0, out var index));
        Assert.Equal(FlatAddressSpace.SharedOrPrivate, plan.Memory[index].AddressSpace);
    }

    // After v_cmpx the clamp is a lane-masked write whose inactive lanes keep an
    // unknown value; those lanes do not store, so the address is still local.
    [Fact]
    public void ClampedAddressWrittenUnderTheStoresExecMask_IsLocal()
    {
        var plan = Extract(Program(
            Vopc(0, "VCmpxLtU32", Operand(3), 5),
            Vop3(4, "VMed3U32", 1, Operand(0x8000_0000), Operand(0x7000_0000), Gen5Operand.Vector(7)),
            Vop1(12, "VMovB32", 0, Operand(16)),
            GlobalAccess(16, "FlatStoreDword", 0, vectorAddress: 0),
            EndProgram(24)));

        Assert.Empty(plan.DeviceAddressRanges);
        Assert.True(plan.Memory.TryGetIndex(16, 0, out var index));
        Assert.Equal(FlatAddressSpace.SharedOrPrivate, plan.Memory[index].AddressSpace);
    }

    // Without an aperture the same flat store is a device access and keeps its range.
    [Fact]
    public void FlatAddressWithoutAperture_StaysGlobal()
    {
        var plan = Extract(Program(
            Vop1(0, "VMovB32", 1, Operand(0)),
            Vop1(4, "VMovB32", 0, Operand(0x1000)),
            GlobalAccess(8, "FlatStoreDword", 0, vectorAddress: 0),
            EndProgram(16)));

        var range = Assert.Single(plan.DeviceAddressRanges);
        Assert.True(range.Written);
        Assert.True(plan.Memory.TryGetIndex(8, 0, out var index));
        Assert.Equal(FlatAddressSpace.Global, plan.Memory[index].AddressSpace);
    }

    // A 32-bit read returns the aperture's high dword; a 64-bit read the full address.
    [Fact]
    public void ApertureOperands_DecodeConsistentlyAsHighDwordAndAddress()
    {
        Assert.True(Gen5InlineConstants.TryDecode(Gen5InlineConstants.SharedBase, out var shared));
        Assert.Equal(Gen5InlineConstants.SharedApertureHigh, shared);
        Assert.True(Gen5InlineConstants.TryDecode(Gen5InlineConstants.PrivateLimit, out var privateLimit));
        Assert.Equal(Gen5InlineConstants.PrivateApertureHigh, privateLimit);
        Assert.Equal(0x7000_0000_0000_0000ul, Gen5InlineConstants.DecodeAperture64(Gen5InlineConstants.SharedBase));
        Assert.Equal(0x7000_0000_FFFF_FFFFul, Gen5InlineConstants.DecodeAperture64(Gen5InlineConstants.SharedLimit));
        Assert.Equal(0x8000_0000_0000_0000ul, Gen5InlineConstants.DecodeAperture64(Gen5InlineConstants.PrivateBase));
    }

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

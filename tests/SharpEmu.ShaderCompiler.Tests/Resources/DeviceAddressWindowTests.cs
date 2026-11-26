// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Which programs reach only their handles' 4 GiB windows.
public sealed class DeviceAddressWindowTests
{
    private const ulong Window = DeviceAddressRangePlanner.WindowOffsetBytes;

    // global_load through s[0:1] with a vector offset: base + zext(v0) + 8, four bytes, ends before base + 2^32 + 12.
    [Fact]
    public void AGlobalReadWithAScalarBaseIsWindowed()
    {
        var plan = Extract(Program(GlobalAccess(0, "GlobalLoadDword", 0, offset: 8, vectorAddress: 0), EndProgram(8)));

        Assert.False(plan.DeviceAddressReadsBounded);
        Assert.True(plan.DeviceAddressReadsWindowed);
        var range = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1000, 0])));
        Assert.True(range.Planned);
        Assert.Equal(0x1000ul, range.Base);
        Assert.True(range.Base + plan.DeviceAddressWindowBytes >= 0x1000ul + Window - 1 + 8 + 4);
    }

    // A negative immediate reaches below the base; the host's range base starts there and the window still covers the top.
    [Fact]
    public void ANegativeImmediateStartsTheWindowBelowTheBase()
    {
        var plan = Extract(Program(
            GlobalAccess(0, "GlobalLoadDword", 0, offset: -16, vectorAddress: 0),
            GlobalAccess(8, "GlobalLoadDwordx2", 0, offset: 24, vectorAddress: 0, dwords: 2),
            EndProgram(16)));

        Assert.True(plan.DeviceAddressReadsWindowed);
        var range = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x1000, 0])));
        Assert.Equal(0x1000ul - 16, range.Base);
        Assert.True(range.Base + plan.DeviceAddressWindowBytes >= 0x1000ul + Window - 1 + 24 + 8);
    }

    // s_load through s[0:1] plus a run-time s4: the offset is zero-extended, so the read stays in the window.
    [Fact]
    public void AScalarReadWithARunTimeOffsetIsWindowed()
    {
        var plan = Extract(Program(ScalarLoad(0, 0, 8, count: 2, immediateOffset: 4, dynamicOffsetRegister: 4), EndProgram(8)));

        Assert.True(plan.DeviceAddressReadsWindowed);
        var range = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x2000, 0])));
        Assert.True(range.Base <= 0x2000ul + 4);
        Assert.True(range.Base + plan.DeviceAddressWindowBytes >= 0x2000ul + Window - 1 + 4 + 8);
    }

    // A flat read addresses the whole 64-bit vector pair: no window holds it.
    [Fact]
    public void AFlatReadIsNotWindowed()
    {
        var plan = Extract(Program(GlobalAccess(0, "FlatLoadDword", 0, vectorAddress: 2), EndProgram(8)));

        Assert.False(plan.DeviceAddressReadsWindowed);
        Assert.Equal(0ul, plan.DeviceAddressWindowBytes);
    }

    // A windowed read beside a flat read: the program keeps the full sweep.
    [Fact]
    public void AFlatReadBesideAWindowedReadKeepsTheFullSweep()
    {
        var plan = Extract(Program(
            GlobalAccess(0, "GlobalLoadDword", 0, offset: 8, vectorAddress: 0),
            GlobalAccess(8, "FlatLoadDword", 0, vectorAddress: 2),
            EndProgram(16)));

        Assert.False(plan.DeviceAddressReadsWindowed);
    }

    // A store through the handle: the written range keeps the full sweep.
    [Fact]
    public void AWrittenRangeIsNotWindowed()
    {
        var plan = Extract(Program(GlobalAccess(0, "GlobalStoreDword", 0, vectorAddress: 0), EndProgram(8)));

        Assert.False(plan.DeviceAddressReadsWindowed);
    }

    // Exactly bounded reads stay in the bounded class.
    [Fact]
    public void ABoundedProgramIsNotWindowed()
    {
        var plan = Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalLoadDword", 0, offset: 8),
            EndProgram(12)));

        Assert.True(plan.DeviceAddressReadsBounded);
        Assert.False(plan.DeviceAddressReadsWindowed);
    }
}

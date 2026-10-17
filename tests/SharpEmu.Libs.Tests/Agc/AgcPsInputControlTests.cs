// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcPsInputControlTests
{
    private const uint SpiPsInputCntl0 = 0x191;
    private const uint SpiPsInControl = 0x1B6;

    [Fact]
    public void InputCountUsesProgrammedNumInterpField()
    {
        Dictionary<uint, uint> registers = new()
        {
            [SpiPsInControl] = 0xFFFF_FFC2,
        };

        Assert.Equal(2u, AgcExports.GetPsInputCount(registers, 9));
    }

    [Fact]
    public void InputCountFallsBackAndClampsToHardwareLimit()
    {
        Assert.Equal(7u, AgcExports.GetPsInputCount(new Dictionary<uint, uint>(), 7));
        Assert.Equal(32u, AgcExports.GetPsInputCount(new Dictionary<uint, uint>(), 40));
    }

    [Fact]
    public void ProgrammedZeroCountDoesNotUseFallbackOrReadStaleSlots()
    {
        Dictionary<uint, uint> registers = new()
        {
            [SpiPsInControl] = 0,
            [SpiPsInputCntl0] = 31,
        };

        var count = AgcExports.GetPsInputCount(registers, 4);

        Assert.Equal(0u, count);
        Assert.Empty(AgcExports.ReadPsInputCntlRegisters(registers, count));
    }

    [Fact]
    public void ProgrammedCountAndSnapshotStayWithinHardwareLimit()
    {
        Dictionary<uint, uint> registers = new()
        {
            [SpiPsInControl] = 63,
            [SpiPsInputCntl0 + 32] = 7,
        };

        Assert.Equal(32u, AgcExports.GetPsInputCount(registers, 1));
        Assert.Equal(
            Enumerable.Range(0, 32).Select(index => (uint)index),
            AgcExports.ReadPsInputCntlRegisters(registers, 63));
    }

    [Fact]
    public void InputControlSnapshotIncludesOnlyActiveSlots()
    {
        Dictionary<uint, uint> registers = new()
        {
            [SpiPsInputCntl0] = 5,
            [SpiPsInputCntl0 + 1] = 7,
            [SpiPsInputCntl0 + 2] = 9,
        };

        var controls = AgcExports.ReadPsInputCntlRegisters(registers, 2);

        Assert.Equal([5u, 7u], controls);
    }

    [Fact]
    public void UnprogrammedActiveSlotUsesIdentityMapping()
    {
        Dictionary<uint, uint> registers = new()
        {
            [SpiPsInputCntl0] = 5,
        };

        var controls = AgcExports.ReadPsInputCntlRegisters(registers, 3);

        Assert.Equal([5u, 1u, 2u], controls);
    }
}

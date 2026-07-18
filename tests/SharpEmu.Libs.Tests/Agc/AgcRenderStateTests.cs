// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRenderStateTests
{
    private const uint PaScScreenScissorTl = 0x0C;
    private const uint PaScScreenScissorBr = 0x0D;
    private const uint PaScWindowScissorTl = 0x81;
    private const uint PaScWindowScissorBr = 0x82;

    [Fact]
    public void ApplyIndirectRegister_AcceptsContextRegisterZero()
    {
        var registers = new Dictionary<uint, uint>();

        AgcExports.ApplyIndirectRegister(registers, 0, 0x12345678);

        Assert.Equal(0x12345678u, registers[0]);
    }

    [Fact]
    public void ApplyIndirectRegister_SkipsInvalidOffsetSentinel()
    {
        var registers = new Dictionary<uint, uint>();

        AgcExports.ApplyIndirectRegister(registers, uint.MaxValue, 0x12345678);

        Assert.Empty(registers);
    }

    [Fact]
    public void DecodeScissor_ScreenCoordinatesAreSigned16Bit()
    {
        var registers = new Dictionary<uint, uint>
        {
            [PaScScreenScissorTl] = Pack(unchecked((ushort)-10), unchecked((ushort)-20)),
            [PaScScreenScissorBr] = Pack(100, 80),
        };

        Assert.Equal(
            new GuestRect(0, 0, 100, 80),
            AgcExports.DecodeScissor(registers, 200, 160));
    }

    [Fact]
    public void DecodeScissor_WindowCoordinatesRemainUnsigned15Bit()
    {
        var registers = new Dictionary<uint, uint>
        {
            [PaScWindowScissorTl] = 0x80000000,
            [PaScWindowScissorBr] = Pack(0x8064, 100),
        };

        Assert.Equal(
            new GuestRect(0, 0, 100, 100),
            AgcExports.DecodeScissor(registers, 200, 200));
    }

    [Fact]
    public void DecodeScissor_ExplicitZeroAreaIsPreserved()
    {
        var registers = new Dictionary<uint, uint>
        {
            [PaScScreenScissorTl] = Pack(10, 20),
            [PaScScreenScissorBr] = Pack(10, 20),
        };

        Assert.Equal(
            new GuestRect(10, 20, 0, 0),
            AgcExports.DecodeScissor(registers, 200, 200));
    }

    [Fact]
    public void DecodeScissor_FullTargetHasNoVulkanOverride()
    {
        var registers = new Dictionary<uint, uint>
        {
            [PaScScreenScissorTl] = 0,
            [PaScScreenScissorBr] = Pack(1920, 1080),
        };

        Assert.Null(AgcExports.DecodeScissor(registers, 1920, 1080));
    }

    private static uint Pack(ushort x, ushort y) => x | ((uint)y << 16);
}

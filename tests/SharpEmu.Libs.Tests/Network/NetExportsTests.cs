// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

// The libSceNet byte-order helpers take their operand in Rdi and return the converted value in
// Rax. They swap endianness unconditionally, which is correct on the little-endian hosts (and
// little-endian guest) the emulator targets, so network (big-endian) order is always a byte swap.
public sealed class NetExportsTests
{
    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    [Fact]
    public void Htonl_SwapsAllFourBytes()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;

        Assert.Equal(0, NetExports.NetHtonl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohl_SwapsAllFourBytes()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;

        Assert.Equal(0, NetExports.NetNtohl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Htons_SwapsLowTwoBytesOnly()
    {
        // High bits above the 16-bit short must be ignored, not folded into the result.
        _ctx[CpuRegister.Rdi] = 0xFFFF_0102;

        Assert.Equal(0, NetExports.NetHtons(_ctx));
        Assert.Equal(0x0201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohs_SwapsLowTwoBytesOnly()
    {
        _ctx[CpuRegister.Rdi] = 0xFFFF_0102;

        Assert.Equal(0, NetExports.NetNtohs(_ctx));
        Assert.Equal(0x0201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Htonl_IgnoresBitsAboveThe32BitWord()
    {
        _ctx[CpuRegister.Rdi] = 0xDEADBEEF_01020304;

        Assert.Equal(0, NetExports.NetHtonl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0xDEADBEEFUL)]
    [InlineData(0x00000000UL)]
    [InlineData(0xFFFFFFFFUL)]
    [InlineData(0x00000001UL)]
    public void HtonlThenNtohl_RoundTripsToOriginal(ulong value)
    {
        _ctx[CpuRegister.Rdi] = value;
        NetExports.NetHtonl(_ctx);

        _ctx[CpuRegister.Rdi] = _ctx[CpuRegister.Rax];
        NetExports.NetNtohl(_ctx);

        Assert.Equal(value, _ctx[CpuRegister.Rax]);
    }

    // Regression guard: a non-palindromic value must not come back as 0. The functions previously
    // computed the swap into Rax and then called SetReturn(0), which overwrote Rax, so every
    // sceNetHtonl/Htons/Ntohl/Ntohs call returned 0 regardless of input.
    [Fact]
    public void ByteOrderConversions_DoNotReturnZeroForNonZeroInput()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;
        NetExports.NetHtonl(_ctx);
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rax] = 0;
        _ctx[CpuRegister.Rdi] = 0x0102;
        NetExports.NetHtons(_ctx);
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
    }
}

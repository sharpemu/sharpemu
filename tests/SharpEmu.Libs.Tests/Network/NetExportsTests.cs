// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class NetExportsTests
{
    [Fact]
    public void Htonl_SwapsBytesAndPreservesRax()
    {
        var context = NewContext();
        context[CpuRegister.Rdi] = 0x12345678UL;

        var result = NetExports.NetHtonl(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x78563412UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Htons_SwapsBytesAndPreservesRax()
    {
        var context = NewContext();
        context[CpuRegister.Rdi] = 0x1234UL;

        var result = NetExports.NetHtons(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x3412UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohl_RoundTripsHtonl()
    {
        var first = NewContext();
        first[CpuRegister.Rdi] = 0xCAFEBABEU;
        NetExports.NetHtonl(first);
        var swapped = first[CpuRegister.Rax];

        var second = NewContext();
        second[CpuRegister.Rdi] = swapped;
        NetExports.NetNtohl(second);

        Assert.Equal(0xCAFEBABEUL, second[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohs_RoundTripsHtons()
    {
        var first = NewContext();
        first[CpuRegister.Rdi] = 0xBEEFUL;
        NetExports.NetHtons(first);
        var swapped = first[CpuRegister.Rax];

        var second = NewContext();
        second[CpuRegister.Rdi] = swapped;
        NetExports.NetNtohs(second);

        Assert.Equal(0xBEEFUL, second[CpuRegister.Rax]);
    }

    [Fact]
    public void Htonl_ZeroInputReturnsZero()
    {
        // The one case where the broken implementation happened to return the
        // correct value: swapping 0 yields 0. Guards against accidentally
        // changing the dispatch status from OK on the zero path.
        var context = NewContext();
        context[CpuRegister.Rdi] = 0UL;

        var result = NetExports.NetHtonl(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Htonl_NonZeroInputNeverReturnsZero()
    {
        // Regression guard for the original bug: with the bug present, every
        // call returned 0 to the guest. This test would have failed before
        // the fix.
        var rng = new Random(0x4E54_5400);
        for (var i = 0; i < 1000; i++)
        {
            var input = unchecked((uint)rng.Next(int.MinValue, int.MaxValue));
            var context = NewContext();
            context[CpuRegister.Rdi] = input;

            NetExports.NetHtonl(context);

            Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        }
    }

    [Fact]
    public void Htons_NonZeroInputNeverReturnsZero()
    {
        var rng = new Random(0x4E54_5401);
        for (var i = 0; i < 1000; i++)
        {
            var input = unchecked((ushort)rng.Next(1, 65536));
            var context = NewContext();
            context[CpuRegister.Rdi] = input;

            NetExports.NetHtons(context);

            Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        }
    }

    [Fact]
    public void Htons_OnlyTouchesLowTwoBytes()
    {
        // The cast `unchecked((ushort)ctx[CpuRegister.Rdi])` masks the input
        // to its low 16 bits, so bits above 0xFFFF never reach the swap.
        // Verify that: a 32-bit input where the high half differs swaps only
        // the masked low half, leaving Rax a 16-bit-only value.
        var context = NewContext();
        context[CpuRegister.Rdi] = 0xCAFE_1234UL;

        NetExports.NetHtons(context);

        Assert.Equal(0x3412UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohl_NonZeroInputNeverReturnsZero()
    {
        var rng = new Random(0x4E54_5402);
        for (var i = 0; i < 1000; i++)
        {
            var input = unchecked((uint)rng.Next(int.MinValue, int.MaxValue));
            var context = NewContext();
            context[CpuRegister.Rdi] = input;

            NetExports.NetNtohl(context);

            Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        }
    }

    [Fact]
    public void Ntohs_NonZeroInputNeverReturnsZero()
    {
        var rng = new Random(0x4E54_5403);
        for (var i = 0; i < 1000; i++)
        {
            var input = unchecked((ushort)rng.Next(1, 65536));
            var context = NewContext();
            context[CpuRegister.Rdi] = input;

            NetExports.NetNtohs(context);

            Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        }
    }

    private static CpuContext NewContext()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }
}
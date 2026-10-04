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
    public void SocketDescriptorsFitGuestFdSet()
    {
        _ctx[CpuRegister.Rsi] = 2; // AF_INET
        _ctx[CpuRegister.Rdx] = 2; // SOCK_DGRAM
        _ctx[CpuRegister.Rcx] = 17; // UDP

        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var first = _ctx[CpuRegister.Rax];
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var second = _ctx[CpuRegister.Rax];

        try
        {
            Assert.InRange(first, 256UL, 1023UL);
            Assert.InRange(second, 256UL, 1023UL);
            Assert.NotEqual(first, second);
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = first;
            NetExports.NetSocketClose(_ctx);
            _ctx[CpuRegister.Rdi] = second;
            NetExports.NetSocketClose(_ctx);
        }
    }

    [Fact]
    public void NonblockingReceiveWithoutDataReturnsWouldBlock()
    {
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 2;
        _ctx[CpuRegister.Rcx] = 17;
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var socket = _ctx[CpuRegister.Rax];

        try
        {
            _ctx[CpuRegister.Rdi] = socket;
            _ctx[CpuRegister.Rsi] = 0x1_0000_0000;
            _ctx[CpuRegister.Rdx] = 1;
            _ctx[CpuRegister.Rcx] = 0x80;
            Assert.Equal(unchecked((int)0x80410123), NetExports.NetRecv(_ctx));
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = socket;
            NetExports.NetSocketClose(_ctx);
        }
    }

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

    [Fact]
    public void GetMacAddress_WritesDeterministicOfflineAddressAndValidatesArguments()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong address = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(address, new byte[] { 1, 2, 3, 4, 5, 6 }));

        ctx[CpuRegister.Rdi] = address;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, NetExports.NetGetMacAddress(ctx));

        var result = new byte[6];
        Assert.True(memory.TryRead(address, result));
        Assert.Equal(new byte[] { 0x02, 0x53, 0x48, 0x41, 0x52, 0x50 }, result);

        ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(unchecked((int)0x80410116), NetExports.NetGetMacAddress(ctx));
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x80410116), NetExports.NetGetMacAddress(ctx));
    }

    [Fact]
    public void EtherNtostr_FormatsSixBytesAndChecksGuestBuffers()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong address = memoryBase + 0x100;
        const ulong destination = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(address, new byte[] { 0x02, 0x1A, 0xB0, 0x04, 0xFE, 0x7D }));

        ctx[CpuRegister.Rdi] = address;
        ctx[CpuRegister.Rsi] = destination;
        ctx[CpuRegister.Rdx] = 18;
        Assert.Equal(0, NetExports.NetEtherNtostr(ctx));

        var result = new byte[18];
        Assert.True(memory.TryRead(destination, result));
        Assert.Equal("02:1a:b0:04:fe:7d\0", System.Text.Encoding.ASCII.GetString(result));

        ctx[CpuRegister.Rdx] = 17;
        Assert.Equal(unchecked((int)0x80410116), NetExports.NetEtherNtostr(ctx));
        ctx[CpuRegister.Rdx] = 18;
        ctx[CpuRegister.Rsi] = memoryBase + 0x2000;
        Assert.Equal(unchecked((int)0x80410116), NetExports.NetEtherNtostr(ctx));
    }

    [Theory]
    [InlineData("6Oc0bLsIYe0", "sceNetGetMacAddress")]
    [InlineData("v6M4txecCuo", "sceNetEtherNtostr")]
    public void MacAddressExports_RegisterPs5Nids(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceNet", export.LibraryName);
    }

    [Fact]
    public void EpollCreate_AllocatesBoundedIdsAndValidatesItsArguments()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(nameAddress, System.Text.Encoding.UTF8.GetBytes("matching\0")));

        NetExports.ResetEpollsForTests();
        try
        {
            ctx[CpuRegister.Rdi] = nameAddress;
            ctx[CpuRegister.Rsi] = 0;
            Assert.Equal(1024, NetExports.NetEpollCreate(ctx));
            Assert.Equal(1024UL, ctx[CpuRegister.Rax]);
            Assert.Equal(1025, NetExports.NetEpollCreate(ctx));

            ctx[CpuRegister.Rsi] = 1;
            Assert.Equal(unchecked((int)0x80410116), NetExports.NetEpollCreate(ctx));
            ctx[CpuRegister.Rsi] = 0;
            ctx[CpuRegister.Rdi] = 0;
            Assert.Equal(unchecked((int)0x80410116), NetExports.NetEpollCreate(ctx));
        }
        finally
        {
            NetExports.ResetEpollsForTests();
        }
    }

    [Theory]
    [InlineData("SF47kB2MNTo", "sceNetEpollCreate")]
    [InlineData("ZVw46bsasAk", "sceNetEpollControl")]
    [InlineData("drjIbDbA7UQ", "sceNetEpollWait")]
    [InlineData("Inp1lfL+Jdw", "sceNetEpollDestroy")]
    public void EpollExports_RegisterWithCatalogIdentity(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceNet", export.LibraryName);
    }

    [Fact]
    public void EpollWait_EmptySetReturnsNoEventsAndValidatesArguments()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong nameAddress = memoryBase + 0x100;
        const ulong eventsAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(nameAddress, System.Text.Encoding.UTF8.GetBytes("matching\0")));

        NetExports.ResetEpollsForTests();
        try
        {
            ctx[CpuRegister.Rdi] = nameAddress;
            ctx[CpuRegister.Rsi] = 0;
            var epollId = NetExports.NetEpollCreate(ctx);

            ctx[CpuRegister.Rdi] = unchecked((ulong)epollId);
            ctx[CpuRegister.Rsi] = eventsAddress;
            ctx[CpuRegister.Rdx] = 1;
            ctx[CpuRegister.Rcx] = 0;
            Assert.Equal(0, NetExports.NetEpollWait(ctx));

            ctx[CpuRegister.Rsi] = 0;
            Assert.Equal(unchecked((int)0x8041010E), NetExports.NetEpollWait(ctx));
            ctx[CpuRegister.Rsi] = eventsAddress;
            ctx[CpuRegister.Rdx] = 0;
            Assert.Equal(unchecked((int)0x80410116), NetExports.NetEpollWait(ctx));
            ctx[CpuRegister.Rdx] = 1;
            ctx[CpuRegister.Rcx] = ulong.MaxValue - 1;
            Assert.Equal(unchecked((int)0x80410116), NetExports.NetEpollWait(ctx));
        }
        finally
        {
            NetExports.ResetEpollsForTests();
        }
    }
}

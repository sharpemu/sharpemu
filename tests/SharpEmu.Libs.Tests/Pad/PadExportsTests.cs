// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    /// <summary>
    /// Mirrors the calling frame observed in PPSA10112: the out-param points at
    /// rbp-0x30 and the caller's stack cookie sits at rbp-0x28, so the state is
    /// eight bytes. Writing more would smash the cookie and fail the guest's
    /// stack check, which is the failure mode this size guards against.
    /// </summary>
    [Fact]
    public void GetTriggerEffectState_WritesEightBytesAndLeavesTheCookieIntact()
    {
        const ulong stateAddress = Base + 0x100;
        const ulong cookieAddress = stateAddress + 8;
        const ulong cookie = 0xC0DEC0DECAFEBA00UL;

        Assert.True(_memory.TryWrite(stateAddress, new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE }));
        Assert.True(_memory.TryWrite(cookieAddress, BitConverter.GetBytes(cookie)));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> state = stackalloc byte[8];
        Assert.True(_memory.TryRead(stateAddress, state));
        foreach (var value in state)
        {
            Assert.Equal(0, value);
        }

        Span<byte> guard = stackalloc byte[8];
        Assert.True(_memory.TryRead(cookieAddress, guard));
        Assert.Equal(cookie, BitConverter.ToUInt64(guard));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void GetTriggerEffectState_RejectsForeignHandles(int handle)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
    }

    [NativeX64Fact]
    public void ReadState_RejectsHandleZeroOnceAPadIsOpen()
    {
        const ulong dataAddress = Base + 0x200;
        try
        {
            PadExports.PadInit(_ctx);
            _ctx[CpuRegister.Rdi] = 0x10000000;
            _ctx[CpuRegister.Rsi] = 0;
            _ctx[CpuRegister.Rdx] = 0;
            _ctx[CpuRegister.Rcx] = 0;
            Assert.Equal(1, PadExports.PadOpen(_ctx));

            _ctx[CpuRegister.Rdi] = 0;
            _ctx[CpuRegister.Rsi] = dataAddress;
            Assert.Equal(InvalidHandle, PadExports.PadReadState(_ctx));

            _ctx[CpuRegister.Rdi] = 1;
            _ctx[CpuRegister.Rsi] = dataAddress;
            Assert.Equal(0, PadExports.PadReadState(_ctx));
        }
        finally
        {
            PadExports.ResetOpenedPadForTests();
        }
    }
}

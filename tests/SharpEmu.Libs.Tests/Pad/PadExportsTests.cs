// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        PadExports.ResetOpenedPadForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => PadExports.ResetOpenedPadForTests();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(15, 12)]
    public void DeviceClassParseData_BoundsThePayloadAndPreservesAdjacentMemory(int length, int copied)
    {
        var padData = new byte[0x78];
        padData[0x4C] = 1;
        padData[0x6B] = (byte)length;
        for (var index = 0; index < 12; index++)
        {
            padData[0x6C + index] = (byte)(index + 1);
        }
        Assert.True(_memory.TryWrite(Base + 0x100, padData));
        Assert.True(_memory.TryWrite(Base + 0x200, Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        _ctx[CpuRegister.Rdx] = Base + 0x200;

        Assert.Equal(0, PadExports.PadDeviceClassParseData(_ctx));
        var output = new byte[32];
        Assert.True(_memory.TryRead(Base + 0x200, output));
        Assert.Equal(copied == 0 ? 0 : -1, BitConverter.ToInt32(output));
        Assert.Equal(1, output[4]);
        Assert.Equal(copied, output[8]);
        Assert.Equal(padData.AsSpan(0x6C, copied).ToArray(), output.AsSpan(12, copied).ToArray());
        Assert.All(output[24..], value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void DeviceClassGetExtendedInformation_LeavesAdjacentMemoryIntact()
    {
        const ulong address = Base + 0x100;
        Assert.True(_memory.TryWrite(address, Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = address;
        Assert.Equal(0, PadExports.PadDeviceClassGetExtendedInformation(_ctx));
        var output = new byte[32];
        Assert.True(_memory.TryRead(address, output));
        Assert.All(output[..0x14], value => Assert.Equal(0, value));
        Assert.All(output[0x14..], value => Assert.Equal(0xA5, value));
    }

    [NativeX64Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        if (handle == 1)
        {
            Assert.Equal(1, OpenPad(0x10000000, 0));
        }

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    // ABI: int scePadResetOrientation(int32_t handle) — handle only, no out
    // parameter, so the only failure mode is a bad handle.
    [NativeX64Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void ResetOrientation_ValidatesHandle(int handle, int expected)
    {
        if (handle == 1)
        {
            Assert.Equal(1, OpenPad(0x10000000, 0));
        }

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadResetOrientation(_ctx));
        Assert.Equal(unchecked((ulong)expected), _ctx[CpuRegister.Rax]);
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

    [Fact]
    public void GetExtControllerInformation_DoesNotOverwriteCallerCookie()
    {
        const ulong informationAddress = Base + 0x100;
        const ulong cookieAddress = informationAddress + 0x30;
        const ulong cookie = 0xC0DEC0DECAFEBA00UL;

        Assert.True(_memory.TryWrite(cookieAddress, BitConverter.GetBytes(cookie)));
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = informationAddress;

        Assert.Equal(0, PadExports.PadGetExtControllerInformation(_ctx));

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

    [NativeX64Fact]
    public void SpecialAndRemotePortsHaveIndependentLiveHandles()
    {
        const int noHandle = unchecked((int)0x80920008);
        const int alreadyOpened = unchecked((int)0x80920004);
        const ulong dataAddress = Base + 0x200;

        Assert.Equal(0, PadExports.PadInit(_ctx));
        _ctx[CpuRegister.Rdi] = 0xFF;
        _ctx[CpuRegister.Rsi] = 16;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(noHandle, PadExports.PadGetHandle(_ctx));

        var standard = OpenPad(0x10000000, 0);
        var special = OpenPad(0x10000000, 2);
        var remote = OpenPad(0xFF, 16);
        Assert.Equal(1, standard);
        Assert.Equal(2, special);
        Assert.Equal(3, remote);
        Assert.Equal(alreadyOpened, OpenPad(0xFF, 16));
        Assert.Equal(remote, PadExports.PadGetHandle(_ctx));

        foreach (var handle in new[] { standard, special, remote })
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ctx[CpuRegister.Rsi] = dataAddress;
            Assert.Equal(0, PadExports.PadGetControllerInformation(_ctx));
            Assert.Equal(0, PadExports.PadReadState(_ctx));
        }

        _ctx[CpuRegister.Rdi] = unchecked((ulong)remote);
        Assert.Equal(0, PadExports.PadClose(_ctx));
        Assert.Equal(InvalidHandle, PadExports.PadReadState(_ctx));
        Assert.Equal(InvalidHandle, PadExports.PadClose(_ctx));

        _ctx[CpuRegister.Rdi] = 0xFF;
        _ctx[CpuRegister.Rsi] = 16;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(noHandle, PadExports.PadGetHandle(_ctx));
        _ctx[CpuRegister.Rdi] = 0x10000000;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(standard, PadExports.PadGetHandle(_ctx));
        _ctx[CpuRegister.Rsi] = 2;
        Assert.Equal(special, PadExports.PadGetHandle(_ctx));
        Assert.True(OpenPad(0xFF, 16) > remote);
    }

    [NativeX64Theory]
    [InlineData(-1, 0, 0, unchecked((int)0x80920008))]
    [InlineData(0x10000001, 0, 0, unchecked((int)0x80920007))]
    [InlineData(0x10000000, 1, 0, unchecked((int)0x80920007))]
    [InlineData(0x10000000, 16, 0, unchecked((int)0x80920007))]
    [InlineData(0xFF, 0, 0, unchecked((int)0x80920007))]
    [InlineData(0xFF, 16, 1, unchecked((int)0x80920007))]
    public void Open_RejectsUnsupportedTuples(int userId, int type, int index, int expected)
    {
        PadExports.PadInit(_ctx);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)userId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)type);
        _ctx[CpuRegister.Rdx] = unchecked((ulong)index);
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(expected, PadExports.PadOpen(_ctx));
    }

    private int OpenPad(int userId, int type)
    {
        PadExports.PadInit(_ctx);
        _ctx[CpuRegister.Rdi] = unchecked((ulong)userId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)type);
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;
        return PadExports.PadOpen(_ctx);
    }
}

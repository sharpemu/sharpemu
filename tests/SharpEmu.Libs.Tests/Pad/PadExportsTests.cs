// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int PrimaryUserId = 0x10000000;
    private const int InvalidHandle = unchecked((int)0x80920003);
    private const int AlreadyOpened = unchecked((int)0x80920004);
    private const int NotInitialized = unchecked((int)0x80920005);
    private const int NoHandle = unchecked((int)0x80920008);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        Assert.Equal(0, PadExports.PadInit(_ctx));
        Assert.Equal(1, Open(0));
    }

    [NativeX64Theory]
    [InlineData(0, InvalidHandle)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    // ABI: int scePadResetOrientation(int32_t handle) — handle only, no out
    // parameter, so the only failure mode is a bad handle.
    [NativeX64Theory]
    [InlineData(0, InvalidHandle)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void ResetOrientation_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadResetOrientation(_ctx));
        Assert.Equal(unchecked((ulong)expected), _ctx[CpuRegister.Rax]);
    }

    [NativeX64Theory]
    [InlineData(0, InvalidHandle)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetAngularVelocityDeadbandState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetAngularVelocityDeadbandState(_ctx));
    }

    [NativeX64Fact]
    public void SetAngularVelocityDeadbandState_AcceptsReopenedSessionAndRejectsClosedHandle()
    {
        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadClose(_ctx));
        var handle = Open(0);
        Assert.True(handle > 1);

        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(InvalidHandle, PadExports.PadSetAngularVelocityDeadbandState(_ctx));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(0, PadExports.PadSetAngularVelocityDeadbandState(_ctx));
    }

    /// <summary>
    /// Mirrors the calling frame observed in PPSA10112: the out-param points at
    /// rbp-0x30 and the caller's stack cookie sits at rbp-0x28, so the state is
    /// eight bytes. Writing more would smash the cookie and fail the guest's
    /// stack check, which is the failure mode this size guards against.
    /// </summary>
    [NativeX64Fact]
    public void GetTriggerEffectState_WritesEightBytesAndLeavesTheCookieIntact()
    {
        const ulong stateAddress = Base + 0x100;
        const ulong cookieAddress = stateAddress + 8;
        const ulong cookie = 0xC0DEC0DECAFEBA00UL;

        Assert.True(_memory.TryWrite(stateAddress, new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE }));
        Assert.True(_memory.TryWrite(cookieAddress, BitConverter.GetBytes(cookie)));

        _ctx[CpuRegister.Rdi] = 1;
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

    [NativeX64Fact]
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

    [NativeX64Fact]
    public void GetExtControllerInformation_DoesNotOverwriteAdjacentMemory()
    {
        const ulong informationAddress = Base + 0x200;
        const int informationSize = 0x2C;
        const ulong cookie = 0xC0DEC0DECAFEBA00UL;

        Assert.True(_memory.TryWrite(informationAddress, new byte[informationSize]));
        Assert.True(_memory.TryWrite(informationAddress + informationSize, BitConverter.GetBytes(cookie)));

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = informationAddress;

        Assert.Equal(0, PadExports.PadGetExtControllerInformation(_ctx));

        Span<byte> information = stackalloc byte[informationSize];
        Assert.True(_memory.TryRead(informationAddress, information));
        Assert.Equal(1, information[0x0B]);
        Assert.Equal(1, information[0x0C]);
        Assert.Equal(0, information[0x1D]);

        Span<byte> guard = stackalloc byte[8];
        Assert.True(_memory.TryRead(informationAddress + informationSize, guard));
        Assert.Equal(cookie, BitConverter.ToUInt64(guard));
    }

    [NativeX64Theory]
    [InlineData(0ul, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT)]
    [InlineData(Base + 0x1000, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)]
    public void GetExtControllerInformation_RejectsInvalidOutput(ulong address, int expected)
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = address;
        Assert.Equal(expected, PadExports.PadGetExtControllerInformation(_ctx));
    }

    [NativeX64Fact]
    public void GetExtControllerInformation_RejectsClosedHandleWithoutWriting()
    {
        const ulong informationAddress = Base + 0x200;
        byte[] sentinel = new byte[0x2C];
        Array.Fill(sentinel, (byte)0xA5);
        Assert.True(_memory.TryWrite(informationAddress, sentinel));
        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadClose(_ctx));
        _ctx[CpuRegister.Rsi] = informationAddress;

        Assert.Equal(InvalidHandle, PadExports.PadGetExtControllerInformation(_ctx));
        Span<byte> information = stackalloc byte[sentinel.Length];
        Assert.True(_memory.TryRead(informationAddress, information));
        Assert.Equal(sentinel, information.ToArray());
    }

    [NativeX64Theory]
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
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = dataAddress;
        Assert.Equal(InvalidHandle, PadExports.PadReadState(_ctx));

        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadReadState(_ctx));
    }

    [NativeX64Fact]
    public void SpecialPort_UsesASeparateDisconnectedHandle()
    {
        Assert.Equal(0, Open(2, extended: true));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = Base + 0x300;
        Assert.Equal(0, PadExports.PadReadState(_ctx));

        Span<byte> data = stackalloc byte[0x78];
        Assert.True(_memory.TryRead(Base + 0x300, data));
        Assert.Equal(0, data[0x4C]);
        Assert.Equal(0, data[0x68]);
        Assert.Equal(0u, BitConverter.ToUInt32(data));

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(0, PadExports.PadClose(_ctx));
        _ctx[CpuRegister.Rsi] = Base + 0x300;
        Assert.Equal(InvalidHandle, PadExports.PadReadState(_ctx));

        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadReadState(_ctx));
    }

    [NativeX64Fact]
    public void GetHandle_ReturnsOnlyAnOpenMatchingPort()
    {
        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(NoHandle, PadExports.PadGetHandle(_ctx));

        Assert.Equal(0, Open(2));
        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, PadExports.PadGetHandle(_ctx));

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(0, PadExports.PadClose(_ctx));
        Assert.Equal(InvalidHandle, PadExports.PadClose(_ctx));
        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        Assert.Equal(NoHandle, PadExports.PadGetHandle(_ctx));
    }

    [NativeX64Fact]
    public void Open_RejectsAnAlreadyOpenStandardPort()
    {
        Assert.Equal(AlreadyOpened, Open(0));
        Assert.Equal(AlreadyOpened, Open(0, extended: true));
    }

    [NativeX64Fact]
    public void Open_AcceptsAnSdkReservedParameterPointer()
    {
        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadClose(_ctx));
        Assert.True(_memory.TryWrite(Base + 0x380, new byte[8]));

        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = Base + 0x380;
        Assert.True(PadExports.PadOpen(_ctx) >= 0);
    }

    [NativeX64Fact]
    public void InitAgain_PreservesOpenHandles()
    {
        Assert.Equal(0, PadExports.PadInit(_ctx));
        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(1, PadExports.PadGetHandle(_ctx));
    }

    [NativeX64Fact]
    public void OpenAndGetHandle_RequireInitialization()
    {
        var context = new CpuContext(new FakeCpuMemory(Base, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = PrimaryUserId;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;

        Assert.Equal(NotInitialized, PadExports.PadOpen(context));
        Assert.Equal(NotInitialized, PadExports.PadGetHandle(context));
    }

    [NativeX64Fact]
    public void ExtendedOpens_CloseOnlyTheirOwnHandle()
    {
        var firstSpecialHandle = Open(2, extended: true);
        var secondSpecialHandle = Open(2, extended: true);
        Assert.Equal(0, firstSpecialHandle);
        Assert.NotEqual(firstSpecialHandle, secondSpecialHandle);
        Assert.NotEqual(1, secondSpecialHandle);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)secondSpecialHandle);
        Assert.Equal(0, PadExports.PadClose(_ctx));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)firstSpecialHandle);
        _ctx[CpuRegister.Rsi] = Base + 0x300;
        Assert.Equal(0, PadExports.PadReadState(_ctx));
        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(0, PadExports.PadReadState(_ctx));
    }

    [NativeX64Fact]
    public void IsRemoteController_WritesOneFalseByte()
    {
        const ulong resultAddress = Base + 0x100;
        Assert.True(_memory.TryWrite(resultAddress, new byte[] { 0xFF, 0xA5 }));
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = resultAddress;

        Assert.Equal(0, PadExports.PadIsRemoteController(_ctx));
        Span<byte> result = stackalloc byte[2];
        Assert.True(_memory.TryRead(resultAddress, result));
        Assert.Equal(0, result[0]);
        Assert.Equal(0xA5, result[1]);
    }

    [NativeX64Theory]
    [InlineData(0ul, unchecked((int)0x80920001))]
    [InlineData(Base + 0x1000, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)]
    public void IsRemoteController_RejectsInvalidOutput(ulong address, int expected)
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = address;
        Assert.Equal(expected, PadExports.PadIsRemoteController(_ctx));
    }

    [NativeX64Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void OrientationAndRemoteQueries_RejectUnknownHandles(int handle)
    {
        const ulong resultAddress = Base + 0x100;
        Assert.True(_memory.TryWrite(resultAddress, new byte[] { 0xA5 }));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = resultAddress;
        Assert.Equal(InvalidHandle, PadExports.PadResetOrientation(_ctx));
        Assert.Equal(InvalidHandle, PadExports.PadIsRemoteController(_ctx));
        Span<byte> result = stackalloc byte[1];
        Assert.True(_memory.TryRead(resultAddress, result));
        Assert.Equal(0xA5, result[0]);
    }

    [NativeX64Fact]
    public void OrientationAndRemoteQueries_RejectClosedSession()
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        Assert.Equal(0, PadExports.PadResetOrientation(_ctx));
        Assert.Equal(0, PadExports.PadClose(_ctx));
        Assert.Equal(InvalidHandle, PadExports.PadResetOrientation(_ctx));
        Assert.Equal(InvalidHandle, PadExports.PadIsRemoteController(_ctx));
    }

    private int Open(int portType, bool extended = false)
    {
        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = (ulong)portType;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;
        return extended ? PadExports.PadOpenExt(_ctx) : PadExports.PadOpen(_ctx);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests.UserService;

// libSceUserService is the PS5 user-account layer: it tracks who is signed in, delivers
// login/logout events, and exposes per-user metadata (names, accessibility settings, etc.).
// All exports operate through CPU registers and guest memory, so a FakeCpuMemory is enough
// to exercise them end-to-end without a live guest.
public sealed class UserServiceTests
{
    // The emulator exposes a single synthetic user whose ID encodes local slot 0
    // (the same encoding real retail hardware uses, so Unity's slot-to-ID helper works).
    private const int PrimaryUserId = 0x10000000;
    private const int InvalidUserId = -1;
    private const string PrimaryUserName = "SharpEmu";

    private const int OrbisUserServiceErrorInvalidArgument  = unchecked((int)0x80960005);
    private const int OrbisUserServiceErrorNoEvent          = unchecked((int)0x80960007);
    private const int OrbisUserServiceErrorInvalidParameter = unchecked((int)0x80960009);
    private const int OrbisUserServiceErrorBufferTooShort   = unchecked((int)0x8096000A);

    private const ulong Base       = 0x1_0000_0000UL;
    private const ulong OutAddress = Base + 0x100;

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext    _ctx;

    public UserServiceTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    // -------------------------------------------------------------------------
    // sceUserServiceInitialize
    // -------------------------------------------------------------------------

    [Fact]
    public void Initialize_ReturnsOk()
    {
        // The initialize export always succeeds; no parameters are needed.
        Assert.Equal(0, UserServiceExports.UserServiceInitialize(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    // -------------------------------------------------------------------------
    // sceUserServiceGetInitialUser
    // -------------------------------------------------------------------------

    [Fact]
    public void GetInitialUser_NullPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = 0;

        var result = UserServiceExports.UserServiceGetInitialUser(_ctx);

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, result);
        Assert.Equal(unchecked((ulong)OrbisUserServiceErrorInvalidArgument), _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void GetInitialUser_ValidPointer_WritesPrimaryUserId()
    {
        _ctx[CpuRegister.Rdi] = OutAddress;

        var result = UserServiceExports.UserServiceGetInitialUser(_ctx);

        Assert.Equal(0, result);
        Assert.True(_ctx.TryReadInt32(OutAddress, out var userId));
        Assert.Equal(PrimaryUserId, userId);
    }

    // -------------------------------------------------------------------------
    // sceUserServiceGetLoginUserIdList
    // -------------------------------------------------------------------------

    [Fact]
    public void GetLoginUserIdList_NullPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = 0;

        var result = UserServiceExports.UserServiceGetLoginUserIdList(_ctx);

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, result);
    }

    [Fact]
    public void GetLoginUserIdList_ValidPointer_WritesSlotLayout()
    {
        // The PS5 supports up to 4 simultaneous logged-in users. SharpEmu fills slot 0
        // with PrimaryUserId and the remaining three slots with -1 (no user).
        _ctx[CpuRegister.Rdi] = OutAddress;

        Assert.Equal(0, UserServiceExports.UserServiceGetLoginUserIdList(_ctx));

        Span<byte> raw = stackalloc byte[sizeof(int) * 4];
        Assert.True(_memory.TryRead(OutAddress, raw));

        Assert.Equal(PrimaryUserId, BinaryPrimitives.ReadInt32LittleEndian(raw[0x00..]));
        Assert.Equal(InvalidUserId, BinaryPrimitives.ReadInt32LittleEndian(raw[0x04..]));
        Assert.Equal(InvalidUserId, BinaryPrimitives.ReadInt32LittleEndian(raw[0x08..]));
        Assert.Equal(InvalidUserId, BinaryPrimitives.ReadInt32LittleEndian(raw[0x0C..]));
    }

    // -------------------------------------------------------------------------
    // sceUserServiceGetEvent
    // -------------------------------------------------------------------------

    [Fact]
    public void GetEvent_NullPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = 0;

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, UserServiceExports.UserServiceGetEvent(_ctx));
    }

    [Fact]
    public void GetEvent_FirstCall_DeliversLoginEvent()
    {
        // The very first call must synthesise a LOGIN event for the primary user so that
        // games blocking on the event queue can proceed past their sign-in wait loop.
        _ctx[CpuRegister.Rdi] = OutAddress;

        Assert.Equal(0, UserServiceExports.UserServiceGetEvent(_ctx));

        // Event layout: [int eventType (0 = login), int userId]
        Assert.True(_ctx.TryReadInt32(OutAddress, out var eventType));
        Assert.Equal(0, eventType); // 0 == login

        Assert.True(_ctx.TryReadInt32(OutAddress + sizeof(int), out var eventUserId));
        Assert.Equal(PrimaryUserId, eventUserId);
    }

    [Fact]
    public void GetEvent_SecondCall_ReturnsNoEvent()
    {
        // After the one-shot login event has been consumed, the queue is empty.
        _ctx[CpuRegister.Rdi] = OutAddress;
        UserServiceExports.UserServiceGetEvent(_ctx); // consume the first event

        _ctx[CpuRegister.Rdi] = OutAddress;
        Assert.Equal(OrbisUserServiceErrorNoEvent, UserServiceExports.UserServiceGetEvent(_ctx));
    }

    // -------------------------------------------------------------------------
    // sceUserServiceGetUserName
    // -------------------------------------------------------------------------

    [Fact]
    public void GetUserName_InvalidUserId_ReturnsInvalidParameter()
    {
        // Any user ID that is not PrimaryUserId or the legacy alias (1) is rejected.
        _ctx[CpuRegister.Rdi] = unchecked((ulong)0xDEADBEEF);
        _ctx[CpuRegister.Rsi] = OutAddress;
        _ctx[CpuRegister.Rdx] = 256;

        Assert.Equal(OrbisUserServiceErrorInvalidParameter, UserServiceExports.UserServiceGetUserName(_ctx));
    }

    [Fact]
    public void GetUserName_NullNamePointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = (ulong)PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 256;

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, UserServiceExports.UserServiceGetUserName(_ctx));
    }

    [Fact]
    public void GetUserName_BufferTooShort_ReturnsBufferTooShort()
    {
        // The capacity must be strictly greater than the UTF-8 byte length of the name.
        var minRequiredCapacity = (ulong)Encoding.UTF8.GetByteCount(PrimaryUserName);

        _ctx[CpuRegister.Rdi] = (ulong)PrimaryUserId;
        _ctx[CpuRegister.Rsi] = OutAddress;
        _ctx[CpuRegister.Rdx] = minRequiredCapacity; // equal, not greater — should fail

        Assert.Equal(OrbisUserServiceErrorBufferTooShort, UserServiceExports.UserServiceGetUserName(_ctx));
    }

    [Fact]
    public void GetUserName_ValidArgs_WritesNullTerminatedUtf8Name()
    {
        var nameLen = Encoding.UTF8.GetByteCount(PrimaryUserName);

        _ctx[CpuRegister.Rdi] = (ulong)PrimaryUserId;
        _ctx[CpuRegister.Rsi] = OutAddress;
        _ctx[CpuRegister.Rdx] = (ulong)(nameLen + 2); // +1 for NUL, +1 of headroom

        Assert.Equal(0, UserServiceExports.UserServiceGetUserName(_ctx));

        // Read back the name bytes plus the NUL terminator.
        Span<byte> raw = stackalloc byte[nameLen + 1];
        Assert.True(_memory.TryRead(OutAddress, raw));
        Assert.Equal(PrimaryUserName, Encoding.UTF8.GetString(raw[..nameLen]));
        Assert.Equal(0, raw[nameLen]); // NUL terminator must be present
    }

    // -------------------------------------------------------------------------
    // sceUserServiceGetAgeLevel (representative of the WriteUserSettingInt32 helpers)
    // -------------------------------------------------------------------------

    [Fact]
    public void GetAgeLevel_InvalidUserId_ReturnsInvalidParameter()
    {
        _ctx[CpuRegister.Rdi] = 0xFFFFFFFFUL; // not PrimaryUserId
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(OrbisUserServiceErrorInvalidParameter, UserServiceExports.UserServiceGetAgeLevel(_ctx));
    }

    [Fact]
    public void GetAgeLevel_NullOutputPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = (ulong)PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, UserServiceExports.UserServiceGetAgeLevel(_ctx));
    }

    [Fact]
    public void GetAgeLevel_ValidArgs_Writes18()
    {
        // Age level is fixed at 18 to allow all content ratings.
        _ctx[CpuRegister.Rdi] = (ulong)PrimaryUserId;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, UserServiceExports.UserServiceGetAgeLevel(_ctx));
        Assert.True(_ctx.TryReadInt32(OutAddress, out var level));
        Assert.Equal(18, level);
    }

    // -------------------------------------------------------------------------
    // sceUserServiceGetGamePresets
    // -------------------------------------------------------------------------

    [Fact]
    public void GetGamePresets_InvalidUserId_ReturnsInvalidParameter()
    {
        _ctx[CpuRegister.Rdi] = 0xDEADUL;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(OrbisUserServiceErrorInvalidParameter, UserServiceExports.UserServiceGetGamePresets(_ctx));
    }

    [Fact]
    public void GetGamePresets_NullPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = (ulong)PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(OrbisUserServiceErrorInvalidArgument, UserServiceExports.UserServiceGetGamePresets(_ctx));
    }

    [Fact]
    public void GetGamePresets_ValidArgs_WritesStructSizeAsFirstField()
    {
        // The presets structure is 0x28 bytes. The first 8 bytes hold the struct size
        // as a self-describing length field (common PS5 SDK convention).
        _ctx[CpuRegister.Rdi] = (ulong)PrimaryUserId;
        _ctx[CpuRegister.Rsi] = OutAddress;

        Assert.Equal(0, UserServiceExports.UserServiceGetGamePresets(_ctx));
        Assert.True(_ctx.TryReadUInt64(OutAddress, out var structSize));
        Assert.Equal(0x28UL, structSize);
    }
}

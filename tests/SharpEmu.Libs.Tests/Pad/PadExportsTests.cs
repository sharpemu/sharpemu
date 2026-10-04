// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

[CollectionDefinition(PadInputStateCollection.Name, DisableParallelization = true)]
public sealed class PadInputStateCollection : ICollectionFixture<PadHostInputFixture>
{
    public const string Name = "PadInputState";
}

public sealed class PadHostInputFixture : IDisposable
{
    public PadHostInputFixture() =>
        PadExports.SetHostInputForTests(new WindowHostInput());

    public void Dispose() => PadExports.SetHostInputForTests(null);
}

[Collection(PadInputStateCollection.Name)]
public sealed class PadExportsTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);
    private const int InvalidArgument = unchecked((int)0x80920001);
    private const int AlreadyOpened = unchecked((int)0x80920004);
    private const int NotInitialized = unchecked((int)0x80920005);
    private const int DeviceNotConnected = unchecked((int)0x80920007);
    private const int DeviceNoHandle = unchecked((int)0x80920008);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        PadExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => PadExports.ResetForTests();

    [Fact]
    public void UnknownN3kSX62fgNo_AcceptsOpaqueArgumentsWithoutTreatingThemAsALength()
    {
        _ctx[CpuRegister.Rdi] = 0x0000000080010BB0;
        _ctx[CpuRegister.Rsi] = 0x0000000080010ED0;
        _ctx[CpuRegister.Rdx] = 0x000000080175A250;
        _ctx[CpuRegister.Rcx] = 0x0000000801795588;
        _ctx[CpuRegister.R8] = 0x0F0F0F0F0F0F0F0F;
        _ctx[CpuRegister.R9] = 0x3333333333333333;

        Assert.Equal(0, PadExports.PadUnknownN3kSX62fgNo(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void UnknownN3kSX62fgNo_DoesNotModifyOpaqueArguments()
    {
        const ulong outputAddress = Base + 0x100;
        var sentinel = Enumerable.Repeat((byte)0xEE, 16).ToArray();
        Assert.True(_memory.TryWrite(outputAddress, sentinel));

        _ctx[CpuRegister.Rdi] = outputAddress;
        _ctx[CpuRegister.Rcx] = (ulong)sentinel.Length;

        Assert.Equal(0, PadExports.PadUnknownN3kSX62fgNo(_ctx));

        Span<byte> output = stackalloc byte[16];
        Assert.True(_memory.TryRead(outputAddress, output));
        Assert.Equal(sentinel, output.ToArray());
    }

    [Fact]
    public void UnknownN3kSX62fgNo_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("n3kSX62fgNo", out var export));
        Assert.Equal("scePadUnknownN3kSX62fgNo", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Theory]
    [InlineData(0, 0UL)]
    [InlineData(0, Base + 0x100)]
    [InlineData(2, 0UL)]
    [InlineData(2, Base + 0x100)]
    public void PadOpen_AcceptsPersonalStandardAndSpecialPortsWithReservedParameter(
        int type,
        ulong parameterAddress)
    {
        InitializePad();
        if (parameterAddress != 0)
        {
            Assert.True(_memory.TryWrite(parameterAddress, new byte[8]));
        }

        Assert.True(OpenPad(type, parameterAddress: parameterAddress) > 0);
    }

    [Fact]
    public void PadOpen_LeavesExtendedOnlyPortForPadOpenExt()
    {
        InitializePad();
        _ctx[CpuRegister.Rdi] = 0x1000_0000;
        _ctx[CpuRegister.Rsi] = 1;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;

        Assert.Equal(DeviceNotConnected, PadExports.PadOpen(_ctx));
        Assert.True(PadExports.PadOpenExt(_ctx) > 0);
    }

    [Fact]
    public void PadOpen_AcceptsSystemRemoteControlPortAndGetHandleFindsIt()
    {
        InitializePad();
        _ctx[CpuRegister.Rdi] = 0xFF;
        _ctx[CpuRegister.Rsi] = 16;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;

        var handle = PadExports.PadOpen(_ctx);

        Assert.True(handle > 0);
        _ctx[CpuRegister.Rdi] = 0xFF;
        _ctx[CpuRegister.Rsi] = 16;
        _ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(handle, PadExports.PadGetHandle(_ctx));
    }

    [Theory]
    [InlineData(0xFF, 0, 0)]
    [InlineData(0xFF, 16, 1)]
    [InlineData(0x10000000, 16, 0)]
    public void PadOpen_RejectsMismatchedRemoteControlTuples(
        int userId,
        int type,
        int index)
    {
        InitializePad();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)userId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)type);
        _ctx[CpuRegister.Rdx] = unchecked((ulong)index);
        _ctx[CpuRegister.Rcx] = 0;

        Assert.Equal(DeviceNotConnected, PadExports.PadOpen(_ctx));
    }

    [Fact]
    public void PadGetHandle_IsLookupOnlyAndKeysOnTheOpenedTuple()
    {
        Assert.Equal(NotInitialized, GetPadHandle());
        InitializePad();
        Assert.Equal(DeviceNoHandle, GetPadHandle());

        var standard = OpenPad(0);
        var special = OpenPad(2);

        Assert.True(standard > 0);
        Assert.True(special > 0);
        Assert.NotEqual(standard, special);
        Assert.Equal(standard, GetPadHandle(0));
        Assert.Equal(special, GetPadHandle(2));
        Assert.Equal(DeviceNoHandle, GetPadHandle(1));
        Assert.Equal(DeviceNoHandle, GetPadHandle(index: 1));
    }

    [Fact]
    public void PadOpen_RejectsDuplicateLiveTupleAndReopenMintsFreshHandle()
    {
        InitializePad();
        var first = OpenPad();

        Assert.Equal(AlreadyOpened, OpenPad());
        Assert.Equal(0, ClosePad(first));

        var reopened = OpenPad();
        Assert.True(reopened > 0);
        Assert.NotEqual(first, reopened);
    }

    [Fact]
    public void PadClose_RetiresOnlyThatHandle()
    {
        InitializePad();
        var standard = OpenPad(0);
        var special = OpenPad(2);

        Assert.Equal(0, ClosePad(standard));
        Assert.Equal(InvalidHandle, ClosePad(standard));
        Assert.Equal(DeviceNoHandle, GetPadHandle(0));
        Assert.Equal(special, GetPadHandle(2));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)standard);
        Assert.Equal(InvalidHandle, PadExports.PadResetOrientation(_ctx));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)special);
        Assert.Equal(0, PadExports.PadResetOrientation(_ctx));
    }

    [Fact]
    public void SetAngularVelocityDeadbandState_RequiresALiveHandle()
    {
        InitializePad();
        var standard = OpenPad(0);
        var special = OpenPad(2);

        Assert.True(PadExports.TryGetAngularVelocityDeadbandStateForTests(standard, out var standardEnabled));
        Assert.False(standardEnabled);
        Assert.True(PadExports.TryGetAngularVelocityDeadbandStateForTests(special, out var specialEnabled));
        Assert.False(specialEnabled);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)standard);
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(0, PadExports.PadSetAngularVelocityDeadbandState(_ctx));
        Assert.True(PadExports.TryGetAngularVelocityDeadbandStateForTests(standard, out standardEnabled));
        Assert.True(standardEnabled);
        Assert.True(PadExports.TryGetAngularVelocityDeadbandStateForTests(special, out specialEnabled));
        Assert.True(specialEnabled);

        Assert.Equal(0, ClosePad(standard));
        Assert.False(PadExports.TryGetAngularVelocityDeadbandStateForTests(standard, out _));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)standard);
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidHandle, PadExports.PadSetAngularVelocityDeadbandState(_ctx));
        Assert.True(PadExports.TryGetAngularVelocityDeadbandStateForTests(special, out specialEnabled));
        Assert.True(specialEnabled);

        const int unknownHandle = 999;
        _ctx[CpuRegister.Rdi] = unknownHandle;
        Assert.Equal(InvalidHandle, PadExports.PadSetAngularVelocityDeadbandState(_ctx));
        Assert.False(PadExports.TryGetAngularVelocityDeadbandStateForTests(unknownHandle, out _));
    }

    [Theory]
    [InlineData(0x10000000, 2)]
    [InlineData(0xFF, 16)]
    public void ClosingAuxiliaryPad_PreservesStandardLeftRightAndCrossInput(
        int auxiliaryUserId,
        int auxiliaryType)
    {
        const ulong outputAddress = Base + 0x100;
        HostWindowInput.Connect();
        try
        {
            PadExports.ResetForTests();
            InitializePad();
            var standard = OpenPad(0);
            var auxiliary = OpenPadForTuple(auxiliaryUserId, auxiliaryType);
            Assert.NotEqual(standard, auxiliary);
            Assert.Equal(AlreadyOpened, OpenPadForTuple(auxiliaryUserId, auxiliaryType));
            Assert.Equal(0, ClosePad(auxiliary));
            Assert.Equal(standard, GetPadHandle(0));
            Assert.Equal(DeviceNoHandle, GetPadHandleForTuple(auxiliaryUserId, auxiliaryType));

            // Some games probe optional player slots with -1 after opening the
            // gameplay pad. A rejected optional configuration call must not
            // invalidate or replace that live standard handle.
            _ctx[CpuRegister.Rdi] = unchecked((ulong)-1L);
            _ctx[CpuRegister.Rsi] = 1;
            Assert.Equal(InvalidHandle, PadExports.PadSetAngularVelocityDeadbandState(_ctx));
            Assert.Equal(standard, GetPadHandle(0));

            HostWindowInput.SetGamepad(
                "test gamepad",
                new HostGamepadState(
                    Connected: true,
                    Buttons: HostGamepadButtons.Left | HostGamepadButtons.Cross,
                    LeftX: 0,
                    LeftY: 128,
                    RightX: 128,
                    RightY: 128,
                    LeftTrigger: 0,
                    RightTrigger: 0));
            _ctx[CpuRegister.Rdi] = unchecked((ulong)standard);
            _ctx[CpuRegister.Rsi] = outputAddress;
            Assert.Equal(0, PadExports.PadReadState(_ctx));

            Span<byte> leftAndCross = stackalloc byte[8];
            Assert.True(_memory.TryRead(outputAddress, leftAndCross));
            Assert.Equal(0x4080u, BitConverter.ToUInt32(leftAndCross[..4]));
            Assert.Equal(0, leftAndCross[4]);

            HostWindowInput.SetGamepad(
                "test gamepad",
                new HostGamepadState(
                    Connected: true,
                    Buttons: HostGamepadButtons.Right | HostGamepadButtons.Cross,
                    LeftX: 255,
                    LeftY: 128,
                    RightX: 128,
                    RightY: 128,
                    LeftTrigger: 0,
                    RightTrigger: 0));
            Assert.Equal(0, PadExports.PadReadState(_ctx));

            Span<byte> rightAndCross = stackalloc byte[8];
            Assert.True(_memory.TryRead(outputAddress, rightAndCross));
            Assert.Equal(0x4020u, BitConverter.ToUInt32(rightAndCross[..4]));
            Assert.Equal(255, rightAndCross[4]);
        }
        finally
        {
            HostWindowInput.Disconnect();
            PadExports.ResetForTests();
        }
    }

    [Fact]
    public void SetAngularVelocityDeadbandState_RegistersWithCatalogIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("r44mAxdSG+U", out var export));
        Assert.Equal("scePadSetAngularVelocityDeadbandState", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
    }

    [Fact]
    public void PadIsRemoteController_ValidatesHandleAndWritesFalse()
    {
        const ulong isRemoteAddress = Base + 0x100;
        Assert.True(_memory.TryWrite(isRemoteAddress, new byte[] { 1 }));

        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = isRemoteAddress;
        Assert.Equal(InvalidHandle, PadExports.PadIsRemoteController(_ctx));

        InitializePad();
        var handle = OpenPad();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidArgument, PadExports.PadIsRemoteController(_ctx));

        _ctx[CpuRegister.Rsi] = isRemoteAddress;
        Assert.Equal(0, PadExports.PadIsRemoteController(_ctx));
        Span<byte> isRemote = stackalloc byte[1];
        Assert.True(_memory.TryRead(isRemoteAddress, isRemote));
        Assert.Equal(0, isRemote[0]);
    }

    [Fact]
    public void PadIsRemoteController_RegistersWithCatalogIdentity()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("fCWdlnmB1Ks", out var export));
        Assert.Equal("scePadIsRemoteController", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
    }

    [Fact]
    public void PadRead_DrainsAllRequestedButtonTransitionsInOneBatch()
    {
        const ulong outputAddress = Base + 0x100;
        HostWindowInput.Connect();
        try
        {
            // Connect emits a host-state notification. Clear it before setting
            // up the read so this test queues only the four deliberate edges.
            PadExports.ResetForTests();
            InitializePad();
            var handle = OpenPad();

            HostWindowInput.SetKey(0x28, true);
            HostWindowInput.SetKey(0x28, false);
            HostWindowInput.SetKey(0x28, true);
            HostWindowInput.SetKey(0x28, false);

            _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ctx[CpuRegister.Rsi] = outputAddress;
            _ctx[CpuRegister.Rdx] = 4;

            Assert.Equal(4, PadExports.PadRead(_ctx));
            Assert.Equal(
                new uint[] { 0x40, 0, 0x40, 0 },
                Enumerable.Range(0, 4)
                    .Select(index =>
                    {
                        Span<byte> buttons = stackalloc byte[4];
                        Assert.True(_memory.TryRead(
                            outputAddress + unchecked((ulong)(index * 0x78)),
                            buttons));
                        return BitConverter.ToUInt32(buttons);
                    })
                    .ToArray());

            Assert.Equal(1, PadExports.PadRead(_ctx));
        }
        finally
        {
            HostWindowInput.Disconnect();
            PadExports.ResetForTests();
        }
    }

    [Fact]
    public void PadRead_WritesOnlyTheRecordsItReturns()
    {
        const ulong outputAddress = Base + 0x100;
        const int requestedCount = 4;
        HostWindowInput.Connect();
        try
        {
            PadExports.ResetForTests();
            var original = Enumerable.Repeat((byte)0xA5, 0x78 * requestedCount).ToArray();
            Assert.True(_memory.TryWrite(outputAddress, original));
            InitializePad();
            var handle = OpenPad();
            HostWindowInput.SetKey(0x28, true);
            _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ctx[CpuRegister.Rsi] = outputAddress;
            _ctx[CpuRegister.Rdx] = requestedCount;

            Assert.Equal(1, PadExports.PadRead(_ctx));

            var unused = new byte[0x78 * (requestedCount - 1)];
            Assert.True(_memory.TryRead(outputAddress + 0x78, unused));
            Assert.All(unused, value => Assert.Equal(0xA5, value));
        }
        finally
        {
            HostWindowInput.Disconnect();
            PadExports.ResetForTests();
        }
    }

    [Fact]
    public void PadRead_ReturnsCurrentStateWhenNoHistoryIsQueued()
    {
        const ulong outputAddress = Base + 0x100;
        InitializePad();
        var handle = OpenPad();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = outputAddress;
        _ctx[CpuRegister.Rdx] = 1;

        Assert.Equal(1, PadExports.PadRead(_ctx));
        Assert.Equal(1, PadExports.PadRead(_ctx));
    }

    [Fact]
    public void PadRead_MultiRecordRequestReturnsOneCurrentRecordWithoutFreshHistory()
    {
        const ulong outputAddress = Base + 0x100;
        var sentinel = Enumerable.Repeat((byte)0xA5, 0x78 * 4).ToArray();
        Assert.True(_memory.TryWrite(outputAddress, sentinel));
        InitializePad();
        var handle = OpenPad();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = outputAddress;
        _ctx[CpuRegister.Rdx] = 4;

        Assert.Equal(1, PadExports.PadRead(_ctx));
        var result = new byte[sentinel.Length];
        Assert.True(_memory.TryRead(outputAddress, result));
        Assert.All(result.AsSpan(0, 4).ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(128, result[4]);
        Assert.Equal(128, result[5]);
        Assert.Equal(128, result[6]);
        Assert.Equal(128, result[7]);
        Assert.Equal(sentinel.AsSpan(0x78).ToArray(), result.AsSpan(0x78).ToArray());
    }

    [Fact]
    public void PadRead_DeliversNeutralStickAfterKeyboardAxisRelease()
    {
        const ulong outputAddress = Base + 0x100;
        HostWindowInput.Connect();
        try
        {
            PadExports.ResetForTests();
            InitializePad();
            var handle = OpenPad();
            _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ctx[CpuRegister.Rsi] = outputAddress;
            _ctx[CpuRegister.Rdx] = 1;

            Assert.Equal(1, PadExports.PadRead(_ctx));

            HostWindowInput.SetKey(0x44, true);
            Assert.Equal(1, PadExports.PadRead(_ctx));
            Span<byte> held = stackalloc byte[8];
            Assert.True(_memory.TryRead(outputAddress, held));
            Assert.Equal(255, held[4]);
            Assert.Equal(128, held[5]);

            HostWindowInput.SetKey(0x44, false);
            Assert.Equal(1, PadExports.PadRead(_ctx));
            Span<byte> released = stackalloc byte[8];
            Assert.True(_memory.TryRead(outputAddress, released));
            Assert.Equal(128, released[4]);
            Assert.Equal(128, released[5]);
            Assert.Equal(1, PadExports.PadRead(_ctx));
        }
        finally
        {
            HostWindowInput.Disconnect();
            PadExports.ResetForTests();
        }
    }

    [Fact]
    public void PadReadState_InvalidatesCurrentThreadCacheAfterCrossThreadFocusLoss()
    {
        const ulong outputAddress = Base + 0x100;
        HostWindowInput.Connect();
        try
        {
            PadExports.ResetForTests();
            InitializePad();
            var handle = OpenPad();
            _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ctx[CpuRegister.Rsi] = outputAddress;

            // Populate this thread's cache with pressed input. Pinning its
            // timestamp after the worker notification makes the one-millisecond
            // cache window deterministic instead of relying on scheduler timing.
            HostWindowInput.SetKey(0x28, true);
            Assert.Equal(0, PadExports.PadReadState(_ctx));
            var worker = new Thread(() => HostWindowInput.SetFocused(false));
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            typeof(PadExports)
                .GetField(
                    "_lastInputSampleTicks",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!
                .SetValue(null, long.MaxValue);

            Assert.Equal(0, PadExports.PadReadState(_ctx));
            Span<byte> buttons = stackalloc byte[4];
            Assert.True(_memory.TryRead(outputAddress, buttons));
            Assert.Equal(0u, BitConverter.ToUInt32(buttons));
        }
        finally
        {
            HostWindowInput.Disconnect();
            PadExports.ResetForTests();
        }
    }

    [Fact]
    public void ResetRuntimeState_StartsTheNextGuestWithAFreshPadSession()
    {
        InitializePad();
        var oldHandle = OpenPad();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)oldHandle);
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(0, PadExports.PadSetAngularVelocityDeadbandState(_ctx));

        PadExports.ResetRuntimeState();

        Assert.Equal(NotInitialized, GetPadHandle());
        _ctx[CpuRegister.Rdi] = unchecked((ulong)oldHandle);
        Assert.Equal(InvalidHandle, PadExports.PadResetOrientation(_ctx));

        InitializePad();
        var newHandle = OpenPad();
        Assert.Equal(1, newHandle);
        Assert.True(PadExports.TryGetAngularVelocityDeadbandStateForTests(newHandle, out var enabled));
        Assert.False(enabled);
    }

    [Fact]
    public void ResetOrientation_RequiresAnOpenHandle()
    {
        InitializePad();
        var handle = OpenPad();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(0, PadExports.PadResetOrientation(_ctx));
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidHandle, PadExports.PadResetOrientation(_ctx));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)-1);
        Assert.Equal(InvalidHandle, PadExports.PadResetOrientation(_ctx));
    }

    [Fact]
    public void ResetOrientation_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("rIZnR6eSpvk", out var export));
        Assert.Equal("scePadResetOrientation", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
    }

    [Fact]
    public void SetTiltCorrectionState_RequiresAnOpenHandle()
    {
        InitializePad();
        var handle = OpenPad();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(0, PadExports.PadSetTiltCorrectionState(_ctx));
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidHandle, PadExports.PadSetTiltCorrectionState(_ctx));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)-1);
        Assert.Equal(InvalidHandle, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    // ABI: int scePadResetOrientation(int32_t handle) — handle only, no out
    // parameter, so the only failure mode is a bad handle.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, InvalidHandle)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void ResetOrientation_ValidatesHandle(int handle, int expected)
    {
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
        InitializePad();
        var handle = OpenPad();
        const ulong stateAddress = Base + 0x100;
        const ulong cookieAddress = stateAddress + 8;
        const ulong cookie = 0xC0DEC0DECAFEBA00UL;

        Assert.True(_memory.TryWrite(stateAddress, new byte[] { 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE }));
        Assert.True(_memory.TryWrite(cookieAddress, BitConverter.GetBytes(cookie)));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
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
        InitializePad();
        _ = OpenPad();
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
    }

    [Fact]
    public void DeviceClassGetExtendedInformation_WritesOnlyTheTwentyByteAbiPayload()
    {
        InitializePad();
        var handle = OpenPad();
        const ulong informationAddress = Base + 0x200;
        var guardedOutput = Enumerable.Repeat((byte)0xA5, 0x20).ToArray();
        Assert.True(_memory.TryWrite(informationAddress, guardedOutput));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = informationAddress;

        Assert.Equal(0, PadExports.PadDeviceClassGetExtendedInformation(_ctx));
        Span<byte> output = stackalloc byte[0x20];
        Assert.True(_memory.TryRead(informationAddress, output));
        Assert.All(output[..0x14].ToArray(), value => Assert.Equal(0, value));
        Assert.All(output[0x14..].ToArray(), value => Assert.Equal(0xA5, value));
    }

    [Fact]
    public void DeviceClassParseData_ReportsAConnectedStandardPad()
    {
        InitializePad();
        var handle = OpenPad();
        const ulong padDataAddress = Base + 0x100;
        const ulong classDataAddress = Base + 0x200;
        const ulong guardAddress = classDataAddress + 24;
        const ulong guard = 0xC0DEC0DECAFEBA00UL;
        var padData = new byte[0x78];
        padData[0x4C] = 1;
        Assert.True(_memory.TryWrite(padDataAddress, padData));
        Assert.True(_memory.TryWrite(guardAddress, BitConverter.GetBytes(guard)));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = padDataAddress;
        _ctx[CpuRegister.Rdx] = classDataAddress;

        Assert.Equal(0, PadExports.PadDeviceClassParseData(_ctx));
        Span<byte> output = stackalloc byte[24];
        Assert.True(_memory.TryRead(classDataAddress, output));
        Assert.Equal(0, BitConverter.ToInt32(output[..4]));
        Assert.Equal(1, output[4]);
        Assert.All(output[5..].ToArray(), value => Assert.Equal(0, value));
        Span<byte> guardBytes = stackalloc byte[8];
        Assert.True(_memory.TryRead(guardAddress, guardBytes));
        Assert.Equal(guard, BitConverter.ToUInt64(guardBytes));
    }

    [Fact]
    public void DeviceClassParseData_CopiesBoundedUniqueDevicePayload()
    {
        InitializePad();
        var handle = OpenPad();
        const ulong padDataAddress = Base + 0x100;
        const ulong classDataAddress = Base + 0x200;
        var padData = new byte[0x78];
        padData[0x4C] = 1;
        padData[0x6B] = 15;
        for (var index = 0; index < 12; index++)
        {
            padData[0x6C + index] = (byte)(index + 1);
        }
        Assert.True(_memory.TryWrite(padDataAddress, padData));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = padDataAddress;
        _ctx[CpuRegister.Rdx] = classDataAddress;

        Assert.Equal(0, PadExports.PadDeviceClassParseData(_ctx));
        Span<byte> output = stackalloc byte[24];
        Assert.True(_memory.TryRead(classDataAddress, output));
        Assert.Equal(-1, BitConverter.ToInt32(output[..4]));
        Assert.Equal(1, output[4]);
        Assert.Equal(12, output[8]);
        Assert.Equal(Enumerable.Range(1, 12).Select(value => (byte)value), output[12..24].ToArray());
    }

    [Theory]
    [InlineData(2, Base + 0x100, Base + 0x200, InvalidHandle)]
    [InlineData(1, 0, Base + 0x200, InvalidArgument)]
    [InlineData(1, Base + 0x100, 0, InvalidArgument)]
    public void DeviceClassParseData_ValidatesArguments(
        int handle,
        ulong padDataAddress,
        ulong classDataAddress,
        int expected)
    {
        InitializePad();
        var openedHandle = OpenPad();
        if (handle == 1)
        {
            handle = openedHandle;
        }
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = padDataAddress;
        _ctx[CpuRegister.Rdx] = classDataAddress;
        Assert.Equal(expected, PadExports.PadDeviceClassParseData(_ctx));
    }

    [Fact]
    public void DeviceClassParseData_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("IHPqcbc0zCA", out var export));
        Assert.Equal("scePadDeviceClassParseData", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
    }

    private void InitializePad()
    {
        Assert.Equal(0, PadExports.PadInit(_ctx));
    }

    private int OpenPad(int type = 0, int index = 0, ulong parameterAddress = 0, bool extended = false)
        => OpenPadForTuple(0x1000_0000, type, index, parameterAddress, extended);

    private int OpenPadForTuple(
        int userId,
        int type,
        int index = 0,
        ulong parameterAddress = 0,
        bool extended = false)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)userId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)type);
        _ctx[CpuRegister.Rdx] = unchecked((ulong)index);
        _ctx[CpuRegister.Rcx] = parameterAddress;
        return extended ? PadExports.PadOpenExt(_ctx) : PadExports.PadOpen(_ctx);
    }

    private int GetPadHandle(int type = 0, int index = 0)
        => GetPadHandleForTuple(0x1000_0000, type, index);

    private int GetPadHandleForTuple(int userId, int type, int index = 0)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)userId);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)type);
        _ctx[CpuRegister.Rdx] = unchecked((ulong)index);
        return PadExports.PadGetHandle(_ctx);
    }

    private int ClosePad(int handle)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        return PadExports.PadClose(_ctx);
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
            var handle = PadExports.PadOpen(_ctx);
            Assert.True(handle > 0);

            _ctx[CpuRegister.Rdi] = 0;
            _ctx[CpuRegister.Rsi] = dataAddress;
            Assert.Equal(InvalidHandle, PadExports.PadReadState(_ctx));

            _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
            _ctx[CpuRegister.Rsi] = dataAddress;
            Assert.Equal(0, PadExports.PadReadState(_ctx));
        }
        finally
        {
            PadExports.ResetOpenedPadForTests();
        }
    }
}

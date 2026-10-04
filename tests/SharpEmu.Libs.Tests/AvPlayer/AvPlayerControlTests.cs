// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AvPlayerControlTests : IDisposable
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const int OperationFailed = unchecked((int)0x806A0002);
    private const int NotSupported = unchecked((int)0x806A0004);
    private const ulong Handle = 0xA0_0000_2400;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private readonly CpuContext _context = new(
        new FakeCpuMemory(0x1_0000_0000, 0x1000),
        Generation.Gen5);
    private readonly object _player;

    public AvPlayerControlTests()
    {
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            width: 16,
            height: 16,
            durationMilliseconds: 1000,
            hasAudio: true);
        var players = (IDictionary)typeof(AvPlayerExports)
            .GetField("Players", PrivateStatic)!.GetValue(null)!;
        var stateGate = typeof(AvPlayerExports)
            .GetField("StateGate", PrivateStatic)!.GetValue(null)!;
        lock (stateGate)
        {
            _player = players[Handle]!;
        }
        _context[CpuRegister.Rdi] = Handle;
    }

    [Fact]
    public void StreamControlsRequireALoadedSource()
    {
        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerEnableStream(_context));
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerDisableStream(_context));
        Assert.Equal(InvalidParameters, AvPlayerExports.AvPlayerSetTrickSpeed(_context));
    }

    [Fact]
    public void AvailableBandwidthAcceptsAValidPlayerWithoutAStreamingSource()
    {
        _context[CpuRegister.Rsi] = 0;

        Assert.Equal(0, AvPlayerExports.AvPlayerSetAvailableBandwidth(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
    }

    [Fact]
    public void AvailableBandwidthRejectsAnUnknownPlayer()
    {
        _context[CpuRegister.Rdi] = Handle + 1;
        _context[CpuRegister.Rsi] = 25_000_000;

        Assert.Equal(
            InvalidParameters,
            AvPlayerExports.AvPlayerSetAvailableBandwidth(_context));
        Assert.Equal(
            unchecked((ulong)InvalidParameters),
            _context[CpuRegister.Rax]);
    }

    [Fact]
    public void StreamSelectionCanOnlyChangeWhileStopped()
    {
        SetState("SourcePath", "control-test-stream");

        _context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, AvPlayerExports.AvPlayerDisableStream(_context));
        Assert.False(GetState<bool>("VideoStreamEnabled"));
        Assert.Equal(OperationFailed, AvPlayerExports.AvPlayerDisableStream(_context));
        Assert.Equal(0, AvPlayerExports.AvPlayerEnableStream(_context));
        Assert.True(GetState<bool>("VideoStreamEnabled"));

        _context[CpuRegister.Rsi] = 2;
        Assert.Equal(OperationFailed, AvPlayerExports.AvPlayerEnableStream(_context));
        Assert.Equal(OperationFailed, AvPlayerExports.AvPlayerDisableStream(_context));

        SetState("Started", true);
        _context[CpuRegister.Rsi] = 1;
        Assert.Equal(OperationFailed, AvPlayerExports.AvPlayerDisableStream(_context));
        Assert.True(GetState<bool>("AudioStreamEnabled"));
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(0, NotSupported, 100)]
    [InlineData(399, NotSupported, 100)]
    [InlineData(-399, NotSupported, 100)]
    [InlineData(400, NotSupported, 400)]
    [InlineData(-400, NotSupported, -400)]
    [InlineData(4000, NotSupported, 3200)]
    [InlineData(-4000, NotSupported, -3200)]
    public void TrickSpeedUsesPlatformClampingAndReturnCodes(
        int requestedSpeed,
        int expectedResult,
        int expectedStoredSpeed)
    {
        SetState("SourcePath", "control-test-stream");
        _context[CpuRegister.Rsi] = unchecked((ulong)requestedSpeed);

        Assert.Equal(expectedResult, AvPlayerExports.AvPlayerSetTrickSpeed(_context));
        Assert.Equal(expectedStoredSpeed, GetState<int>("TrickSpeed"));
    }

    public void Dispose() => AvPlayerExports.RemovePlayerForTest(Handle);

    private TValue GetState<TValue>(string propertyName) =>
        (TValue)_player.GetType().GetProperty(propertyName)!.GetValue(_player)!;

    private void SetState(string propertyName, object value) =>
        _player.GetType().GetProperty(propertyName)!.SetValue(_player, value);
}

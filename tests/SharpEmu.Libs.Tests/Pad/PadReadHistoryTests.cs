// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

[CollectionDefinition(HostWindowInputCollection.Name, DisableParallelization = true)]
public sealed class HostWindowInputCollection
{
    public const string Name = "Host window input";
}

[Collection(HostWindowInputCollection.Name)]
public sealed class PadReadHistoryTests : IDisposable
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong DataAddress = Base + 0x100;
    private const int PadDataSize = 0x78;
    private const int PrimaryUserId = 0x10000000;
    private const int EnterKey = 0x0D;
    private const uint Cross = 0x4000;

    private readonly FakeCpuMemory _memory = new(Base, 0x2000);
    private readonly CpuContext _ctx;

    public PadReadHistoryTests()
    {
        HostWindowInput.Connect();
        _ctx = new CpuContext(_memory, Generation.Gen5);
        Assert.Equal(0, PadExports.PadInit(_ctx));
        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(1, PadExports.PadOpen(_ctx));
    }

    public void Dispose() => HostWindowInput.Disconnect();

    [NativeX64Fact]
    public void ATapBetweenTwoReadsReachesTheGameAsAPressAndARelease()
    {
        Assert.Equal(1, Read(64));
        Assert.Equal(0u, Buttons(0) & Cross);

        HostWindowInput.SetKey(EnterKey, true);
        HostWindowInput.SetKey(EnterKey, false);

        Assert.Equal(3, Read(64));
        Assert.Equal(Cross, Buttons(0) & Cross);
        Assert.Equal(0u, Buttons(1) & Cross);
        Assert.Equal(0u, Buttons(2) & Cross);
        Assert.True(Timestamp(0) <= Timestamp(1) && Timestamp(1) <= Timestamp(2));
        Assert.Equal(1, Read(64));
        Assert.Equal(0u, Buttons(0) & Cross);
    }

    [NativeX64Fact]
    public void ReadsOfOneStateTakeTheChangesInOrder()
    {
        Assert.Equal(1, Read(1));

        HostWindowInput.SetKey(EnterKey, true);
        HostWindowInput.SetKey(EnterKey, false);

        Assert.Equal(1, Read(1));
        Assert.Equal(Cross, Buttons(0) & Cross);
        Assert.Equal(1, Read(1));
        Assert.Equal(0u, Buttons(0) & Cross);
        Assert.Equal(1, Read(1));
        Assert.Equal(0u, Buttons(0) & Cross);
    }

    [NativeX64Fact]
    public void GamepadButtonEventsBetweenPollsReachTheGame()
    {
        HostWindowInput.SetGamepad("test pad", new HostGamepadState(true, HostGamepadButtons.None, 128, 128, 128, 128, 0, 0));
        Assert.Equal(1, Read(64));

        HostWindowInput.SetGamepadButton(HostGamepadButtons.Cross, true);
        HostWindowInput.SetGamepadButton(HostGamepadButtons.Cross, false);
        HostWindowInput.SetGamepad("test pad", new HostGamepadState(true, HostGamepadButtons.None, 128, 128, 128, 128, 0, 0));

        Assert.Equal(3, Read(64));
        Assert.Equal(Cross, Buttons(0) & Cross);
        Assert.Equal(0u, Buttons(1) & Cross);
        Assert.Equal(0u, Buttons(2) & Cross);
    }

    [NativeX64Fact]
    public void AHeldButtonStaysPressedInTheCurrentState()
    {
        Assert.Equal(1, Read(64));

        HostWindowInput.SetKey(EnterKey, true);

        Assert.Equal(2, Read(64));
        Assert.Equal(Cross, Buttons(0) & Cross);
        Assert.Equal(Cross, Buttons(1) & Cross);
        Assert.Equal(1, Read(64));
        Assert.Equal(Cross, Buttons(0) & Cross);
    }

    private int Read(int count)
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = DataAddress;
        _ctx[CpuRegister.Rdx] = (ulong)count;
        return PadExports.PadRead(_ctx);
    }

    private uint Buttons(int index) => BinaryPrimitives.ReadUInt32LittleEndian(State(index));

    private ulong Timestamp(int index) => BinaryPrimitives.ReadUInt64LittleEndian(State(index)[0x50..]);

    private byte[] State(int index)
    {
        var data = new byte[PadDataSize];
        Assert.True(_memory.TryRead(DataAddress + (ulong)(index * PadDataSize), data));
        return data;
    }
}

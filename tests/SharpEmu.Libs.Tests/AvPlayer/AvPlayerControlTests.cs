// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AvPlayerControlTests : IDisposable
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const ulong Handle = 0xA0_0000_2400;
    private readonly CpuContext _context = new(
        new FakeCpuMemory(0x1_0000_0000, 0x1000),
        Generation.Gen5);

    public AvPlayerControlTests()
    {
        AvPlayerExports.RegisterPlayerForTest(
            Handle,
            width: 16,
            height: 16,
            durationMilliseconds: 1000);
        _context[CpuRegister.Rdi] = Handle;
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

    public void Dispose() => AvPlayerExports.RemovePlayerForTest(Handle);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestContextTransferValidationTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void MappedGuestInstructionPointer_IsAccepted()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var continuation = CreateContinuation(MemoryBase + 0x100, MemoryBase + 0x800);

        Assert.True(DirectExecutionBackend.TryValidateGuestContextTransferTarget(
            memory,
            continuation,
            out var error));
        Assert.Null(error);
    }

    [Fact]
    public void UnmappedGuestInstructionPointer_IsRejected()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var continuation = CreateContinuation(MemoryBase + 0x2000, MemoryBase + 0x800);

        Assert.False(DirectExecutionBackend.TryValidateGuestContextTransferTarget(
            memory,
            continuation,
            out var error));
        Assert.Contains("not mapped guest memory", error);
    }

    [Theory]
    [InlineData(0UL, MemoryBase + 0x800)]
    [InlineData(MemoryBase + 0x100, 0UL)]
    public void InvalidInstructionOrStackPointer_IsRejected(ulong rip, ulong rsp)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var continuation = CreateContinuation(rip, rsp);

        Assert.False(DirectExecutionBackend.TryValidateGuestContextTransferTarget(
            memory,
            continuation,
            out var error));
        Assert.Contains("invalid guest context transfer target", error);
    }

    private static GuestCpuContinuation CreateContinuation(ulong rip, ulong rsp) =>
        new()
        {
            Rip = rip,
            Rsp = rsp,
        };
}

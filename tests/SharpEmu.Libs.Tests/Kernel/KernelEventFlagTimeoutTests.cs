// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelEventFlagTimeoutTests
{
    private const ulong BaseAddress = 0x1_1000_0000;
    private const ulong HandleAddress = BaseAddress + 0x100;
    private const ulong NameAddress = BaseAddress + 0x140;
    private const ulong ResultAddress = BaseAddress + 0x200;
    private const ulong TimeoutAddress = BaseAddress + 0x240;
    private const ulong WaiterCountAddress = BaseAddress + 0x280;
    private const uint WaitOr = 0x02;

    [Fact]
    public void TimedOutBlockedWaitReleasesItsWaiter()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var name = Encoding.UTF8.GetBytes("timeout-flag\0");
        Assert.True(memory.TryWrite(NameAddress, name));
        ctx[CpuRegister.Rdi] = HandleAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCreateEventFlag(ctx));
        var handle = ReadUInt64(memory, HandleAddress);

        Assert.True(memory.TryWrite(TimeoutAddress, BitConverter.GetBytes(1000u)));
        var previousThread = GuestThreadExecution.EnterGuestThread(0x801);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0801,
            resumeRsp: 0x2_0801,
            returnSlotAddress: 0x3_0801);
        IGuestThreadBlockWaiter? waiter;
        try
        {
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = 1;
            ctx[CpuRegister.Rdx] = WaitOr;
            ctx[CpuRegister.Rcx] = ResultAddress;
            ctx[CpuRegister.R8] = TimeoutAddress;
            KernelEventFlagCompatExports.KernelWaitEventFlag(ctx);
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _, out _, out _, out _, out waiter, out _));
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }

        Assert.NotNull(waiter);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            waiter.Resume());

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = WaiterCountAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventFlagCompatExports.KernelCancelEventFlag(ctx));
        Span<byte> count = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(WaiterCountAddress, count));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(count));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}

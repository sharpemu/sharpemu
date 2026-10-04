// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class RudpExportsTests : IDisposable
{
    private const int RudpErrorInvalidArgument = unchecked((int)0x80770004);
    private const int RudpErrorInvalidPollId = unchecked((int)0x8077001F);
    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    public RudpExportsTests() => RudpExports.ResetForTests();

    public void Dispose() => RudpExports.ResetForTests();

    [Fact]
    public void PollCreateReturnsDistinctPositiveIdsAndRetainsCapacity()
    {
        _ctx[CpuRegister.Rdi] = 64;
        Assert.Equal(1, RudpExports.RudpPollCreate(_ctx));
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
        Assert.True(RudpExports.TryGetPollCapacity(1, out var firstCapacity));
        Assert.Equal(64U, firstCapacity);

        _ctx[CpuRegister.Rdi] = 8;
        Assert.Equal(2, RudpExports.RudpPollCreate(_ctx));
        Assert.Equal(2UL, _ctx[CpuRegister.Rax]);
        Assert.True(RudpExports.TryGetPollCapacity(2, out var secondCapacity));
        Assert.Equal(8U, secondCapacity);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x1_0000_0000UL)]
    public void PollCreateRejectsInvalidEventCounts(ulong requestedEvents)
    {
        _ctx[CpuRegister.Rdi] = requestedEvents;

        Assert.Equal(
            RudpErrorInvalidArgument,
            RudpExports.RudpPollCreate(_ctx));
        Assert.Equal(
            unchecked((ulong)(long)RudpErrorInvalidArgument),
            _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void PollWaitExportRegistersForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));
            Assert.True(manager.TryGetExport("M6ggviwXpLs", out var export));
            Assert.Equal("sceRudpPollWait", export.Name);
            Assert.Equal("libSceRudp", export.LibraryName);
        }
    }

    [Fact]
    public void PollWaitWithZeroTimeoutReturnsNoEventsAndPreservesBuffer()
    {
        _ctx[CpuRegister.Rdi] = 64;
        var pollId = RudpExports.RudpPollCreate(_ctx);
        const ulong events = 0x1_0000_0100;
        var guard = Enumerable.Repeat((byte)0xA5, 8 * 4).ToArray();
        Assert.True(_ctx.Memory.TryWrite(events, guard));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)pollId);
        _ctx[CpuRegister.Rsi] = events;
        _ctx[CpuRegister.Rdx] = 4;
        _ctx[CpuRegister.Rcx] = 0;

        Assert.Equal(0, RudpExports.RudpPollWait(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        var observed = new byte[guard.Length];
        Assert.True(_ctx.Memory.TryRead(events, observed));
        Assert.Equal(guard, observed);
    }

    [Fact]
    public void PollWaitRejectsUnknownPollId()
    {
        _ctx[CpuRegister.Rdi] = 99;
        _ctx[CpuRegister.Rsi] = 0x1_0000_0100;
        _ctx[CpuRegister.Rdx] = 1;
        _ctx[CpuRegister.Rcx] = 1000;

        Assert.Equal(RudpErrorInvalidPollId, RudpExports.RudpPollWait(_ctx));
    }

    [Fact]
    public void PositivePollTimeoutRequestsCooperativeGuestBlock()
    {
        _ctx[CpuRegister.Rdi] = 64;
        var pollId = RudpExports.RudpPollCreate(_ctx);
        var previousThread = GuestThreadExecution.EnterGuestThread(0x1234);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000,
            resumeRsp: 0x2_0000,
            returnSlotAddress: 0x3_0000);
        try
        {
            _ctx[CpuRegister.Rdi] = unchecked((ulong)pollId);
            _ctx[CpuRegister.Rsi] = 0x1_0000_0100;
            _ctx[CpuRegister.Rdx] = 4;
            _ctx[CpuRegister.Rcx] = 1000;

            Assert.Equal(0, RudpExports.RudpPollWait(_ctx));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out var wakeKey,
                out var waiter,
                out var deadline));
            Assert.Equal("sceRudpPollWait", reason);
            Assert.Equal($"rudp-poll:{pollId}", wakeKey);
            Assert.True(hasContinuation);
            Assert.Null(waiter);
            Assert.True(deadline > 0);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }
}

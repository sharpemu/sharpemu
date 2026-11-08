// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

public sealed class FrameTickLedgerTests
{
    [Fact]
    public void BeforeAnyFrameEveryTickBelongsToTheFirstFrameAndNothingIsComplete()
    {
        var ledger = new FrameTickLedger();

        Assert.Equal(0, ledger.LatestSubmittedFrame);
        Assert.Equal(1, ledger.FrameOf(42));
        Assert.Equal(0, ledger.CompletedFrame(1000));
    }

    [Fact]
    public void TicksMapToTheFrameThatSubmittedThem()
    {
        var ledger = new FrameTickLedger();
        Assert.Equal(1, ledger.NoteFrameSubmitted(10));
        Assert.Equal(2, ledger.NoteFrameSubmitted(20));
        Assert.Equal(3, ledger.NoteFrameSubmitted(30));

        Assert.Equal(1, ledger.FrameOf(1));
        Assert.Equal(1, ledger.FrameOf(10));
        Assert.Equal(2, ledger.FrameOf(11));
        Assert.Equal(2, ledger.FrameOf(20));
        Assert.Equal(3, ledger.FrameOf(30));
        Assert.Equal(4, ledger.FrameOf(31));
    }

    [Fact]
    public void CompletedFrameIsTheLastFrameWhoseTicksAllCompleted()
    {
        var ledger = new FrameTickLedger();
        ledger.NoteFrameSubmitted(10);
        ledger.NoteFrameSubmitted(20);
        ledger.NoteFrameSubmitted(30);

        Assert.Equal(0, ledger.CompletedFrame(9));
        Assert.Equal(1, ledger.CompletedFrame(10));
        Assert.Equal(1, ledger.CompletedFrame(19));
        Assert.Equal(2, ledger.CompletedFrame(20));
        Assert.Equal(3, ledger.CompletedFrame(500));
    }

    [Fact]
    public void FrameWithoutNewTicksSharesThePreviousLastTick()
    {
        var ledger = new FrameTickLedger();
        ledger.NoteFrameSubmitted(10);
        ledger.NoteFrameSubmitted(5);

        Assert.Equal(2, ledger.CompletedFrame(10));
        Assert.Equal(1, ledger.FrameOf(10));
    }

    [Fact]
    public void OldTicksSaturateAtTheOldestRememberedFrame()
    {
        var ledger = new FrameTickLedger();
        for (var frame = 1; frame <= FrameTickLedger.Capacity * 2; frame++)
        {
            ledger.NoteFrameSubmitted((ulong)frame * 10);
        }

        var oldest = ledger.LatestSubmittedFrame - FrameTickLedger.Capacity + 1;
        Assert.Equal(oldest, ledger.FrameOf(1));
        Assert.Equal(oldest - 1, ledger.CompletedFrame(1));
        Assert.Equal(ledger.LatestSubmittedFrame, ledger.FrameOf((ulong)ledger.LatestSubmittedFrame * 10));
    }
}

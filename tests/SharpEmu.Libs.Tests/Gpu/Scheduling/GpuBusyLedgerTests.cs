// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

public sealed class GpuBusyLedgerTests
{
    [Fact]
    public void GapsBetweenBuffersAreNotCounted()
    {
        var ledger = new GpuBusyLedger();

        ledger.AddSubmission(1, [(100, 150), (400, 430)]);

        Assert.Equal(80UL, ledger.TotalBusy);
    }

    [Fact]
    public void OverlappingIntervalsAreCountedOnce()
    {
        var ledger = new GpuBusyLedger();

        ledger.AddSubmission(1, [(0, 100)]);
        ledger.AddSubmission(2, [(60, 130)]);

        Assert.Equal(130UL, ledger.TotalBusy);
    }

    [Fact]
    public void AnIntervalNestedInsideEarlierWorkAddsNothing()
    {
        var ledger = new GpuBusyLedger();

        ledger.AddSubmission(1, [(0, 100), (20, 40)]);
        ledger.AddSubmission(2, [(10, 90)]);

        Assert.Equal(100UL, ledger.TotalBusy);
        Assert.True(ledger.TryBusyAt(2, out var busy));
        Assert.Equal(100UL, busy);
    }

    [Fact]
    public void ASubmissionWithoutAvailableBuffersKeepsTheBusyTime()
    {
        var ledger = new GpuBusyLedger();

        ledger.AddSubmission(1, [(0, 10)]);
        ledger.AddSubmission(2, []);

        Assert.True(ledger.TryBusyAt(2, out var busy));
        Assert.Equal(10UL, busy);
    }

    [Fact]
    public void AHeldBackTickReadsTheNextSubmissionThatWasSignalled()
    {
        var ledger = new GpuBusyLedger();

        ledger.AddSubmission(3, [(0, 10)]);
        ledger.AddSubmission(6, [(20, 50)]);

        Assert.True(ledger.TryBusyAt(3, out var exact));
        Assert.Equal(10UL, exact);
        Assert.True(ledger.TryBusyAt(4, out var folded));
        Assert.Equal(40UL, folded);
        Assert.False(ledger.TryBusyAt(7, out _));
    }

    [Fact]
    public void OnlyTheLastSubmissionsAreKept()
    {
        var ledger = new GpuBusyLedger();
        for (ulong tick = 1; tick <= GpuBusyLedger.Capacity + 10; tick++)
        {
            ledger.AddSubmission(tick, [(tick * 10, tick * 10 + 5)]);
        }

        Assert.Equal((ulong)(GpuBusyLedger.Capacity + 10) * 5, ledger.TotalBusy);
        // Evicted ticks map to the oldest one still stored.
        Assert.True(ledger.TryBusyAt(1, out var oldest));
        Assert.Equal(11UL * 5, oldest);
        Assert.True(ledger.TryBusyAt(GpuBusyLedger.Capacity + 10, out var newest));
        Assert.Equal(ledger.TotalBusy, newest);
    }

    [Fact]
    public void AStampInsideABufferIsWorthTheBusyTimeBeforeItInThatBuffer()
    {
        var ledger = new GpuBusyLedger();
        ledger.AddBuffer(0, 100, [(1, 40)]);
        ledger.AddBuffer(200, 300, [(2, 250)]);

        Assert.True(ledger.TryStampBusy(1, out var first));
        Assert.Equal(40UL, first);
        Assert.True(ledger.TryStampBusy(2, out var second));
        Assert.Equal(150UL, second);
    }

    [Fact]
    public void AStampInABufferOverlappingTheLastOneOnlyCountsTheNewTime()
    {
        var ledger = new GpuBusyLedger();
        ledger.AddBuffer(0, 100, default);
        // The second buffer starts at 50 but its first 50 ticks were already busy.
        ledger.AddBuffer(50, 200, [(1, 80), (2, 150), (3, 900)]);

        Assert.True(ledger.TryStampBusy(1, out var early));
        Assert.Equal(100UL + 0, early);
        Assert.True(ledger.TryStampBusy(2, out var middle));
        Assert.Equal(150UL, middle);
        // A timestamp past the buffer's end cannot exceed what the buffer contributed.
        Assert.True(ledger.TryStampBusy(3, out var late));
        Assert.Equal(200UL, late);
    }

    [Fact]
    public void TwoStampsInOneBufferKeepTheirRealDifference()
    {
        var ledger = new GpuBusyLedger();
        ledger.AddBuffer(1000, 2000, [(1, 1200), (2, 1750)]);

        Assert.True(ledger.TryStampBusy(1, out var begin));
        Assert.True(ledger.TryStampBusy(2, out var end));
        Assert.Equal(550UL, end - begin);
        Assert.False(ledger.TryStampBusy(3, out _));
    }

    [Fact]
    public void OnlyTheLastStampsAreKept()
    {
        var ledger = new GpuBusyLedger();
        for (ulong id = 1; id <= GpuBusyLedger.StampCapacity + 5; id++)
        {
            ledger.AddBuffer(id * 10, id * 10 + 5, [(id, id * 10 + 2)]);
        }

        Assert.False(ledger.TryStampBusy(1, out _));
        Assert.True(ledger.TryStampBusy(GpuBusyLedger.StampCapacity + 5, out _));
    }
}

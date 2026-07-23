// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Debugger.Breakpoints;
using Xunit;

namespace SharpEmu.Debugger.Tests;

public sealed class BreakpointStoreTests
{
    [Fact]
    public void Add_AssignsIncrementalIds()
    {
        var store = new BreakpointStore();

        var first = store.Add(BreakpointKind.Execute, 0x1000);
        var second = store.Add(BreakpointKind.WriteWatch, 0x2000, length: 8);

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(2, store.Snapshot().Count);
    }

    [Fact]
    public void Add_ForcesExecuteLengthToOne()
    {
        var store = new BreakpointStore();

        var breakpoint = store.Add(BreakpointKind.Execute, 0x4000, length: 64);

        Assert.Equal(BreakpointKind.Execute, breakpoint.Kind);
        Assert.Equal(1UL, breakpoint.Length);
        Assert.True(breakpoint.Covers(0x4000));
        Assert.False(breakpoint.Covers(0x4001));
    }

    [Fact]
    public void Add_PreservesWatchLengthAtLeastOne()
    {
        var store = new BreakpointStore();

        var withLength = store.Add(BreakpointKind.ReadWatch, 0x5000, length: 16);
        var zeroClamped = store.Add(BreakpointKind.AccessWatch, 0x6000, length: 0);

        Assert.Equal(16UL, withLength.Length);
        Assert.Equal(1UL, zeroClamped.Length);
    }

    [Fact]
    public void Remove_And_SetEnabled_MutateStore()
    {
        var store = new BreakpointStore();
        var breakpoint = store.Add(BreakpointKind.Execute, 0x1000);

        Assert.True(store.SetEnabled(breakpoint.Id, enabled: false));
        Assert.False(store.Snapshot().Single().Enabled);
        Assert.True(store.Remove(breakpoint.Id));
        Assert.Empty(store.Snapshot());
        Assert.False(store.Remove(breakpoint.Id));
        Assert.False(store.SetEnabled(breakpoint.Id, enabled: true));
    }

    [Fact]
    public void FindExecuteHit_ReturnsFirstEnabledExecuteMatch()
    {
        var store = new BreakpointStore();
        var disabled = store.Add(BreakpointKind.Execute, 0x1000);
        var watch = store.Add(BreakpointKind.WriteWatch, 0x1000, length: 4);
        var hit = store.Add(BreakpointKind.Execute, 0x1000);

        store.SetEnabled(disabled.Id, enabled: false);

        var found = store.FindExecuteHit(0x1000);

        Assert.NotNull(found);
        Assert.Equal(hit.Id, found!.Id);
        Assert.Equal(BreakpointKind.Execute, found.Kind);
        Assert.NotEqual(watch.Id, found.Id);
        Assert.Null(store.FindExecuteHit(0x1001));
    }

    [Fact]
    public void Clear_RemovesAllBreakpoints()
    {
        var store = new BreakpointStore();
        store.Add(BreakpointKind.Execute, 0x1);
        store.Add(BreakpointKind.Execute, 0x2);

        store.Clear();

        Assert.Empty(store.Snapshot());
        Assert.Null(store.FindExecuteHit(0x1));
    }
}

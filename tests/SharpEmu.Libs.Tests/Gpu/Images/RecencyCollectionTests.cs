// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class RecencyCollectionTests
{
    [Fact]
    public void RetainedEntriesDoNotHideLaterCandidates()
    {
        var queue = new RecencyQueue<int>();
        for (var index = 0; index < 41; index++) queue.Insert(index, 0);
        var first = new List<int>();
        queue.CollectNextAtOrBeforeTick(100, 40, first);
        Assert.Equal(40, first.Count);
        var second = new List<int>();
        queue.CollectNextAtOrBeforeTick(100, 40, second);
        Assert.Equal(new[] { 40 }, second);
        var order = new List<int>();
        queue.ForEachItemAtOrBeforeTick(0, entry => { order.Add(entry); return false; });
        Assert.Equal(Enumerable.Range(0, 41), order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BudgetScanAdvancesPastEntriesRejectedByTheOlderCutoff(bool touch)
    {
        var queue = new RecencyQueue<int>();
        queue.Insert(1, 30);
        var middle = queue.Insert(2, 30);
        queue.Insert(3, 30);
        var seen = new List<int>();
        queue.CollectNextBudgetCandidates(84, 1, seen);
        queue.CollectNextAtOrBeforeTick(20, 20, seen);
        if (touch) queue.Touch(middle, 100);
        else queue.Free(middle);
        queue.CollectNextBudgetCandidates(84, 1, seen);
        Assert.Equal(new[] { 1, 3 }, seen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedOrTouchedCursorAdvancesToItsSuccessor(bool touch)
    {
        var queue = new RecencyQueue<int>();
        queue.Insert(1, 0);
        var middle = queue.Insert(2, 0);
        queue.Insert(3, 0);
        queue.CollectNextAtOrBeforeTick(0, 1, new List<int>());
        if (touch) queue.Touch(middle, 10);
        else
        {
            queue.Free(middle);
            queue.Insert(4, 10);
        }
        Assert.Equal(10UL, queue.GetLastUseTick(middle));
        var next = new List<int>();
        queue.CollectNextAtOrBeforeTick(0, 2, next);
        Assert.Equal(new[] { 3 }, next);
        next.Clear();
        queue.CollectNextAtOrBeforeTick(0, 1, next);
        Assert.Equal(new[] { 1 }, next);
    }
}

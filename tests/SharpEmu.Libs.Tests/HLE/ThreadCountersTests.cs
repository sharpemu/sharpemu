// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class ThreadCountersTests
{
    [Fact]
    public void IncrementsFromManyThreadsSumExactly()
    {
        var counter = new ThreadCounter();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var index = 0; index < 1_000_000; index++)
            {
                counter.Increment();
            }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        Assert.Equal(8_000_000, counter.Take());
    }

    [Fact]
    public void TakeReturnsOnlyWhatWasCountedSinceTheLastTake()
    {
        var counter = new ThreadCounter();
        counter.Add(5);
        Assert.Equal(5, counter.Take());
        counter.Increment();
        counter.Increment();
        Assert.Equal(2, counter.Take());
        Assert.Equal(0, counter.Take());
        Assert.Equal(7, counter.Total);
    }

    [Fact]
    public void CountersAreIndependent()
    {
        var first = new ThreadCounter();
        var second = new ThreadCounter();
        first.Add(3);
        second.Add(4);
        Assert.Equal(3, first.Take());
        Assert.Equal(4, second.Take());
    }
}

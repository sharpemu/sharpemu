// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class PendingFileLogTests
{
    [Fact]
    public void DrainPreservesAllLinesBeyondTheDisplayBatchLimit()
    {
        var pending = new ConcurrentQueue<(string Line, bool IsError)>();
        var expected = Enumerable.Range(0, 5001).Select(i => $"line {i}").ToArray();
        foreach (var line in expected)
        {
            pending.Enqueue((line, false));
        }
        pending.Enqueue(("final error", true));
        var written = new List<string>();
        var displayed = new List<string>();

        MainWindow.DrainPendingLogLines(pending, written.Add, displayed.Add);

        Assert.Equal(expected.Append("final error"), written);
        Assert.Equal(written, displayed);
        Assert.Empty(pending);
        MainWindow.DrainPendingLogLines(pending, written.Add, displayed.Add);
        Assert.Equal(expected.Length + 1, written.Count);
    }

    [Fact]
    public void FinalDrainIncludesLinesReceivedAfterTheFirstDrain()
    {
        var pending = new ConcurrentQueue<(string Line, bool IsError)>();
        var written = new List<string>();
        var displayed = new List<string>();
        pending.Enqueue(("first", false));
        MainWindow.DrainPendingLogLines(pending, written.Add, displayed.Add);
        pending.Enqueue(("last", true));

        MainWindow.DrainPendingLogLines(pending, written.Add, displayed.Add);

        Assert.Equal(new[] { "first", "last" }, written);
        Assert.Equal(written, displayed);
        Assert.Empty(pending);
    }

    [Fact]
    public void BatchedDrainPreservesConsoleLinesWhenFileLoggingIsDisabled()
    {
        var pending = new ConcurrentQueue<(string Line, bool IsError)>();
        var expected = Enumerable.Range(0, 1201).Select(i => $"line {i}").ToArray();
        foreach (var line in expected)
            pending.Enqueue((line, false));
        var displayed = new List<string>();

        MainWindow.DrainPendingLogLines(pending, _ => { }, displayed.Add, 500);
        Assert.Equal(500, displayed.Count);
        Assert.Equal(701, pending.Count);
        while (!pending.IsEmpty)
            MainWindow.DrainPendingLogLines(pending, _ => { }, displayed.Add, 500);

        Assert.Equal(expected, displayed);
    }
}

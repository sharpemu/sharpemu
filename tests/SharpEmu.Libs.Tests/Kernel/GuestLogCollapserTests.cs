// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// GuestLogCollapser relays guest printf output but collapses runs of identical
// lines so a chatty guest (e.g. an engine logging a shader-cache miss per shader
// every frame) cannot flood the log. Unique output must never be dropped.
public sealed class GuestLogCollapserTests
{
    // A collapser writing into a StringWriter with a fixed "\n" newline, so the
    // captured text is identical on every platform (default is "\r\n" on Windows).
    private static (GuestLogCollapser Log, StringWriter Sink) NewCollapser()
    {
        var sink = new StringWriter { NewLine = "\n" };
        return (new GuestLogCollapser("PFX ", sink), sink);
    }

    [Fact]
    public void SingleLine_IsPrintedImmediately_WithNoSummary()
    {
        var (log, sink) = NewCollapser();

        log.Write("only once");

        Assert.Equal("PFX only once\n", sink.ToString());
    }

    [Fact]
    public void DistinctLines_AreAllRelayedVerbatim()
    {
        var (log, sink) = NewCollapser();

        log.Write("a");
        log.Write("b");
        log.Write("c");

        Assert.Equal("PFX a\nPFX b\nPFX c\n", sink.ToString());
    }

    [Fact]
    public void IdenticalRun_PrintsOnce_ThenSummarisesWhenADifferentLineArrives()
    {
        var (log, sink) = NewCollapser();

        // 5000 identical lines followed by one different line: the flood must
        // collapse to the line itself plus a single "repeated" summary.
        for (var i = 0; i < 5000; i++)
        {
            log.Write("same line");
        }
        log.Write("different line");

        Assert.Equal(
            "PFX same line\n" +
            "PFX (previous message repeated 4999 more times)\n" +
            "PFX different line\n",
            sink.ToString());
    }

    [Fact]
    public void Flush_DrainsTrailingRepeats_ThatWouldOtherwiseBeLostAtExit()
    {
        var (log, sink) = NewCollapser();

        log.Write("tail");
        log.Write("tail");
        log.Write("tail");

        // No different line follows, so without an explicit flush the two extra
        // repeats would never be reported. The guest exit paths call Flush() for
        // exactly this case.
        log.Flush();

        Assert.Equal(
            "PFX tail\n" +
            "PFX (previous message repeated 2 more times)\n",
            sink.ToString());
    }

    [Fact]
    public void Flush_WithNoPendingRepeats_WritesNothingExtra()
    {
        var (log, sink) = NewCollapser();

        log.Write("x");
        log.Flush();
        log.Flush();

        Assert.Equal("PFX x\n", sink.ToString());
    }

    [Fact]
    public void MessageEndingInNewline_IsNotGivenASecondNewline()
    {
        var (log, sink) = NewCollapser();

        // The guest already terminated the line; the collapser must relay it as-is
        // rather than appending another newline.
        log.Write("has newline\n");

        Assert.Equal("PFX has newline\n", sink.ToString());
    }
}

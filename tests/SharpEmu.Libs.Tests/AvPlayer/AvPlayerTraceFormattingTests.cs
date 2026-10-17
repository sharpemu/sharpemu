// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

/// <summary>
/// The AvPlayer traces moved from "format, then decide" to "decide, then
/// format", which is only a performance change if the bytes that reach stderr
/// are the same ones. These tests capture stderr and compare the two paths
/// against each other rather than against a restated expectation.
/// </summary>
// Console.SetError is process-wide and the trace counter is static, so these
// run in the collection that already serializes the other AvPlayer tests -
// which drive the real exports and would otherwise interleave their own
// trace lines into the capture below.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AvPlayerTraceFormattingTests
{
    private static string[] Capture(Action action)
    {
        var original = Console.Error;
        var writer = new StringWriter();
        try
        {
            Console.SetError(writer);
            action();
        }
        finally
        {
            Console.SetError(original);
        }

        // Only the probe lines: anything else on stderr belongs to another test
        // and must not change what this one concludes.
        return writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("[AVPLAYER][INFO] probe ", StringComparison.Ordinal) ||
                           line.StartsWith("[AVPLAYER][INFO] once ", StringComparison.Ordinal))
            .ToArray();
    }

    // The rate limiter writes the first 32 calls and every 300th after that.
    // Both overloads have to advance the same counter and emit the same text at
    // the same positions, or a title's log would change shape.
    [Fact]
    public void InterpolatedAndPrebuiltTracesProduceIdenticalOutput()
    {
        const int calls = 650;

        AvPlayerExports.ResetTraceCountForTests();
        var viaString = Capture(() =>
        {
            for (var i = 0; i < calls; i++)
            {
                AvPlayerExports.TraceStringForTests($"probe v={i}");
            }
        });

        AvPlayerExports.ResetTraceCountForTests();
        var viaHandler = Capture(() =>
        {
            for (var i = 0; i < calls; i++)
            {
                AvPlayerExports.TraceInterpolatedForTests(i);
            }
        });

        Assert.Equal(viaString, viaHandler);
    }

    // Pin the sampling rule itself, so "identical to each other" cannot become
    // "identically broken".
    [Fact]
    public void RateLimitedTraceWritesTheFirst32AndEvery300th()
    {
        AvPlayerExports.ResetTraceCountForTests();

        var lines = Capture(() =>
        {
            for (var i = 0; i < 650; i++)
            {
                AvPlayerExports.TraceInterpolatedForTests(i);
            }
        });

        // Calls 1..32 write, then call 300 and call 600.
        Assert.Equal(34, lines.Length);
        Assert.Equal("[AVPLAYER][INFO] probe v=0", lines[0]);
        Assert.Equal("[AVPLAYER][INFO] probe v=31", lines[31]);
        Assert.Equal("[AVPLAYER][INFO] probe v=299", lines[32]);
        Assert.Equal("[AVPLAYER][INFO] probe v=599", lines[33]);
    }

    // A skipped trace still has to advance the counter, or the sampling would
    // drift the moment one call site stopped formatting.
    [Fact]
    public void SkippedTracesStillAdvanceTheSamplingCounter()
    {
        AvPlayerExports.ResetTraceCountForTests();

        var lines = Capture(() =>
        {
            // 32 writes, then 267 skipped, then the 300th writes.
            for (var i = 0; i < 300; i++)
            {
                AvPlayerExports.TraceInterpolatedForTests(i);
            }
        });

        Assert.Equal(33, lines.Length);
        Assert.Equal("[AVPLAYER][INFO] probe v=299", lines[^1]);
    }

    // The once-per-key trace must report a key exactly once, and must report
    // the same text whether the message was interpolated or prebuilt.
    [Fact]
    public void OnceTraceReportsAKeyExactlyOnce()
    {
        AvPlayerExports.ClearTracedOnceForTests();
        var key = $"probe_{Guid.NewGuid():N}";

        Assert.False(AvPlayerExports.HasTracedOnceForTests(key));

        var lines = Capture(() =>
        {
            AvPlayerExports.TraceOnceInterpolatedForTests(key, 1);
            AvPlayerExports.TraceOnceInterpolatedForTests(key, 2);
            AvPlayerExports.TraceOnceInterpolatedForTests(key, 3);
        });

        Assert.True(AvPlayerExports.HasTracedOnceForTests(key));
        Assert.Single(lines);
        Assert.Equal("[AVPLAYER][INFO] once v=1", lines[0]);
    }

    [Fact]
    public void OnceTraceFormatsTheSameTextAsAPrebuiltMessage()
    {
        var interpolatedKey = $"probe_{Guid.NewGuid():N}";
        var prebuiltKey = $"probe_{Guid.NewGuid():N}";
        AvPlayerExports.ClearTracedOnceForTests();

        var viaHandler = Capture(() =>
            AvPlayerExports.TraceOnceInterpolatedForTests(interpolatedKey, 7));
        var viaString = Capture(() =>
            AvPlayerExports.TraceOnceStringForTests(prebuiltKey, "once v=7"));

        Assert.Equal(viaString, viaHandler);
    }

    // Keys are independent: reporting one must not suppress another.
    [Fact]
    public void OnceTraceKeysAreIndependent()
    {
        AvPlayerExports.ClearTracedOnceForTests();
        var first = $"probe_{Guid.NewGuid():N}";
        var second = $"probe_{Guid.NewGuid():N}";

        var lines = Capture(() =>
        {
            AvPlayerExports.TraceOnceInterpolatedForTests(first, 1);
            AvPlayerExports.TraceOnceInterpolatedForTests(second, 2);
            AvPlayerExports.TraceOnceInterpolatedForTests(first, 3);
        });

        Assert.Equal(2, lines.Length);
        Assert.Equal("[AVPLAYER][INFO] once v=1", lines[0]);
        Assert.Equal("[AVPLAYER][INFO] once v=2", lines[1]);
    }
}

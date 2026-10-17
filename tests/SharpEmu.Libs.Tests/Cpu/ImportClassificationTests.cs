// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// The import dispatch path no longer re-derives these decisions from the NID on
// every guest call; it reads them off a table built once at stub setup. A wrong
// entry is therefore silent at runtime, which is what these tests guard.
public sealed class ImportClassificationTests
{
    // The trivial-leaf path skips the import-boundary bookkeeping: no call
    // frame, no stack arguments, no return-RIP handling. An export that can
    // park the guest thread or unwind back through its caller must never take
    // it, or the scheduler loses the frame it needs to resume.
    [Theory]
    [InlineData("WKAXJ4XBPQ4")] // scePthreadCondWait
    [InlineData("BmMjYxmew1w")] // scePthreadCondTimedwait
    [InlineData("Op8TBGY5KHg")] // pthread_cond_wait
    [InlineData("27bAgiJmOh0")] // pthread_cond_timedwait
    [InlineData("Zxa0VhQVTsk")] // sceKernelWaitSema
    [InlineData("1jfXLRVzisc")] // sceKernelUsleep
    [InlineData("9UK1vLZQft4")] // scePthreadMutexLock
    [InlineData("onNY9Byn-W8")] // scePthreadJoin
    public void BlockingImportsAreNotTrivialLeaves(string nid)
    {
        Assert.False(
            DirectExecutionBackend.IsTrivialLeafImport(nid),
            $"{nid} can park the guest thread and must keep the full gateway");
    }

    // Everything on the trivial-leaf list is a per-frame poll or a register
    // query, so it has to be declared non-blocking too; the two lists
    // disagreeing would mean the fast path and the leaf path classify the same
    // export differently.
    [Theory]
    [InlineData("h8uongcBNVs")] // sceSystemGestureGetTouchEventsCount
    [InlineData("n88vx3C5nW8")] // gettimeofday
    [InlineData("lLMT9vJAck0")] // clock_gettime
    [InlineData("UbQoYawOsfY")] // sceAvPlayerIsActive
    [InlineData("JdksQu8pNdQ")] // sceAvPlayerGetVideoDataEx
    [InlineData("YndgXqQVV7c")] // scePadReadState
    public void HotPollsAreTrivialLeaves(string nid)
    {
        Assert.True(DirectExecutionBackend.IsTrivialLeafImport(nid), nid);
    }

    // These are the results a healthy title produces constantly - a trylock on
    // a held mutex, a probe for an absent file, a timed wait that elapsed. The
    // dispatch path recognises each by one integer compare now, so the code it
    // compares against has to be the one the export actually returns.
    [Theory]
    [InlineData("eV9wAD2riIA", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND))]
    [InlineData("1G3lF1Gg1k8", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND))]
    [InlineData("gEpBkcwxUjw", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND))]
    [InlineData("27bAgiJmOh0", 60)]
    [InlineData("fzyMKs9kim0", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT))]
    [InlineData("BmMjYxmew1w", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT))]
    [InlineData("JTvBflhYazQ", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT))]
    [InlineData("9UK1vLZQft4", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK))]
    [InlineData("YndgXqQVV7c", unchecked((int)0x80920003))]
    [InlineData("K-jXhbt2gn4", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY))]
    [InlineData("upoVrzMHFeE", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY))]
    [InlineData("H2a+IN9TP0E", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN))]
    [InlineData("12wOHk8ywb0", unchecked((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY))]
    [InlineData("PIWqhn9oSxc", unchecked((int)0x80410123))]
    [InlineData("yH17Q6NWtVg", unchecked((int)0x80960007))]
    [InlineData("D-CzAxQL0XI", unchecked((int)0x80960009))]
    [InlineData("uWIYLFkkwqk", unchecked((int)0x80B2000C))]
    public void ExpectedBenignResultMatchesTheDocumentedCode(string nid, int expected)
    {
        Assert.Equal(expected, DirectExecutionBackend.ClassifyExpectedBenignResult(nid));
    }

    // 0 means "this import has no routine failure", which is what makes the
    // single compare safe: a genuine error on any other NID still logs.
    [Theory]
    [InlineData("h8uongcBNVs")]
    [InlineData("tn3VlD0hG60")]
    [InlineData("__internal_bootstrap_bridge")]
    [InlineData("")]
    public void ImportsWithoutARoutineFailureReportZero(string nid)
    {
        Assert.Equal(0, DirectExecutionBackend.ClassifyExpectedBenignResult(nid));
    }

    // A success code must never be treated as benign-and-suppressed, or a real
    // failure sharing that value would go unreported.
    [Fact]
    public void NoImportDeclaresSuccessAsItsRoutineFailure()
    {
        string[] nids =
        [
            "eV9wAD2riIA", "1G3lF1Gg1k8", "gEpBkcwxUjw", "27bAgiJmOh0", "fzyMKs9kim0",
            "BmMjYxmew1w", "JTvBflhYazQ", "9UK1vLZQft4", "YndgXqQVV7c", "K-jXhbt2gn4",
            "upoVrzMHFeE", "H2a+IN9TP0E", "12wOHk8ywb0", "PIWqhn9oSxc", "yH17Q6NWtVg",
            "D-CzAxQL0XI", "uWIYLFkkwqk",
        ];

        foreach (var nid in nids)
        {
            var code = DirectExecutionBackend.ClassifyExpectedBenignResult(nid);
            Assert.NotEqual(0, code);
            // The dispatch path only consults this once the result is already
            // non-positive, so a positive expectation could never match.
            Assert.True(code < 0 || code == 60, $"{nid} expects {code}");
        }
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class ImportResultLoggingTests
{
    [Fact]
    public void AprResolvePosixNotFound_IsExpectedFileProbeMiss()
    {
        Assert.True(DirectExecutionBackend.IsExpectedFileProbeMissResult(
            "gEpBkcwxUjw",
            (OrbisGen2Result)(-1)));
    }

    [Theory]
    [InlineData("gEpBkcwxUjw")]
    [InlineData("eV9wAD2riIA")]
    [InlineData("1G3lF1Gg1k8")]
    public void RawKernelNotFound_IsExpectedForFileProbeNids(string nid)
    {
        Assert.True(DirectExecutionBackend.IsExpectedFileProbeMissResult(
            nid,
            OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND));
    }

    [Theory]
    [InlineData("eV9wAD2riIA")]
    [InlineData("1G3lF1Gg1k8")]
    [InlineData("unrelated-nid")]
    public void PosixMinusOne_IsNotExpectedForOtherImports(string nid)
    {
        Assert.False(DirectExecutionBackend.IsExpectedFileProbeMissResult(
            nid,
            (OrbisGen2Result)(-1)));
    }

    [Theory]
    [InlineData(1ul, true)]
    [InlineData(0x00007FFFF01F7EDCul, true)]
    [InlineData(0ul, false)]
    public void KernelSemaphoreTimeout_IsExpectedOnlyForTimedWaits(
        ulong timeoutAddress,
        bool expected)
    {
        Assert.Equal(
            expected,
            DirectExecutionBackend.IsExpectedTimedKernelSemaphoreResult(
                "Zxa0VhQVTsk",
                OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                timeoutAddress));
    }

    [Fact]
    public void KernelSemaphoreTimeout_DoesNotHideOtherResultsOrImports()
    {
        Assert.False(DirectExecutionBackend.IsExpectedTimedKernelSemaphoreResult(
            "Zxa0VhQVTsk",
            OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            timeoutAddress: 1));
        Assert.False(DirectExecutionBackend.IsExpectedTimedKernelSemaphoreResult(
            "unrelated-nid",
            OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            timeoutAddress: 1));
    }

    [Theory]
    [InlineData("04AjkP0jO9U", -1, 2ul, 1ul, 60, true)]
    [InlineData("04AjkP0jO9U", -1, 2ul, 0ul, 60, false)]
    [InlineData("04AjkP0jO9U", -1, 3ul, 1ul, 60, false)]
    [InlineData("04AjkP0jO9U", -1, 2ul, 1ul, 22, false)]
    [InlineData("unrelated-nid", -1, 2ul, 1ul, 60, false)]
    public void UmtxTimeout_IsExpectedOnlyForFiniteTimedWaits(
        string nid,
        int result,
        ulong operation,
        ulong timeoutAddress,
        int errno,
        bool expected)
    {
        Assert.Equal(
            expected,
            DirectExecutionBackend.IsExpectedUmtxTimedWaitResult(
                nid,
                (OrbisGen2Result)result,
                operation,
                timeoutAddress,
                errno));
    }
}

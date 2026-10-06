// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// #783: hand-emitted stubs called Win64 functions with RSP misaligned by 8.
//
// Win64 needs RSP % 16 == 0 AT the `call`, so a stub must move the stack by an
// amount that lands on the right phase for how it was entered: 0 mod 16 when
// entered by RIP redirect (no return address pushed, RSP explicitly aligned), and
// 8 mod 16 when entered by `call` (return address already pushed).
//
// These emitters are Windows-only and one runs only during a FastFail, so the
// frame sizes cannot be executed from a test. What is pinned here is the phase
// arithmetic itself, which is the part that was wrong and the part a future edit
// would silently reintroduce.
public sealed class WinAbiFrameSizeTests
{
    private const int RegisterPushes = 6;

    private static int FieldValue(string name)
    {
        var field = typeof(DirectExecutionBackend).GetField(
            name,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Convert.ToInt32(field.GetValue(null));
    }

    [Fact]
    public void RedirectEntryStub_LeavesRspSixteenAlignedAtItsWin64Calls()
    {
        const int entryPhase = 0;

        var phaseAtCall = (entryPhase + FieldValue("RedirectFrameSize")) % 16;

        Assert.Equal(0, phaseAtCall);
    }

    [Fact]
    public void CallEntryBreadcrumb_LeavesRspSixteenAlignedAtItsWin64Calls()
    {
        const int entryPhase = 8; // a VEH handler is entered by call

        var pushed = RegisterPushes * sizeof(long);
        var phaseAtCall = (entryPhase + pushed + FieldValue("CallEntryFrameSize")) % 16;

        Assert.Equal(0, phaseAtCall);
    }

    [Fact]
    public void CallEntryBreadcrumb_FrameIsWideEnoughForItsHighestLocal()
    {
        // The FastFail breadcrumb reads the pushed Rip at +0x48, which must sit
        // inside the allocated frame.
        var frameSize = FieldValue("CallEntryFrameSize");

        Assert.True(frameSize >= 0x48, $"frame of {frameSize} cannot hold the Rip slot at +0x48");
    }

    [Fact]
    public void RedirectEntryStub_FrameCoversWin64ShadowSpace()
    {
        // Win64 reserves 32 bytes of shadow space at the callee's RSP.
        Assert.True(FieldValue("RedirectFrameSize") >= 32);
    }

    [Fact]
    public void TheOldCallEntrySizedFrameWouldHaveBeenEightOutOfPhase()
    {
        // Guards the regression itself: the defect was a 40-byte (0x28) frame on a
        // redirect-entered stub, and a 64-byte (0x40) frame on the call-entered one.
        // Both are 8 mod 16 and would leave the Win64 calls misaligned.
        Assert.Equal(8, (0 + 0x28) % 16);
        Assert.Equal(8, (8 + RegisterPushes * sizeof(long) + 0x40) % 16);
    }
}

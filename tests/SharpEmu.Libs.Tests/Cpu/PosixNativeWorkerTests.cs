// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using CorePosix = SharpEmu.Core.Cpu.Native.PosixHostStubs;
using HlePosix = SharpEmu.HLE.Host.Posix.PosixHostStubs;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed unsafe class PosixNativeWorkerTests
{
    // Mirrors the native guest worker teardown: the raw thread signals its
    // done-event when it leaves the run loop, the owner waits on that event,
    // then reaps the thread with a blocking join. JoinWorkerThread hanging
    // (the failure this guards against) would time the test out.
    [Fact]
    public void WorkerSignalsDoneEventAndJoins()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var done = CorePosix.CreateWorkerEvent();
        Assert.NotEqual(0, done);
        try
        {
            var worker = HlePosix.CreateWorkerThread(
                (nint)(delegate* unmanaged<nint, nint>)&SignalDone,
                done,
                4u * 1024u * 1024u,
                out _);
            Assert.NotEqual(0, worker);
            Assert.True(CorePosix.WaitWorkerEvent(done, 1000));
            HlePosix.JoinWorkerThread(worker);
        }
        finally
        {
            CorePosix.DestroyWorkerEvent(done);
        }
    }

    [UnmanagedCallersOnly]
    private static nint SignalDone(nint doneHandle)
    {
        _ = CorePosix.SignalWorkerEvent(doneHandle);
        return 0;
    }
}

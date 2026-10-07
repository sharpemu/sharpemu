// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// The POSIX stand-in for kernel32!Sleep takes milliseconds in ecx. It must sleep for a second or
// more as well: macOS usleep rejects such lengths with EINVAL, which made the stub return at once.
public sealed unsafe class PosixSleepStubTests
{
    [NativeX64Theory]
    [InlineData(1200u, 1.0, 4.0)]
    [InlineData(20u, 0.015, 1.0)]
    public void SleepStubSleepsForTheRequestedMilliseconds(uint milliseconds, double minSeconds, double maxSeconds)
    {
        if (OperatingSystem.IsWindows()) return;
        // SysV passes the fourth integer argument in rcx, where the stub reads its milliseconds.
        var sleep = (delegate* unmanaged<nuint, nuint, nuint, uint, void>)PosixHostStubs.SleepStubAddress;
        var started = Stopwatch.GetTimestamp();
        sleep(0, 0, 0, milliseconds);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Assert.InRange(elapsed, minSeconds, maxSeconds);
    }
}

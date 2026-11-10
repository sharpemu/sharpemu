// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Threading;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// The stall watchdog reads the live registers of the thread executing guest code by suspending
/// it. Guest code runs natively, so the managed CpuContext still holds whatever execution was
/// entered with — reporting its RIP names the entry point rather than where the guest is stuck.
///
/// Suspending the thread it is diagnosing is the risk in that: a capture that fails to resume
/// would freeze the emulator at exactly the moment someone is trying to find out why it stopped.
/// </summary>
public sealed class HostThreadContextCaptureTests
{
    private static readonly MethodInfo Capture = typeof(DirectExecutionBackend).GetMethod(
        "TryCaptureHostThreadContext",
        BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>
    /// A running thread must keep running afterwards. This is the property that keeps the
    /// watchdog from deadlocking the process it reports on.
    /// </summary>
    [Fact]
    public void LeavesTheCapturedThreadRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var progress = 0L;
        var stop = false;
        var started = new ManualResetEventSlim();
        var threadId = 0;

        var worker = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            started.Set();
            while (!Volatile.Read(ref stop))
            {
                Interlocked.Increment(ref progress);
            }
        })
        { IsBackground = true };

        worker.Start();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

            Assert.True(TryCapture(threadId, out var rip, out var rsp));
            Assert.NotEqual(0UL, rip);
            Assert.NotEqual(0UL, rsp);

            var afterCapture = Interlocked.Read(ref progress);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Interlocked.Read(ref progress) == afterCapture && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1);
            }

            Assert.True(
                Interlocked.Read(ref progress) > afterCapture,
                "the captured thread made no progress afterwards - it was left suspended");
        }
        finally
        {
            Volatile.Write(ref stop, true);
            worker.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Capturing the calling thread would suspend the watchdog itself, which never returns. It
    /// has to decline instead.
    /// </summary>
    [Fact]
    public void DeclinesToCaptureTheCallingThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(TryCapture(GetCurrentThreadId(), out _, out _));
    }

    /// <summary>
    /// Thread id 0 is what the watchdog reads when no guest execution is in progress. It must
    /// fall back to the managed context rather than reporting invented registers.
    /// </summary>
    [Fact]
    public void DeclinesWhenNoThreadIsExecuting()
    {
        Assert.False(TryCapture(0, out _, out _));
    }

    private static bool TryCapture(int hostThreadId, out ulong rip, out ulong rsp)
    {
        object?[] args = [hostThreadId, null];
        var captured = (bool)Capture.Invoke(null, args)!;
        var snapshot = args[1]!;
        var type = snapshot.GetType();
        rip = (ulong)type.GetProperty("Rip")!.GetValue(snapshot)!;
        rsp = (ulong)type.GetProperty("Rsp")!.GetValue(snapshot)!;
        return captured;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();
}

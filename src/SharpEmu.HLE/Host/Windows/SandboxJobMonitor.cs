// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Watches the job of a sandboxed child through an I/O completion port and reports each time the child tried to
/// start another process, which the one-process job limit refused. Attach it before the child is resumed and
/// dispose it with the job.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class SandboxJobMonitor : IDisposable
{
    private const int JobObjectAssociateCompletionPortInformationClass = 7;
    private const uint JobObjectMsgActiveProcessLimit = 3;
    private const uint JobObjectMsgActiveProcessZero = 4;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly nint JobKey = 1;
    private static readonly nint StopKey = 2;

    private readonly nint _port;
    private readonly Thread _thread;
    private int _blockedCount;
    private int _disposed;

    private SandboxJobMonitor(nint port)
    {
        _port = port;
        _thread = new Thread(Run, 128 * 1024)
        {
            IsBackground = true,
            Name = "SharpEmu sandbox job monitor",
        };
    }

    /// <summary>Raised on the monitor thread for every refused process creation; the argument is the running count.</summary>
    public event Action<int>? ProcessLaunchBlocked;

    /// <summary>How many process creations the job has refused so far.</summary>
    public int BlockedCount => Volatile.Read(ref _blockedCount);

    /// <summary>Associates a new completion port with the job and starts listening. Null when the job refuses it.</summary>
    public static SandboxJobMonitor? Attach(nint job)
    {
        var port = CreateIoCompletionPort(-1, 0, 0, 1);
        if (port == 0)
        {
            return null;
        }

        var association = new JobAssociateCompletionPort { CompletionKey = JobKey, CompletionPort = port };
        bool associated;
        unsafe
        {
            associated = SetInformationJobObject(
                job, JobObjectAssociateCompletionPortInformationClass, (nint)(&association), (uint)sizeof(JobAssociateCompletionPort));
        }

        if (!associated)
        {
            CloseHandle(port);
            return null;
        }

        var monitor = new SandboxJobMonitor(port);
        monitor._thread.Start();
        return monitor;
    }

    private void Run()
    {
        while (GetQueuedCompletionStatus(_port, out var message, out var key, out _, Infinite))
        {
            if (key == StopKey)
            {
                return;
            }

            if (key != JobKey)
            {
                continue;
            }

            if (message == JobObjectMsgActiveProcessLimit)
            {
                var count = Interlocked.Increment(ref _blockedCount);
                try
                {
                    ProcessLaunchBlocked?.Invoke(count);
                }
                catch (Exception)
                {
                    // A faulty subscriber must not stop the monitor.
                }
            }
            else if (message == JobObjectMsgActiveProcessZero)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = PostQueuedCompletionStatus(_port, 0, StopKey, 0);
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        CloseHandle(_port);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAssociateCompletionPort
    {
        public nint CompletionKey;
        public nint CompletionPort;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateIoCompletionPort(nint fileHandle, nint existingPort, nint key, uint threads);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetQueuedCompletionStatus(
        nint port, out uint bytes, out nint key, out nint overlapped, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PostQueuedCompletionStatus(nint port, uint bytes, nint key, nint overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}

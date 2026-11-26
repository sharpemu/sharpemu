// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class SandboxJobMonitorTests
{
    private const uint CreateSuspended = 0x4;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectLimitActiveProcess = 0x8;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [Fact]
    public void ProcessLaunchBlocked_FiresWhenTheChildStartsAnotherProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = CreateJob();
        try
        {
            using var monitor = SandboxJobMonitor.Attach(job);
            Assert.NotNull(monitor);
            using var blocked = new ManualResetEventSlim();
            var reported = 0;
            monitor!.ProcessLaunchBlocked += count =>
            {
                reported = count;
                blocked.Set();
            };

            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var commandLine = new StringBuilder($"\"{cmd}\" /c \"\"{cmd}\" /c exit 0\"");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            Assert.True(CreateProcessW(
                null, commandLine, 0, 0, false, CreateSuspended | CreateNoWindow, 0, null, ref startup, out var info));
            try
            {
                Assert.True(AssignProcessToJobObject(job, info.Process));
                Assert.NotEqual(uint.MaxValue, ResumeThread(info.Thread));
                Assert.True(blocked.Wait(TimeSpan.FromSeconds(10)), "the refused process creation was not reported");
                Assert.True(reported >= 1);
                Assert.True(monitor.BlockedCount >= 1);
            }
            finally
            {
                _ = TerminateProcess(info.Process, 1);
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
        }
        finally
        {
            CloseHandle(job);
        }
    }

    [Fact]
    public void Dispose_ReturnsPromptlyWhenNothingWasReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = CreateJob();
        try
        {
            var monitor = SandboxJobMonitor.Attach(job);
            Assert.NotNull(monitor);
            var raised = 0;
            monitor!.ProcessLaunchBlocked += _ => raised++;

            var watch = Stopwatch.StartNew();
            monitor.Dispose();
            monitor.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(0, raised);
            Assert.Equal(0, monitor.BlockedCount);
        }
        finally
        {
            CloseHandle(job);
        }
    }

    private static nint CreateJob()
    {
        var job = CreateJobObjectW(0, null);
        Assert.NotEqual(0, job);
        var info = new JobExtendedLimit();
        info.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess;
        info.ActiveProcessLimit = 1;
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<JobExtendedLimit>());
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            Assert.True(SetInformationJobObject(job, 9, memory, (uint)Marshal.SizeOf<JobExtendedLimit>()));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }

        return job;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION; only the basic limits are used.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Pointer;
        public nint StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class WindowsDebuggerBreakTests
{
    [Fact]
    public void OnlyFirstChanceBreakpointsOnDebuggerThreadsAreAccepted()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        var tracker = LoadSystemLibrary(out var threadEntry, out var breakpoint);
        tracker.Observe(new() { Kind = 2, ThreadId = 7, ThreadStartAddress = threadEntry });
        var debugEvent = Breakpoint(7, breakpoint);
        Assert.True(tracker.IsDebuggerBreak(debugEvent));
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(8, breakpoint)));
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(7, breakpoint + 1)));
        debugEvent.FirstChance = 0;
        Assert.False(tracker.IsDebuggerBreak(debugEvent));
        debugEvent = Breakpoint(7, breakpoint);
        debugEvent.Exception.Code = 0xC0000005;
        Assert.False(tracker.IsDebuggerBreak(debugEvent));
        tracker.Observe(new() { Kind = 2, ThreadId = 8, ThreadStartAddress = threadEntry + 1 });
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(8, breakpoint)));
        tracker.Observe(new() { Kind = 3, ThreadId = 9, ProcessThreadStartAddress = threadEntry + 1 });
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(9, breakpoint)));
    }

    [Fact]
    public void ThreadExitReuseAndLibraryUnloadRemoveOldEvidence()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        var tracker = LoadSystemLibrary(out var threadEntry, out var breakpoint);
        tracker.Observe(new() { Kind = 2, ThreadId = 7, ThreadStartAddress = threadEntry });
        Assert.True(tracker.IsDebuggerBreak(Breakpoint(7, breakpoint)));
        tracker.Observe(new() { Kind = 4, ThreadId = 7 });
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(7, breakpoint)));
        tracker.Observe(new() { Kind = 2, ThreadId = 7, ThreadStartAddress = threadEntry + 1 });
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(7, breakpoint)));
        tracker.Observe(new() { Kind = 2, ThreadId = 7, ThreadStartAddress = threadEntry });
        tracker.Observe(new() { Kind = 7, UnloadedLibraryBase = 0x71000000 });
        Assert.True(tracker.IsDebuggerBreak(Breakpoint(7, breakpoint)));
        tracker.Observe(new() { Kind = 7, UnloadedLibraryBase = 0x70000000 });
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(7, breakpoint)));
    }

    [Fact]
    public void MissingLibraryEvidenceNeverAuthorizesABreak()
    {
        var tracker = new WindowsCrashCapture.DebuggerBreakTracker();
        tracker.Observe(new() { Kind = 6, FileHandle = 0, LoadedLibraryBase = 0x70000000 });
        tracker.Observe(new() { Kind = 2, ThreadId = 7, ThreadStartAddress = 0 });
        Assert.False(tracker.IsDebuggerBreak(Breakpoint(7, 0)));
    }

    private static WindowsCrashCapture.DebuggerBreakTracker LoadSystemLibrary(out nint threadEntry, out nint breakpoint)
    {
        var module = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "ntdll.dll"));
        try
        {
            const int targetBase = 0x70000000;
            threadEntry = targetBase + NativeLibrary.GetExport(module, "DbgUiRemoteBreakin") - module;
            breakpoint = targetBase + NativeLibrary.GetExport(module, "DbgBreakPoint") - module;
            var tracker = new WindowsCrashCapture.DebuggerBreakTracker();
            using var file = File.OpenRead(Path.Combine(Environment.SystemDirectory, "ntdll.dll"));
            tracker.Observe(new() { Kind = 6, FileHandle = file.SafeFileHandle.DangerousGetHandle(), LoadedLibraryBase = targetBase });
            return tracker;
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }

    private static WindowsCrashCapture.DebugEvent Breakpoint(uint threadId, nint address) => new()
    {
        Kind = 1,
        ThreadId = threadId,
        FirstChance = 1,
        Exception = new() { Code = 0x80000003, Address = address },
    };
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.Core.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class WindowsCrashCaptureTests
{
    [Fact]
    public void NativeLayoutsMatchWindowsX64()
    {
        Assert.Equal(176, Marshal.SizeOf<WindowsCrashCapture.DebugEvent>());
        Assert.Equal(152, Marshal.SizeOf<WindowsCrashCapture.ExceptionRecord>());
        Assert.Equal(1232, Marshal.SizeOf<WindowsCrashCapture.ThreadContext>());
        Assert.Equal(16, Marshal.SizeOf<WindowsCrashCapture.DumpExceptionInformation>());
        Assert.Equal(168, Marshal.OffsetOf<WindowsCrashCapture.DebugEvent>("FirstChance").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<WindowsCrashCapture.DebugEvent>("LoadedLibraryBase").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<WindowsCrashCapture.DebugEvent>("ThreadStartAddress").ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<WindowsCrashCapture.DebugEvent>("ProcessThreadStartAddress").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<WindowsCrashCapture.ExceptionRecord>("Parameters").ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<WindowsCrashCapture.DumpExceptionInformation>("Pointers").ToInt32());
    }

    [Fact]
    public void InvalidHelperArgumentsAreRejected()
    {
        Assert.Equal(1, WindowsCrashCapture.RunHelper([]));
        Assert.Equal(1, WindowsCrashCapture.RunHelper([WindowsCrashCapture.HelperArgument, "0", "unused", "unused"]));
    }


    [Fact]
    public async Task FailedAttachIsReportedBeforeStartup()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;
        var reportPath = Path.Combine(Path.GetTempPath(), $"sharpemu-crash-attach-{Guid.NewGuid():N}");
        var readyEventName = $"Local\\SharpEmu-Crash-Ready-{Guid.NewGuid():N}";
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
        var helper = Task.Factory.StartNew(() => WindowsCrashCapture.RunHelper(
            [WindowsCrashCapture.HelperArgument, uint.MaxValue.ToString(), readyEventName, reportPath]),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            using var helperExited = ((IAsyncResult)helper).AsyncWaitHandle;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Task.Run(() => WindowsCrashCapture.WaitForHelper(readyEvent, helperExited)).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, await helper.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("Attach to the game process failed (Windows error", File.ReadAllText(reportPath + ".capture.log"));
        }
        finally
        {
            File.Delete(reportPath + ".capture.log");
        }
    }

    [Theory]
    [InlineData("clean", false)]
    [InlineData("handled", false)]
    [InlineData("access-violation", true)]
    [InlineData("fast-fail", true)]
    [InlineData("dump-failure", false)]
    [InlineData("debugger-breaks", false)]
    [InlineData("debugger-breaks-then-crash", true)]
    [InlineData("application-breakpoint", true)]
    [InlineData("application-debugbreak", true)]
    public async Task CapturesOnlyUnhandledExceptions(string scenario, bool expectDump)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-crash-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var dumpPath = Path.Combine(directory, "probe.dmp");
        if (scenario == "dump-failure")
            Directory.CreateDirectory(dumpPath);
        var readyEventName = $"Local\\SharpEmu-Crash-Ready-{Guid.NewGuid():N}";
        var eventName = $"Local\\SharpEmu-Crash-Test-{Guid.NewGuid():N}";
        using var startEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildProbe(eventName, scenario))));
        startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var target = Process.Start(startInfo)!;
        var standardOutput = target.StandardOutput.ReadToEndAsync();
        var standardError = target.StandardError.ReadToEndAsync();
        var helper = Task.Factory.StartNew(() => WindowsCrashCapture.RunHelper(
            [WindowsCrashCapture.HelperArgument, target.Id.ToString(), readyEventName, dumpPath]),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            using var helperExited = ((IAsyncResult)helper).AsyncWaitHandle;
            await Task.Run(() => WindowsCrashCapture.WaitForHelper(readyEvent, helperExited)).WaitAsync(TimeSpan.FromSeconds(45));
            if (scenario.StartsWith("debugger-breaks", StringComparison.Ordinal))
            {
                for (var breakIndex = 1; breakIndex <= 3; breakIndex++)
                {
                    Assert.True(DebugBreakProcess(target.Handle));
                    await WaitForDebuggerBreakAsync(target, dumpPath + ".capture.log", breakIndex);
                }
            }
            startEvent.Set();
            await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var helperResult = await helper.WaitAsync(TimeSpan.FromSeconds(45));
            var output = await standardOutput + await standardError;
            Assert.Contains("probe-started", output);
            Assert.Equal(scenario == "dump-failure" ? 1 : 0, helperResult);
            Assert.Equal(expectDump, File.Exists(dumpPath));
            var report = File.ReadAllText(dumpPath + ".capture.log");
            if (expectDump)
            {
                Assert.Contains("Dump complete.", report);
                Assert.NotEqual(0, target.ExitCode);
                VerifyDump(dumpPath, scenario == "fast-fail" ? 0xC0000409u :
                    scenario.StartsWith("application-", StringComparison.Ordinal) ? 0x80000003u : 0xC0000005u);
            }
            else if (scenario == "dump-failure")
            {
                Assert.Contains("Capture failed:", report);
                Assert.NotEqual(0, target.ExitCode);
            }
            else
            {
                Assert.Equal(0, target.ExitCode);
                Assert.Contains("probe-completed", output);
                Assert.DoesNotContain("Unhandled exception:", report);
            }
        }
        finally
        {
            startEvent.Set();
            if (!target.HasExited)
                target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
            await helper.WaitAsync(TimeSpan.FromSeconds(15));
            Directory.Delete(directory, recursive: true);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DebugBreakProcess(nint process);

    private static async Task WaitForDebuggerBreakAsync(Process target, string reportPath, int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            string report;
            using (var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
                report = await reader.ReadToEndAsync(timeout.Token);
            if (report.Split("Debugger break continued.", StringSplitOptions.None).Length - 1 >= expectedCount)
                return;
            Assert.False(target.HasExited, report);
            Assert.DoesNotContain("Capture failed:", report);
            Assert.DoesNotContain("Unhandled exception:", report);
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void VerifyDump(string path, uint exceptionCode)
    {
        using var input = new BinaryReader(File.OpenRead(path));
        Assert.Equal(0x504D444Du, input.ReadUInt32());
        input.ReadUInt32();
        var streamCount = input.ReadUInt32();
        var directoryOffset = input.ReadUInt32();
        var streams = new Dictionary<uint, uint>();
        input.BaseStream.Position = directoryOffset;
        for (var index = 0; index < streamCount; index++)
        {
            var type = input.ReadUInt32();
            input.ReadUInt32();
            streams[type] = input.ReadUInt32();
        }
        Assert.Contains(3u, streams.Keys);
        Assert.Contains(4u, streams.Keys);
        Assert.Contains(9u, streams.Keys);
        Assert.Contains(16u, streams.Keys);
        input.BaseStream.Position = streams[6];
        Assert.NotEqual(0u, input.ReadUInt32());
        input.ReadUInt32();
        Assert.Equal(exceptionCode, input.ReadUInt32());
        input.BaseStream.Position = streams[6] + 24;
        var exceptionAddress = input.ReadUInt64();
        Assert.NotEqual(0ul, exceptionAddress);
        input.BaseStream.Position = streams[6] + 164;
        var contextOffset = input.ReadUInt32();
        input.BaseStream.Position = contextOffset + 248;
        var instructionAddress = input.ReadUInt64();
        if (exceptionCode == 0x80000003u)
            Assert.InRange(instructionAddress, exceptionAddress, exceptionAddress + 1);
        else
            Assert.Equal(exceptionAddress, instructionAddress);

        input.BaseStream.Position = streams[9];
        var rangeCount = input.ReadUInt64();
        var memoryOffset = input.ReadUInt64();
        for (ulong rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
        {
            var address = input.ReadUInt64();
            var size = input.ReadUInt64();
            if (exceptionAddress >= address && exceptionAddress - address < size)
            {
                input.BaseStream.Position = checked((long)(memoryOffset + exceptionAddress - address));
                if (exceptionCode == 0x80000003u)
                    Assert.Equal(0xCC, input.ReadByte());
                else
                    Assert.Equal(exceptionCode == 0xC0000409u ? (ushort)0x29CD : (ushort)0x008B, input.ReadUInt16());
                return;
            }
            memoryOffset += size;
        }
        Assert.Fail("The dump does not contain the faulting instruction.");

    }

    private static string BuildProbe(string eventName, string scenario) => $$"""
        $ErrorActionPreference = 'Stop'
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class CrashProbe
        {
            [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
            [DllImport("kernel32.dll")] static extern IntPtr CreateThread(IntPtr attributes, UIntPtr stack, IntPtr entry, IntPtr argument, uint flags, out uint threadId);
            [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
            [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll")] static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);
            [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
            [DllImport("kernel32.dll")] static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
            public static void Run(string scenario)
            {
                SetErrorMode(2);
                byte[] payload = scenario == "clean" || scenario == "debugger-breaks" ? new byte[] { 0x31,0xC0,0xC3 } :
                    scenario == "application-breakpoint" ? new byte[] { 0xCC,0x31,0xC0,0xC3 } :
                    scenario == "fast-fail" ? new byte[] { 0xB9,7,0,0,0,0xCD,0x29 } :
                    new byte[] { 0xB9,0xE8,3,0,0,0x31,0xC0,0x8B,0,0xFF,0xC9,0x75,0xF8,0x31,0xC0,0xC3 };
                if (scenario == "application-debugbreak")
                {
                    var entry = GetProcAddress(GetModuleHandle("ntdll.dll"), "DbgBreakPoint");
                    if (entry == IntPtr.Zero) throw new InvalidOperationException("Could not find the breakpoint entry.");
                    payload = new byte[] { 0x48,0x83,0xEC,0x28,0x48,0xB8,0,0,0,0,0,0,0,0,
                        0xFF,0xD0,0x48,0x83,0xC4,0x28,0x31,0xC0,0xC3 };
                    BitConverter.GetBytes(entry.ToInt64()).CopyTo(payload, 6);
                }
                if (scenario == "handled")
                {
                    byte[] handler = {
                        0x48,0x8B,0x01,0x81,0x38,0x05,0,0,0xC0,0x75,0x12,
                        0x48,0x8B,0x41,0x08,0x48,0x83,0x80,0xF8,0,0,0,2,
                        0xB8,0xFF,0xFF,0xFF,0xFF,0xC3,0x31,0xC0,0xC3 };
                    if (AddVectoredExceptionHandler(1, Allocate(handler)) == IntPtr.Zero)
                        throw new InvalidOperationException("Could not add the test fault handler.");
                }
                uint threadId;
                var thread = CreateThread(IntPtr.Zero, UIntPtr.Zero, Allocate(payload), IntPtr.Zero, 0, out threadId);
                if (thread == IntPtr.Zero) throw new InvalidOperationException("Could not start the test thread.");
                WaitForSingleObject(thread, uint.MaxValue);
                CloseHandle(thread);
            }
            static IntPtr Allocate(byte[] code)
            {
                var address = VirtualAlloc(IntPtr.Zero, (UIntPtr)code.Length, 0x3000, 0x40);
                if (address == IntPtr.Zero) throw new InvalidOperationException("Could not allocate test code.");
                Marshal.Copy(code, 0, address, code.Length);
                FlushInstructionCache(new IntPtr(-1), address, (UIntPtr)code.Length);
                return address;
            }
        }
        '@
        $start = [System.Threading.EventWaitHandle]::OpenExisting('{{eventName}}')
        $null = $start.WaitOne()
        Write-Output 'probe-started'
        [CrashProbe]::Run('{{scenario}}')
        Write-Output 'probe-completed'
        exit 0
        """;
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Core.Diagnostics;

public static partial class WindowsCrashCapture
{
    public const string HelperArgument = "--sharpemu-crash-helper";
    private const string CaptureVariable = "SHARPEMU_CRASH_CAPTURE";

    public static void StartIfEnabled(string? logFilePath)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(CaptureVariable), "1", StringComparison.Ordinal))
            return;

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.Error.WriteLine("[CRASH][WARN] Crash capture requires a Windows x64 process.");
            return;
        }

        try
        {
            var logPath = string.IsNullOrWhiteSpace(logFilePath)
                ? Path.Combine(AppContext.BaseDirectory, "user", "logs", $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log")
                : Path.GetFullPath(logFilePath);
            var dumpPath = Path.ChangeExtension(logPath, $"crash-{Environment.ProcessId}.dmp");
            Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
            var readyEventName = $"Local\\SharpEmu-Crash-{Guid.NewGuid():N}";
            using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is not available.");
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            startInfo.ArgumentList.Add(HelperArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(readyEventName);
            startInfo.ArgumentList.Add(dumpPath);
            startInfo.Environment.Remove(CaptureVariable);

            // The helper stays attached until this process exits, including runtime teardown.
            using var helper = Process.Start(startInfo) ?? throw new InvalidOperationException("The crash helper did not start.");
            using var exited = new ProcessExitWaitHandle(helper);
            WaitForHelper(readyEvent, exited);
            Console.Error.WriteLine($"[CRASH][INFO] Crash capture ready. Dump: {dumpPath}");
            Console.Error.WriteLine($"[CRASH][INFO] Capture report: {dumpPath}.capture.log");
            Console.Error.WriteLine($"[CRASH][INFO] Runtime: {RuntimeInformation.FrameworkDescription}. Debugging can change timing.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[CRASH][ERROR] Crash capture is not active: {exception.Message}");
        }
    }

    internal static void WaitForHelper(WaitHandle ready, WaitHandle exited)
    {
        if (WaitHandle.WaitAny([ready, exited]) != 0)
            throw new InvalidOperationException("The crash helper exited before capture was ready. Check the capture report.");
    }

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(Process process) =>
            SafeWaitHandle = new SafeWaitHandle(process.Handle, ownsHandle: false);
    }

    public static int RunHelper(string[] arguments)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            arguments.Length != 4 || arguments[0] != HelperArgument ||
            !uint.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId == 0)
            return 1;

        var attached = false;
        StreamWriter? report = null;
        try
        {
            report = new StreamWriter(arguments[3] + ".capture.log", append: true) { AutoFlush = true };
            report.WriteLine($"Capture helper started. Process: {processId}. Dump: {arguments[3]}");
            report.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}. OS: {Environment.OSVersion}.");
            using var readyEvent = EventWaitHandle.OpenExisting(arguments[2]);
            if (!DebugActiveProcess(processId))
                throw NativeFailure("Attach to the game process");
            attached = true;
            if (!DebugSetProcessKillOnExit(false))
                throw NativeFailure("Keep the game alive if the helper exits");
            return ProcessDebugEvents(processId, arguments[3], readyEvent, report);
        }
        catch (Exception exception)
        {
            report?.WriteLine($"Capture failed: {exception.Message}");
            Console.Error.WriteLine($"[CRASH][ERROR] Capture failed: {exception.Message}");
            return 1;
        }
        finally
        {
            if (attached)
                _ = DebugActiveProcessStop(processId);
            report?.Dispose();
        }
    }

    private static int ProcessDebugEvents(uint processId, string dumpPath, EventWaitHandle readyEvent, TextWriter report)
    {
        var initialBreakpoint = true;
        var captured = false;
        while (true)
        {
            if (!WaitForDebugEventEx(out var debugEvent, uint.MaxValue))
                throw NativeFailure("Wait for a debug event");

            var continuation = DebugContinue;
            var ready = false;
            try
            {
                if (debugEvent.Kind is CreateProcessEvent or LoadLibraryEvent && debugEvent.FileHandle != 0)
                    _ = CloseHandle(debugEvent.FileHandle);
                if (debugEvent.Kind == ExceptionEvent)
                {
                    continuation = DebugExceptionNotHandled;
                    if (initialBreakpoint && debugEvent.FirstChance != 0 && debugEvent.Exception.Code == BreakpointException)
                    {
                        initialBreakpoint = false;
                        continuation = DebugContinue;
                        ready = true;
                    }
                    else if (debugEvent.FirstChance == 0 && !captured)
                    {
                        captured = true;
                        report.WriteLine($"Unhandled exception: 0x{debugEvent.Exception.Code:X8}. " +
                            $"Address: 0x{debugEvent.Exception.Address:X16}. Thread: {debugEvent.ThreadId}.");
                        WriteDump(processId, debugEvent, dumpPath);
                        report.WriteLine("Dump complete.");
                    }
                }
            }
            finally
            {
                // Even a failed dump must leave the original exception unhandled.
                if (!ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, continuation))
                    throw NativeFailure("Continue a debug event");
            }

            if (ready)
            {
                report.WriteLine("Capture ready. First-chance exceptions remain unhandled by the helper.");
                readyEvent.Set();
            }
            if (debugEvent.Kind == ExitProcessEvent)
            {
                report.WriteLine($"Process exited: 0x{debugEvent.ExitCode:X8}. Dump captured: {captured}.");
                return 0;
            }
        }
    }

    private static Win32Exception NativeFailure(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation} failed (Windows error {error}).");
    }
}

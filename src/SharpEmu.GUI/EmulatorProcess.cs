// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.GUI;

/// <summary>
/// Launches the SharpEmu CLI as a child process with the same CET/CFG mitigation
/// opt-outs the CLI would apply to its own relaunched child, while capturing
/// stdout/stderr through pipes. The CLI's internal relaunch is suppressed via
/// SHARPEMU_DISABLE_MITIGATION_RELAUNCH so output is not lost to a detached
/// console. A kill-on-close job object ties the emulator's lifetime to the GUI.
/// </summary>
internal sealed class EmulatorProcess : IDisposable
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint INFINITE = 0xFFFFFFFF;
    private const int PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY = 0x00020007;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_OFF = 0x00000002UL << 40;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF = 0x00000002UL << 28;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_USER_CET_SET_CONTEXT_IP_VALIDATION_ALWAYS_OFF = 0x00000002UL << 32;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_XTENDED_CONTROL_FLOW_GUARD_ALWAYS_OFF = 0x00000002UL << 40;

    private readonly object _sync = new();
    private nint _processHandle;
    private nint _jobHandle;
    private Process? _fallbackProcess;
    private bool _running;
    private bool _disposed;

    public event Action<string, bool>? OutputReceived;

    public event Action<int>? Exited;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _running;
            }
        }
    }

    public void Start(string exePath, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("The emulator process is already running.");
            }

            if (OperatingSystem.IsWindows())
            {
                StartWindows(exePath, arguments, workingDirectory);
            }
            else
            {
                StartFallback(exePath, arguments, workingDirectory);
            }

            _running = true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            if (_processHandle != 0)
            {
                _ = TerminateProcess(_processHandle, 1);
            }

            try
            {
                _fallbackProcess?.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private void StartWindows(string exePath, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        // The CLI would otherwise relaunch itself into a mitigated child whose
        // console output cannot flow through our pipes.
        Environment.SetEnvironmentVariable("SHARPEMU_DISABLE_MITIGATION_RELAUNCH", "1");

        var securityAttributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1,
        };

        if (!CreatePipe(out var stdoutRead, out var stdoutWrite, ref securityAttributes, 0) ||
            !CreatePipe(out var stderrRead, out var stderrWrite, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipes.");
        }

        _ = SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);
        _ = SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);

        var startupInfoEx = new STARTUPINFOEX();
        startupInfoEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        startupInfoEx.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startupInfoEx.StartupInfo.hStdOutput = stdoutWrite;
        startupInfoEx.StartupInfo.hStdError = stderrWrite;

        nint attributeList = 0;
        nint mitigationPolicies = 0;
        try
        {
            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal((nint)attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize the process attribute list.");
            }

            startupInfoEx.lpAttributeList = attributeList;

            var policy1 = PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_OFF;
            var policy2 =
                PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF |
                PROCESS_CREATION_MITIGATION_POLICY2_USER_CET_SET_CONTEXT_IP_VALIDATION_ALWAYS_OFF |
                PROCESS_CREATION_MITIGATION_POLICY2_XTENDED_CONTROL_FLOW_GUARD_ALWAYS_OFF;

            mitigationPolicies = Marshal.AllocHGlobal(sizeof(ulong) * 2);
            Marshal.WriteInt64(mitigationPolicies, unchecked((long)policy1));
            Marshal.WriteInt64(nint.Add(mitigationPolicies, sizeof(long)), unchecked((long)policy2));

            if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
                mitigationPolicies,
                (nuint)(sizeof(ulong) * 2),
                0,
                0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to apply the mitigation policy.");
            }

            var currentDirectory = workingDirectory ?? Environment.CurrentDirectory;
            var created = CreateProcessW(
                exePath,
                new StringBuilder(BuildCommandLine(exePath, arguments)),
                0,
                0,
                true,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                0,
                currentDirectory,
                ref startupInfoEx,
                out var processInfo);

            if (!created)
            {
                // Some mitigation policy bits (e.g. XFG) are not supported on
                // older Windows builds. Mirror the CLI's behavior and fall back
                // to launching without the mitigation attribute list.
                startupInfoEx.lpAttributeList = 0;
                created = CreateProcessW(
                    exePath,
                    new StringBuilder(BuildCommandLine(exePath, arguments)),
                    0,
                    0,
                    true,
                    CREATE_NO_WINDOW,
                    0,
                    currentDirectory,
                    ref startupInfoEx,
                    out processInfo);
            }

            if (!created)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Failed to start '{exePath}' (Win32 error {error}: {new Win32Exception(error).Message}).");
            }

            CloseHandle(processInfo.hThread);
            _processHandle = processInfo.hProcess;

            _jobHandle = CreateJobObjectW(0, null);
            if (_jobHandle != 0 &&
                (!TryEnableKillOnJobClose(_jobHandle) || !AssignProcessToJobObject(_jobHandle, processInfo.hProcess)))
            {
                CloseHandle(_jobHandle);
                _jobHandle = 0;
            }

            StartReaderThread(stdoutRead, isError: false);
            StartReaderThread(stderrRead, isError: true);
            StartExitWatcherThread();
        }
        catch
        {
            CloseHandle(stdoutRead);
            CloseHandle(stderrRead);
            throw;
        }
        finally
        {
            // The child owns duplicated pipe write ends; closing ours lets the
            // readers observe EOF when the child exits.
            CloseHandle(stdoutWrite);
            CloseHandle(stderrWrite);

            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (mitigationPolicies != 0)
            {
                Marshal.FreeHGlobal(mitigationPolicies);
            }
        }
    }

    private void StartFallback(string exePath, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data, false);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data, true);
            }
        };
        process.Exited += (_, _) =>
        {
            int exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }

            OnExited(exitCode);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _fallbackProcess = process;
    }

    private void StartReaderThread(nint readHandle, bool isError)
    {
        var thread = new Thread(() =>
        {
            using var stream = new FileStream(new SafeFileHandle(readHandle, ownsHandle: true), FileAccess.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    OutputReceived?.Invoke(line, isError);
                }
            }
            catch (IOException)
            {
                // Pipe broken on process teardown.
            }
        })
        {
            IsBackground = true,
            Name = isError ? "SharpEmu stderr reader" : "SharpEmu stdout reader",
        };
        thread.Start();
    }

    private void StartExitWatcherThread()
    {
        var processHandle = _processHandle;
        var thread = new Thread(() =>
        {
            _ = WaitForSingleObject(processHandle, INFINITE);
            var exitCode = GetExitCodeProcess(processHandle, out var rawExitCode)
                ? unchecked((int)rawExitCode)
                : -1;
            OnExited(exitCode);
        })
        {
            IsBackground = true,
            Name = "SharpEmu exit watcher",
        };
        thread.Start();
    }

    private void OnExited(int exitCode)
    {
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            if (_processHandle != 0)
            {
                CloseHandle(_processHandle);
                _processHandle = 0;
            }

            if (_jobHandle != 0)
            {
                CloseHandle(_jobHandle);
                _jobHandle = 0;
            }

            _fallbackProcess?.Dispose();
            _fallbackProcess = null;
        }

        Exited?.Invoke(exitCode);
    }

    private static bool TryEnableKillOnJobClose(nint jobHandle)
    {
        var extendedLimitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(extendedLimitInfo, memory, false);
            return SetInformationJobObject(
                jobHandle,
                JobObjectExtendedLimitInformation,
                memory,
                unchecked((uint)size));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static string BuildCommandLine(string processPath, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteArgument(processPath));
        for (var i = 0; i < args.Count; i++)
        {
            builder.Append(' ');
            builder.Append(QuoteArgument(args[i]));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = false;
        foreach (var c in argument)
        {
            if (char.IsWhiteSpace(c) || c == '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        var backslashCount = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashCount++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashCount * 2) + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            if (backslashCount > 0)
            {
                builder.Append('\\', backslashCount);
                backslashCount = 0;
            }

            builder.Append(c);
        }

        if (backslashCount > 0)
        {
            builder.Append('\\', backslashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out nint hReadPipe,
        out nint hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nuint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nuint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint lpAttributeList);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint hJob,
        int jobObjectInfoClass,
        nint lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

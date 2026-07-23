// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.GUI;

/// <summary>
/// Owns an isolated emulator process. Guest virtual memory is fixed-address and
/// cannot be reliably reused while guest-created host threads are still alive,
/// so the GUI must never execute a game in its own process.
/// </summary>
internal sealed class EmulatorProcess : IDisposable
{
    public const int HostStopExitCode = -2;

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint Infinite = 0xFFFFFFFF;
    private const int ProcThreadAttributeMitigationPolicy = 0x00020007;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const string MitigatedChildFlag = "--sharpemu-mitigated-child";
    private const string MitigatedChildEnvironment = "SHARPEMU_MITIGATED_CHILD";
    private const ulong ControlFlowGuardAlwaysOff = 0x00000002UL << 40;
    private const ulong CetUserShadowStacksAlwaysOff = 0x00000002UL << 28;
    private const ulong UserCetSetContextIpValidationAlwaysOff = 0x00000002UL << 32;

    private static readonly object EnvironmentGate = new();

    private readonly object _sync = new();
    private nint _processHandle;
    private nint _jobHandle;
    private Process? _fallbackProcess;
    private bool _running;
    private bool _stopRequested;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(arguments);

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_running)
            {
                throw new InvalidOperationException("The emulator process is already running.");
            }

            _stopRequested = false;
        }

        if (OperatingSystem.IsWindows())
        {
            StartWindows(exePath, arguments, workingDirectory);
            return;
        }

        StartFallback(exePath, arguments, workingDirectory);
    }

    public void Stop()
    {
        nint processHandle;
        nint jobHandle;
        Process? fallbackProcess;
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _stopRequested = true;
            processHandle = _processHandle;
            jobHandle = _jobHandle;
            fallbackProcess = _fallbackProcess;
        }

        if (jobHandle != 0)
        {
            _ = TerminateJobObject(jobHandle, unchecked((uint)HostStopExitCode));
            return;
        }

        if (processHandle != 0)
        {
            _ = TerminateProcess(processHandle, unchecked((uint)HostStopExitCode));
            return;
        }

        try
        {
            if (fallbackProcess is { HasExited: false })
            {
                fallbackProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited while Stop was handling the request.
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

    private void StartFallback(string exePath, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
                : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the emulator process.");
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, eventArgs) => ForwardOutput(eventArgs.Data, isError: false);
        process.ErrorDataReceived += (_, eventArgs) => ForwardOutput(eventArgs.Data, isError: true);
        process.Exited += (_, _) => OnExited(process.ExitCode);

        lock (_sync)
        {
            _fallbackProcess = process;
            _running = true;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private unsafe void StartWindows(string exePath, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        nint stdoutRead = 0;
        nint stdoutWrite = 0;
        nint stderrRead = 0;
        nint stderrWrite = 0;
        nint attributeList = 0;
        nint mitigationPolicies = 0;
        nint processHandle = 0;
        nint threadHandle = 0;

        try
        {
            var security = new SecurityAttributes
            {
                Size = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = 1,
            };
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0) ||
                !CreatePipe(out stderrRead, out stderrWrite, ref security, 0))
            {
                throw new InvalidOperationException($"Could not create emulator output pipes (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (!SetHandleInformation(stdoutRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(stderrRead, HandleFlagInherit, 0))
            {
                throw new InvalidOperationException($"Could not configure emulator output pipes (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal((nint)attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new InvalidOperationException($"Could not initialize process mitigation attributes (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            mitigationPolicies = Marshal.AllocHGlobal(sizeof(ulong) * 2);
            Marshal.WriteInt64(mitigationPolicies, unchecked((long)ControlFlowGuardAlwaysOff));
            Marshal.WriteInt64(
                nint.Add(mitigationPolicies, sizeof(long)),
                unchecked((long)(CetUserShadowStacksAlwaysOff | UserCetSetContextIpValidationAlwaysOff)));
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (nint)ProcThreadAttributeMitigationPolicy,
                    mitigationPolicies,
                    (nuint)(sizeof(ulong) * 2),
                    0,
                    0))
            {
                throw new InvalidOperationException($"Could not apply process mitigations (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            var startup = new StartupInfoEx();
            startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.StartupInfo.Flags = StartfUseStdHandles;
            startup.StartupInfo.StdOutput = stdoutWrite;
            startup.StartupInfo.StdError = stderrWrite;
            startup.AttributeList = attributeList;

            var childArguments = new List<string>(arguments.Count + 1) { MitigatedChildFlag };
            childArguments.AddRange(arguments);
            var commandLine = new StringBuilder(BuildCommandLine(exePath, childArguments));
            ProcessInformation processInfo;
            lock (EnvironmentGate)
            {
                var previousValue = Environment.GetEnvironmentVariable(MitigatedChildEnvironment);
                try
                {
                    Environment.SetEnvironmentVariable(MitigatedChildEnvironment, "1");
                    if (!CreateProcessW(
                            null,
                            commandLine,
                            0,
                            0,
                            true,
                            ExtendedStartupInfoPresent | CreateNoWindow,
                            0,
                            string.IsNullOrWhiteSpace(workingDirectory)
                                ? Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
                                : workingDirectory,
                            ref startup,
                            out processInfo))
                    {
                        throw new InvalidOperationException($"Could not start the emulator process (Win32 error {Marshal.GetLastWin32Error()}).");
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable(MitigatedChildEnvironment, previousValue);
                }
            }

            processHandle = processInfo.Process;
            threadHandle = processInfo.Thread;
            CloseHandle(stdoutWrite);
            stdoutWrite = 0;
            CloseHandle(stderrWrite);
            stderrWrite = 0;

            var jobHandle = CreateJobObjectW(0, null);
            if (jobHandle != 0 &&
                (!TryEnableKillOnJobClose(jobHandle) || !AssignProcessToJobObject(jobHandle, processHandle)))
            {
                CloseHandle(jobHandle);
                jobHandle = 0;
            }

            lock (_sync)
            {
                _processHandle = processHandle;
                _jobHandle = jobHandle;
                _running = true;
            }
            processHandle = 0;

            StartPipeReader(stdoutRead, isError: false);
            stdoutRead = 0;
            StartPipeReader(stderrRead, isError: true);
            stderrRead = 0;
            StartWindowsExitWatcher(_processHandle);
        }
        catch
        {
            if (processHandle != 0)
            {
                _ = TerminateProcess(processHandle, 1);
            }

            throw;
        }
        finally
        {
            if (threadHandle != 0)
            {
                CloseHandle(threadHandle);
            }
            if (processHandle != 0)
            {
                CloseHandle(processHandle);
            }
            if (stdoutRead != 0)
            {
                CloseHandle(stdoutRead);
            }
            if (stdoutWrite != 0)
            {
                CloseHandle(stdoutWrite);
            }
            if (stderrRead != 0)
            {
                CloseHandle(stderrRead);
            }
            if (stderrWrite != 0)
            {
                CloseHandle(stderrWrite);
            }
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

    private void StartPipeReader(nint handle, bool isError)
    {
        var readerThread = new Thread(() =>
        {
            using var safeHandle = new SafeFileHandle(handle, ownsHandle: true);
            using var stream = new FileStream(safeHandle, FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                ForwardOutput(line, isError);
            }
        }, 256 * 1024)
        {
            IsBackground = true,
            Name = isError ? "SharpEmu stderr reader" : "SharpEmu stdout reader",
        };
        readerThread.Start();
    }

    private void StartWindowsExitWatcher(nint processHandle)
    {
        var watcher = new Thread(() =>
        {
            _ = WaitForSingleObject(processHandle, Infinite);
            var exitCode = 1;
            if (GetExitCodeProcess(processHandle, out var nativeExitCode))
            {
                exitCode = unchecked((int)nativeExitCode);
            }

            OnExited(exitCode);
        }, 128 * 1024)
        {
            IsBackground = true,
            Name = "SharpEmu exit watcher",
        };
        watcher.Start();
    }

    private void ForwardOutput(string? line, bool isError)
    {
        if (line is not null)
        {
            OutputReceived?.Invoke(line, isError);
        }
    }

    private void OnExited(int nativeExitCode)
    {
        int exitCode;
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            exitCode = _stopRequested ? HostStopExitCode : nativeExitCode;
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
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            return SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformationClass, memory, unchecked((uint)size));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static string BuildCommandLine(string processPath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(QuoteArgument(processPath));
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(static c => char.IsWhiteSpace(c) || c == '"'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var slashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (slashCount * 2) + 1);
                builder.Append(character);
                slashCount = 0;
                continue;
            }

            if (slashCount > 0)
            {
                builder.Append('\\', slashCount);
                slashCount = 0;
            }

            builder.Append(character);
        }

        if (slashCount > 0)
        {
            builder.Append('\\', slashCount * 2);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EmulatorProcess));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out nint readPipe, out nint writePipe, ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nuint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

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
    private static extern bool TerminateJobObject(nint job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

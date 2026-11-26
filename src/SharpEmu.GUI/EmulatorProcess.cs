// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.GUI;

/// <summary>
/// Owns an isolated emulator process. Guest virtual memory is fixed-address and
/// cannot be reliably reused while guest-created host threads are still alive,
/// so the GUI must never execute a game in its own process.
/// </summary>
internal sealed class EmulatorProcess : IDisposable
{
    public const int HostStopExitCode = -2;

    internal static bool IsWindowReadyMessage(string line) =>
        line.Contains("[LOADER][INFO]", StringComparison.Ordinal) &&
        line.Contains(" window ready:", StringComparison.Ordinal);

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint Infinite = 0xFFFFFFFF;
    private const int ProcThreadAttributeMitigationPolicy = 0x00020007;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const string MitigatedChildFlag = "--sharpemu-mitigated-child";
    private const string MitigatedChildEnvironment = "SHARPEMU_MITIGATED_CHILD";

    private static readonly object EnvironmentGate = new();

    private readonly object _sync = new();
    private nint _processHandle;
    private nint _jobHandle;
    private IDisposable? _gamepadBridge;
    // Holds the handles the sandboxed child inherited (its game archive) until it exits.
    private IDisposable? _sandbox;
    private SandboxJobMonitor? _sandboxMonitor;
    private Process? _fallbackProcess;
    private bool _running;
    private bool _stopRequested;
    private bool _disposed;

    public event Action<string, bool>? OutputReceived;

    public event Action<int>? Exited;

    /// <summary>Raised once per run, when the sandbox refused the game's first attempt to start another process.</summary>
    public event Action? SandboxActionBlocked;

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

    /// <summary>
    /// The launcher's choice for the next start: the "Run games in a sandbox" setting, or false for a one-off launch
    /// without it. An explicit SHARPEMU_SANDBOX=0/1 in the environment overrides it (<see cref="SandboxLaunchPolicy"/>).
    /// </summary>
    public bool SandboxEnabled { get; set; } = SandboxLaunchPolicy.GuiDefault;

    public void Start(string exePath, IReadOnlyList<string> arguments, string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
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

        // CreateProcess and Process.Start both inherit the calling process's environment.
        // Scope the game's values to process creation and restore them even when startup fails.
        // The Windows mitigation path takes the same (reentrant) gate.
        lock (EnvironmentGate)
        {
            var previous = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                previous[(string)entry.Key] = (string?)entry.Value;
            IReadOnlyList<string> loaderVariables = [];
            try
            {
                foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
                    Environment.SetEnvironmentVariable(name, value);
                // SHARPEMU_VK_DISABLE_IMPLICITS also needs the Vulkan loader configured in the
                // child's environment; undo those variables along with the game's own.
                if (SharpEmu.Libs.VideoOut.RenderDocCapture.ApplyVulkanLoaderEnvironment())
                    loaderVariables = ["VK_LOADER_DEBUG", "VK_LOADER_LAYERS_DISABLE", "VK_LOADER_LAYERS_ALLOW"];
                if (OperatingSystem.IsWindows()) StartWindows(exePath, arguments, workingDirectory);
                else StartFallback(exePath, arguments, workingDirectory);
            }
            finally
            {
                foreach (var name in (environment?.Keys ?? []).Concat(loaderVariables).Distinct(StringComparer.OrdinalIgnoreCase))
                    Environment.SetEnvironmentVariable(name, previous.GetValueOrDefault(name));
            }
        }
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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
        nint sandboxJob = 0;
        WindowsAppContainerSandbox? sandbox = null;
        SandboxJobMonitor? sandboxMonitor = null;
        SharpEmu.Libs.Pad.GamepadBridgeHost? gamepadBridge = null;

        try
        {
            // The child runs in an AppContainer limited to the folders of this launch when the GUI setting is on
            // (default) or SHARPEMU_SANDBOX=1; SHARPEMU_SANDBOX=0 forces it off. Failing to set that up fails the
            // launch: it never falls back to an unconfined child.
            if (SandboxLaunchPolicy.ShouldSandbox(SandboxEnabled, Environment.GetEnvironmentVariable(SandboxLaunchPolicy.Variable)))
            {
                sandbox = WindowsAppContainerSandbox.Create(new SandboxInputs(
                    Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
                    SharpEmu.Libs.SharpEmuPaths.DataRoot,
                    arguments.LastOrDefault(static argument => !argument.StartsWith("--", StringComparison.Ordinal)),
                    null,
                    arguments,
                    Environment.GetEnvironmentVariable));
            }

            // The sandboxed child's pipe ends are not inheritable until the CreateProcessW call below (InheritListedHandles).
            var security = new SecurityAttributes
            {
                Size = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = sandbox is null ? 1 : 0,
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

            var attributeCount = sandbox is null ? 1 : WindowsAppContainerSandbox.AttributeCount;
            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, attributeCount, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal((nint)attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref attributeListSize))
            {
                throw new InvalidOperationException($"Could not initialize process mitigation attributes (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            mitigationPolicies = Marshal.AllocHGlobal(sizeof(ulong) * 2);
            Marshal.WriteInt64(mitigationPolicies, unchecked((long)GuestProcessMitigationPolicy.Primary));
            Marshal.WriteInt64(
                nint.Add(mitigationPolicies, sizeof(long)),
                unchecked((long)GuestProcessMitigationPolicy.Secondary));
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

            if (sandbox is not null)
            {
                sandbox.AddSecurityCapabilities(attributeList);
                // The AppContainer child sees no controller: this process reads it and shares it (see GamepadBridgeHost).
                // The launcher's own menu polling stops meanwhile, so one thread uses SDL gamepads.
                gamepadBridge = SharpEmu.Libs.Pad.GamepadBridgeHost.TryStart();
                if (gamepadBridge is null)
                {
                    sandbox.AddHandleList(attributeList, stdoutWrite, stderrWrite);
                }
                else
                {
                    sandbox.AddHandleList(attributeList, stdoutWrite, stderrWrite, gamepadBridge.Handle);
                }
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
                IDisposable? inheritScope = null;
                try
                {
                    Environment.SetEnvironmentVariable(MitigatedChildEnvironment, "1");
                    inheritScope = sandbox?.InheritListedHandles();
                    if (!CreateProcessW(
                            null,
                            commandLine,
                            0,
                            0,
                            true,
                            ExtendedStartupInfoPresent | CreateNoWindow | WindowsGuestAddressReservation.CreateSuspended |
                                (sandbox is null ? 0 : CreateUnicodeEnvironment),
                            sandbox?.CreateEnvironmentBlock(gamepadBridge is null ? null : [gamepadBridge.ChildEnvironment]) ?? 0,
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
                    inheritScope?.Dispose();
                    Environment.SetEnvironmentVariable(MitigatedChildEnvironment, previousValue);
                }
            }

            processHandle = processInfo.Process;
            threadHandle = processInfo.Thread;
            // The sandboxed child joins its one-process job while still suspended, so no guest code runs outside it.
            if (sandbox is not null)
            {
                sandboxJob = sandbox.CreateConfinedJob(processHandle, out sandboxMonitor);
                if (sandboxMonitor is not null)
                {
                    sandboxMonitor.ProcessLaunchBlocked += count =>
                    {
                        if (count == 1)
                        {
                            // A PS5 game cannot start a Windows program: the attempt means a modified dump. Stop it.
                            Stop();
                            SandboxActionBlocked?.Invoke();
                        }
                    };
                }
            }

            WindowsGuestAddressReservation.PrepareAndResume(processHandle, threadHandle);
            CloseHandle(stdoutWrite);
            stdoutWrite = 0;
            CloseHandle(stderrWrite);
            stderrWrite = 0;

            var jobHandle = sandboxJob != 0 ? sandboxJob : CreateJobObjectW(0, null);
            if (sandboxJob == 0 && jobHandle != 0 &&
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
            sandboxJob = 0;
            // The bridge lives as long as the child: it is disposed when the child exits (OnExited) or is stopped.
            lock (_sync)
            {
                _gamepadBridge = gamepadBridge;
                _sandbox = sandbox;
                _sandboxMonitor = sandboxMonitor;
            }

            gamepadBridge = null;
            sandbox = null;
            sandboxMonitor = null;

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
            sandboxMonitor?.Dispose();
            gamepadBridge?.Dispose();
            sandbox?.Dispose();
            if (sandboxJob != 0)
            {
                CloseHandle(sandboxJob);
            }
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
        if (!string.IsNullOrEmpty(line))
        {
            OutputReceived?.Invoke(line, isError);
        }
    }

    private void OnExited(int nativeExitCode)
    {
        int exitCode;
        IDisposable? bridge;
        IDisposable? sandboxMonitor;
        IDisposable? sandbox;
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
            bridge = _gamepadBridge;
            _gamepadBridge = null;
            sandbox = _sandbox;
            _sandbox = null;
            sandboxMonitor = _sandboxMonitor;
            _sandboxMonitor = null;
        }

        bridge?.Dispose();
        sandboxMonitor?.Dispose();
        sandbox?.Dispose();
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

    internal static string QuoteArgument(string value)
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

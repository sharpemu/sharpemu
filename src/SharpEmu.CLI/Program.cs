// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Runtime;
using SharpEmu.Core.Cpu;
using SharpEmu.GUI;
using SharpEmu.HLE;
using SharpEmu.Logging;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.CLI;

internal static partial class Program
{
    private static readonly SharpEmuLogger Log = SharpEmuLog.For("SharpEmu.CLI");
    private const int DefaultImportTraceLimit = 32;
    private const string MitigatedChildFlag = "--sharpemu-mitigated-child";
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint INFINITE = 0xFFFFFFFF;
    private const int PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY = 0x00020007;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY_CONTROL_FLOW_GUARD_ALWAYS_OFF = 0x00000002UL << 40;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF = 0x00000002UL << 28;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_USER_CET_SET_CONTEXT_IP_VALIDATION_ALWAYS_OFF = 0x00000002UL << 32;
    private const ulong PROCESS_CREATION_MITIGATION_POLICY2_XTENDED_CONTROL_FLOW_GUARD_ALWAYS_OFF = 0x00000002UL << 40;
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        args = NormalizeInternalArguments(args, out var isMitigatedChild);
        if (args.Length == 0 && !isMitigatedChild)
        {
            // No arguments: open the desktop frontend. Any argument selects
            // the classic CLI behavior below.
            return GuiLauncher.Run();
        }

        // The executable uses the GUI subsystem, so CLI mode has to connect
        // itself to a console before the first write.
        EnsureCliConsole();

        Console.Error.WriteLine($"[DEBUG] SharpEmu starting with {args.Length} args");

        if (!isMitigatedChild && TryRunMitigatedChild(args, out var childExitCode))
        {
            return childExitCode;
        }

        if (!TryParseArguments(args, out var ebootPath, out var runtimeOptions, out var logLevel))
        {
            PrintUsage();
            return 1;
        }

        SharpEmuLog.MinimumLevel = logLevel;

        ebootPath = Path.GetFullPath(ebootPath);
        Console.Error.WriteLine($"[DEBUG] Full path: {ebootPath}");
        
        if (!File.Exists(ebootPath))
        {
            Log.Error($"EBOOT file was not found: {ebootPath}");
            return 2;
        }

        Console.Error.WriteLine("[DEBUG] Creating runtime...");

        using var runtime = SharpEmuRuntime.CreateDefault(runtimeOptions);

        OrbisGen2Result result;
        try
        {
            Console.Error.WriteLine($"[DEBUG] Running: {ebootPath}");
            result = runtime.Run(ebootPath);
            Console.Error.WriteLine($"[DEBUG] Result: {result}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DEBUG] Exception: {ex}");
            Log.Error("SharpEmu failed to run.", ex);
            return 3;
        }

        Log.Info($"SharpEmu execution completed. Result={result} (0x{(int)result:X8})");
        if (!string.IsNullOrWhiteSpace(runtime.LastSessionSummary))
        {
            Log.Info(runtime.LastSessionSummary);
        }

        if (!string.IsNullOrWhiteSpace(runtime.LastBasicBlockTrace))
        {
            Log.Info("BB trace:");
            Log.Info(runtime.LastBasicBlockTrace);
        }

        if (!string.IsNullOrWhiteSpace(runtime.LastMilestoneLog))
        {
            Log.Info(runtime.LastMilestoneLog);
        }

        if (result != OrbisGen2Result.ORBIS_GEN2_OK && !string.IsNullOrWhiteSpace(runtime.LastExecutionDiagnostics))
        {
            Log.Warn(runtime.LastExecutionDiagnostics);
        }

        if (runtimeOptions.ImportTraceLimit > 0 && !string.IsNullOrWhiteSpace(runtime.LastExecutionTrace))
        {
            Log.Info("Import trace:");
            Log.Info(runtime.LastExecutionTrace);
        }

        return result == OrbisGen2Result.ORBIS_GEN2_OK ? 0 : 4;
    }

    private static void EnsureCliConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Standard handles already provided (pipes or file redirection, e.g.
        // when the GUI or a script launches us): use them as-is.
        if (IsHandleValid(GetStdHandle(STD_OUTPUT_HANDLE)) && IsHandleValid(GetStdHandle(STD_ERROR_HANDLE)))
        {
            return;
        }

        // Prefer the console of the parent process (interactive terminal);
        // create one only when started with arguments but no terminal at all
        // (e.g. a shortcut), so usage and errors remain visible.
        if (!AttachConsole(ATTACH_PARENT_PROCESS) && GetConsoleWindow() == 0)
        {
            _ = AllocConsole();
        }

        RebindStdHandleToConsole(STD_OUTPUT_HANDLE);
        RebindStdHandleToConsole(STD_ERROR_HANDLE);
    }

    private static void RebindStdHandleToConsole(int stdHandle)
    {
        if (IsHandleValid(GetStdHandle(stdHandle)) || GetConsoleWindow() == 0)
        {
            return;
        }

        var conOut = CreateFileW(
            "CONOUT$",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            0,
            OPEN_EXISTING,
            0,
            0);
        if (IsHandleValid(conOut))
        {
            _ = SetStdHandle(stdHandle, conOut);
        }
    }

    private static bool IsHandleValid(nint handle)
    {
        return handle != 0 && handle != -1;
    }

    private static string[] NormalizeInternalArguments(string[] args, out bool isMitigatedChild)
    {
        isMitigatedChild = false;
        if (args.Length == 0)
        {
            return args;
        }

        var list = new List<string>(args.Length);
        foreach (var arg in args)
        {
            if (string.Equals(arg, MitigatedChildFlag, StringComparison.Ordinal))
            {
                isMitigatedChild = true;
                continue;
            }

            list.Add(arg);
        }

        return list.ToArray();
    }

    private static bool TryRunMitigatedChild(string[] args, out int childExitCode)
    {
        childExitCode = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_MITIGATION_RELAUNCH"), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var childArgs = new string[args.Length + 1];
        childArgs[0] = MitigatedChildFlag;
        for (var i = 0; i < args.Length; i++)
        {
            childArgs[i + 1] = args[i];
        }

        var commandLine = BuildCommandLine(processPath, childArgs);
        var startupInfoEx = new STARTUPINFOEX();
        startupInfoEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        nint attributeList = 0;
        nint mitigationPolicies = 0;
        try
        {
            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal((nint)attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                return false;
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
                (nint)PROC_THREAD_ATTRIBUTE_MITIGATION_POLICY,
                mitigationPolicies,
                (nuint)(sizeof(ulong) * 2),
                0,
                0))
            {
                return false;
            }

            var cmdLineBuilder = new StringBuilder(commandLine);
            nint jobHandle = 0;
            if (!CreateProcessW(
                processPath,
                cmdLineBuilder,
                0,
                0,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                0,
                Environment.CurrentDirectory,
                ref startupInfoEx,
                out var processInfo))
            {
                return false;
            }

            try
            {
                jobHandle = CreateJobObjectW(0, null);
                if (jobHandle != 0 &&
                    TryEnableKillOnJobClose(jobHandle) &&
                    !AssignProcessToJobObject(jobHandle, processInfo.hProcess))
                {
                    CloseHandle(jobHandle);
                    jobHandle = 0;
                }

                ConsoleCancelEventHandler? cancelHandler = null;
                EventHandler? processExitHandler = null;
                cancelHandler = (_, eventArgs) =>
                {
                    _ = TerminateProcess(processInfo.hProcess, 1);
                    eventArgs.Cancel = true;
                };
                processExitHandler = (_, _) =>
                {
                    _ = TerminateProcess(processInfo.hProcess, 1);
                };
                Console.CancelKeyPress += cancelHandler;
                AppDomain.CurrentDomain.ProcessExit += processExitHandler;

                _ = WaitForSingleObject(processInfo.hProcess, INFINITE);
                Console.CancelKeyPress -= cancelHandler;
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;

                if (!GetExitCodeProcess(processInfo.hProcess, out var exitCode))
                {
                    return false;
                }

                childExitCode = unchecked((int)exitCode);
                Console.Error.WriteLine("[DEBUG] Running in mitigated child process (CET/CFG disabled).");
                return true;
            }
            finally
            {
                if (jobHandle != 0)
                {
                    CloseHandle(jobHandle);
                }

                CloseHandle(processInfo.hThread);
                CloseHandle(processInfo.hProcess);
            }
        }
        finally
        {
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

    private static void PrintUsage()
    {
        Log.Info("Usage: SharpEmu.CLI [--strict] [--trace-imports[=N]] [--cpu-engine=<native>] [--log-level=<level>] <path-to-eboot.bin>");
        Log.Info(@"Example: SharpEmu.CLI --cpu-engine=native --trace-imports=64 --log-level=debug ""E:\Games\...\eboot.bin""");
    }

    private static bool TryParseArguments(
        string[] args,
        out string ebootPath,
        out SharpEmuRuntimeOptions runtimeOptions,
        out LogLevel logLevel)
    {
        if (args.Length == 0)
        {
            ebootPath = string.Empty;
            runtimeOptions = default;
            logLevel = SharpEmuLog.MinimumLevel;
            return false;
        }

        var strictDynlibResolution = false;
        var importTraceLimit = 0;
        var cpuEngine = CpuExecutionEngine.NativeOnly;
        logLevel = SharpEmuLog.MinimumLevel;
        var pathTokens = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, "--strict", StringComparison.OrdinalIgnoreCase))
            {
                strictDynlibResolution = true;
                continue;
            }

            if (string.Equals(argument, "--trace-imports", StringComparison.OrdinalIgnoreCase))
            {
                importTraceLimit = DefaultImportTraceLimit;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var explicitLimit))
                {
                    importTraceLimit = Math.Max(0, explicitLimit);
                    i++;
                }

                continue;
            }

            if (string.Equals(argument, "--log-level", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !SharpEmuLog.TryParseLevel(args[i + 1], out logLevel))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    return false;
                }

                i++;
                continue;
            }

            if (string.Equals(argument, "--cpu-engine", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || !TryParseCpuEngine(args[i + 1], out cpuEngine))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    return false;
                }

                i++;
                continue;
            }

            const string logLevelPrefix = "--log-level=";
            if (argument.StartsWith(logLevelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var valueText = argument[logLevelPrefix.Length..];
                if (!SharpEmuLog.TryParseLevel(valueText, out logLevel))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    return false;
                }

                continue;
            }

            const string cpuEnginePrefix = "--cpu-engine=";
            if (argument.StartsWith(cpuEnginePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var valueText = argument[cpuEnginePrefix.Length..];
                if (!TryParseCpuEngine(valueText, out cpuEngine))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    return false;
                }

                continue;
            }

            const string tracePrefix = "--trace-imports=";
            if (argument.StartsWith(tracePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var valueText = argument[tracePrefix.Length..];
                if (!int.TryParse(valueText, out importTraceLimit))
                {
                    ebootPath = string.Empty;
                    runtimeOptions = default;
                    logLevel = SharpEmuLog.MinimumLevel;
                    return false;
                }

                importTraceLimit = Math.Max(0, importTraceLimit);
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                ebootPath = string.Empty;
                runtimeOptions = default;
                logLevel = SharpEmuLog.MinimumLevel;
                return false;
            }

            pathTokens.Add(argument);
        }

        if (pathTokens.Count == 0)
        {
            ebootPath = string.Empty;
            runtimeOptions = default;
            logLevel = SharpEmuLog.MinimumLevel;
            return false;
        }

        ebootPath = string.Join(' ', pathTokens);
        runtimeOptions = new SharpEmuRuntimeOptions
        {
            CpuEngine = cpuEngine,
            StrictDynlibResolution = strictDynlibResolution,
            ImportTraceLimit = importTraceLimit,
        };
        return true;
    }

    private static bool TryParseCpuEngine(string valueText, out CpuExecutionEngine engine)
    {
        if (string.Equals(valueText, "native", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(valueText, "native-only", StringComparison.OrdinalIgnoreCase))
        {
            engine = CpuExecutionEngine.NativeOnly;
            return true;
        }

        engine = CpuExecutionEngine.NativeOnly;
        return false;
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

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int stdHandle, nint handle);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Runtime.InteropServices;

namespace SharpEmu.CLI;

/// <summary>
/// Under Rosetta 2 every garbage collection suspends the runtime with
/// FlushProcessWriteBuffers, which reads each thread's registers through a Mach call that
/// Rosetta services by parking the thread in its runtime. A signal that reaches a parked
/// thread (guest memory tracking raises thousands per second) trips Rosetta's "expected saved
/// LR to be in translated code" assertion and wedges the process. With the default gen0 budget
/// the emulator collects about twice a second; a 64 MiB budget makes that rare.
/// </summary>
/// <remarks>
/// The GC reads its gen0 budget only from the environment when the runtime starts, so the
/// process re-executes itself once with the variable set. execve keeps the process id.
/// </remarks>
internal static class RosettaGcBudget
{
    private const string BudgetVariable = "DOTNET_GCgen0size";
    // 64 MiB: a larger budget turns every collection into a blocking gen1/gen2 pass of
    // about a second (512 MiB measured 8 such pauses in 150 s of Demon's Souls).
    private const string Budget = "0x4000000";
    private const string OptOutVariable = "SHARPEMU_ROSETTA_GC_BUDGET";

    public static void ReexecIfNeeded(string[] args)
    {
        if (!OperatingSystem.IsMacOS() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            Environment.GetEnvironmentVariable(BudgetVariable) is not null ||
            Environment.GetEnvironmentVariable(OptOutVariable) == "0" ||
            !IsTranslated())
        {
            return;
        }

        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var environment = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment.Add($"{entry.Key}={entry.Value}");
        }
        environment.Add($"{BudgetVariable}={Budget}");

        var argv = BuildArguments(path, Environment.GetCommandLineArgs()[0], args);
        var nativeArguments = ToNullTerminated(argv);
        var nativeEnvironment = ToNullTerminated(environment);
        // Only returns on failure; the emulator then runs with the default budget.
        _ = Execve(path, nativeArguments, nativeEnvironment);
        var error = Marshal.GetLastPInvokeError();
        foreach (var pointer in nativeArguments) Marshal.FreeCoTaskMem(pointer);
        foreach (var pointer in nativeEnvironment) Marshal.FreeCoTaskMem(pointer);
        Console.Error.WriteLine(
            $"[LOADER][WARN] Could not restart with a larger GC budget under Rosetta: errno={error}");
    }

    internal static string[] BuildArguments(string executable, string entryAssembly, string[] args) =>
        Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [executable, entryAssembly, .. args]
            : [executable, .. args];

    private static bool IsTranslated()
    {
        var value = 0;
        var size = (nint)sizeof(int);
        return SysctlByName("sysctl.proc_translated", ref value, ref size, 0, 0) == 0 && value == 1;
    }

    private static nint[] ToNullTerminated(IReadOnlyList<string> values)
    {
        var pointers = new nint[values.Count + 1];
        for (var i = 0; i < values.Count; i++)
        {
            pointers[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
        }
        return pointers;
    }

    [DllImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static extern int Execve(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        nint[] argv,
        nint[] envp);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        ref int value,
        ref nint size,
        nint newValue,
        nint newSize);
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Core.Diagnostics;

public static partial class WindowsCrashCapture
{
    internal static unsafe bool IsFirstNullExecuteFault(DebugEvent debugEvent) =>
        debugEvent.Kind == ExceptionEvent && debugEvent.FirstChance != 0 &&
        debugEvent.Exception.Code == 0xC0000005 && debugEvent.Exception.ParameterCount >= 2 &&
        debugEvent.Exception.Parameters[0] == 8 && debugEvent.Exception.Parameters[1] == 0;

    internal static unsafe bool ShouldTraceFirstChance(DebugEvent debugEvent) =>
        debugEvent.Kind == ExceptionEvent && debugEvent.FirstChance != 0 &&
        (debugEvent.Exception.Code == 0xC0000409 ||
         (debugEvent.Exception.Code == 0xC0000005 && debugEvent.Exception.ParameterCount >= 2 &&
          (debugEvent.Exception.Parameters[0] == 8 || (ulong)debugEvent.Exception.Address >= 0x0000700000000000)));

    private static unsafe void TraceNativeFault(uint processId, DebugEvent debugEvent, TextWriter report)
    {
        // Preserve the original context before VEH/CLR unwinding can obscure it.
        // Diagnostic failures must never change the target's exception disposition.
        try
        {
            using var thread = OpenThread(ThreadContextAccess, false, debugEvent.ThreadId);
            using var process = OpenProcess(ProcessReadAccess, false, processId);
            var context = (ThreadContext*)NativeMemory.AlignedAlloc((nuint)sizeof(ThreadContext), 16);
            if (context == null) return;
            try
            {
                NativeMemory.Clear(context, (nuint)sizeof(ThreadContext));
                context->Flags = AllX64ContextFlags;
                if (thread.IsInvalid || !GetThreadContext(thread, context)) return;
                var words = (ulong*)context;
                report.WriteLine($"First-chance native fault: thread={debugEvent.ThreadId} " +
                    $"code=0x{debugEvent.Exception.Code:X8} rip=0x{words[31]:X16} rsp=0x{words[19]:X16} " +
                    $"parameters={debugEvent.Exception.ParameterCount} " +
                    $"p0=0x{debugEvent.Exception.Parameters[0]:X16} p1=0x{debugEvent.Exception.Parameters[1]:X16}");
                report.WriteLine($"Registers: rax=0x{words[15]:X16} rcx=0x{words[16]:X16} " +
                    $"rdx=0x{words[17]:X16} rbx=0x{words[18]:X16} rbp=0x{words[20]:X16} " +
                    $"rsi=0x{words[21]:X16} rdi=0x{words[22]:X16}");
                var stack = stackalloc ulong[32];
                if (!process.IsInvalid && ReadProcessMemory(process, (nint)words[19], stack, 32 * sizeof(ulong), out var read))
                    for (var i = 0; i < (int)(read / sizeof(ulong)); i++)
                        report.WriteLine($"Stack 0x{words[19] + (ulong)i * sizeof(ulong):X16}: 0x{stack[i]:X16}");
            }
            finally { NativeMemory.AlignedFree(context); }
        }
        catch (Exception exception) { report.WriteLine($"Fault trace unavailable: {exception.Message}"); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadProcessMemory(SafeProcessHandle process, nint address,
        void* buffer, nuint size, out nuint bytesRead);
}

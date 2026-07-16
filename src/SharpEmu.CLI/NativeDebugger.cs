// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.CLI;

/// <summary>
/// Optional Win32 debug-event loop for the mitigated child process, enabled by
/// setting SHARPEMU_NATIVE_DEBUGGER=1.
///
/// TryRunMitigatedChild normally just does a WaitForSingleObject on the child
/// and reports whatever GetExitCodeProcess returns. That misses crashes that
/// terminate the process without going through SharpEmu.Core's own vectored
/// exception handler: a __fastfail (stack-cookie / fast-fail security check)
/// from directly-executed guest code bypasses SEH/VEH entirely and is only
/// ever reported to an attached debugger before the process dies. Launching
/// the child with DEBUG_ONLY_THIS_PROCESS gives us that channel.
///
/// This is a pass-through observer, not an interactive debugger: every
/// exception is logged (code, faulting address, and register state) and then
/// continued with DBG_EXCEPTION_NOT_HANDLED, so SharpEmu.Core's own recovery
/// paths (lazy commit, guest allocator holes, the MSVC C++ exception
/// passthrough, etc.) still run exactly as they would with no debugger
/// attached. Windows-only, matching the CET/CFG mitigation this replaces the
/// wait for.
/// </summary>
internal static partial class Program
{
    private const string NativeDebuggerEnvironmentVariable = "SHARPEMU_NATIVE_DEBUGGER";

    private const uint DEBUG_ONLY_THIS_PROCESS = 0x00000002;
    private const uint DBG_CONTINUE = 0x00010002;
    private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
    private const uint INFINITE_DEBUG_WAIT = 0xFFFFFFFF;

    private const uint EXCEPTION_DEBUG_EVENT = 1;
    private const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    private const uint EXIT_PROCESS_DEBUG_EVENT = 5;
    private const uint OUTPUT_DEBUG_STRING_EVENT = 8;
    private const uint EXCEPTION_BREAKPOINT = 0x80000003;

    // Mirrors the x64 CONTEXT offsets SharpEmu.Core's own vectored exception
    // handler already uses (DirectExecutionBackend.cs); duplicated here rather
    // than shared across the assembly boundary for a handful of constants.
    private const int CTX_CONTEXTFLAGS = 48;
    private const int CTX_RAX = 120;
    private const int CTX_RCX = 128;
    private const int CTX_RDX = 136;
    private const int CTX_RBX = 144;
    private const int CTX_RSP = 152;
    private const int CTX_RBP = 160;
    private const int CTX_RSI = 168;
    private const int CTX_RDI = 176;
    private const int CTX_R8 = 184;
    private const int CTX_R9 = 192;
    private const int CTX_R10 = 200;
    private const int CTX_R11 = 208;
    private const int CTX_R12 = 216;
    private const int CTX_R13 = 224;
    private const int CTX_R14 = 232;
    private const int CTX_R15 = 240;
    private const int CTX_RIP = 248;
    private const int CONTEXT_SIZE_AMD64 = 1232;
    private const uint CONTEXT_FULL_AMD64 = 0x10000B; // CONTROL | INTEGER | FLOATING_POINT

    private static bool IsNativeDebuggerEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(NativeDebuggerEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Replaces WaitForSingleObject+GetExitCodeProcess when the child was
    /// created with DEBUG_ONLY_THIS_PROCESS. Returns false on the same terms
    /// TryRunMitigatedChild's original wait did: a failure here means "don't
    /// trust this relaunch", so the caller falls through to running in-process
    /// instead of reporting a bogus exit code.
    /// </summary>
    private static bool RunNativeDebugLoop(uint processId, nint fallbackThreadHandle, out int childExitCode)
    {
        childExitCode = 0;
        var mainThreadHandle = fallbackThreadHandle;
        var sawInitialBreakpoint = false;

        Console.Error.WriteLine($"[DEBUG][NATIVEDBG] Native debugger attached (pid={processId}).");

        while (true)
        {
            if (!WaitForDebugEvent(out var debugEvent, INFINITE_DEBUG_WAIT))
            {
                Console.Error.WriteLine($"[DEBUG][NATIVEDBG] WaitForDebugEvent failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            var continueStatus = DBG_CONTINUE;

            switch (debugEvent.DebugEventCode)
            {
                case CREATE_PROCESS_DEBUG_EVENT:
                    // The debugger gets its own duplicated process/thread
                    // handles here; the system closes them for us when
                    // EXIT_PROCESS_DEBUG_EVENT fires, except hFile, which is
                    // ours to close (it's a handle to the eboot image and
                    // would otherwise stay locked for the child's lifetime).
                    mainThreadHandle = debugEvent.CreateProcessThreadHandle;
                    if (debugEvent.CreateProcessFileHandle != 0)
                    {
                        CloseHandle(debugEvent.CreateProcessFileHandle);
                    }

                    break;

                case EXCEPTION_DEBUG_EVENT:
                    if (!sawInitialBreakpoint && debugEvent.ExceptionCode == EXCEPTION_BREAKPOINT)
                    {
                        // DEBUG_ONLY_THIS_PROCESS always raises exactly one of
                        // these right after CREATE_PROCESS_DEBUG_EVENT. It is
                        // not a guest fault; continue it as-is.
                        sawInitialBreakpoint = true;
                        break;
                    }

                    LogNativeDebugException(debugEvent, mainThreadHandle);

                    // Hand the exception back to normal SEH/VEH dispatch
                    // inside the child so SharpEmu.Core's own handler still
                    // runs unchanged; we are only observing.
                    continueStatus = DBG_EXCEPTION_NOT_HANDLED;
                    break;

                case OUTPUT_DEBUG_STRING_EVENT:
                    if (debugEvent.DebugStringLength > 0)
                    {
                        Console.Error.WriteLine(
                            $"[DEBUG][NATIVEDBG] OutputDebugString received ({debugEvent.DebugStringLength} chars, not decoded).");
                    }

                    break;

                case EXIT_PROCESS_DEBUG_EVENT:
                    childExitCode = unchecked((int)debugEvent.ExitProcessCode);
                    Console.Error.WriteLine(
                        $"[DEBUG][NATIVEDBG] Child exited: 0x{debugEvent.ExitProcessCode:X8} ({DescribeNativeExitCode(debugEvent.ExitProcessCode)})");
                    ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, continueStatus);
                    return true;
            }

            if (!ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, continueStatus))
            {
                Console.Error.WriteLine($"[DEBUG][NATIVEDBG] ContinueDebugEvent failed: {Marshal.GetLastWin32Error()}");
                return false;
            }
        }
    }

    private static unsafe void LogNativeDebugException(in DEBUG_EVENT debugEvent, nint threadHandle)
    {
        var isSecondChance = debugEvent.FirstChance == 0;
        Console.Error.WriteLine("[DEBUG][NATIVEDBG] ----------------------------------------");
        Console.Error.WriteLine(
            $"[DEBUG][NATIVEDBG] {(isSecondChance ? "Second-chance" : "First-chance")} exception 0x{debugEvent.ExceptionCode:X8} ({DescribeNativeExceptionCode(debugEvent.ExceptionCode)})");
        Console.Error.WriteLine($"[DEBUG][NATIVEDBG]   Exception address: 0x{debugEvent.ExceptionAddress:X16}");

        var context = NativeMemory.AlignedAlloc((nuint)CONTEXT_SIZE_AMD64, 16);
        try
        {
            NativeMemory.Clear(context, (nuint)CONTEXT_SIZE_AMD64);
            *(uint*)((byte*)context + CTX_CONTEXTFLAGS) = CONTEXT_FULL_AMD64;

            if (GetThreadContext(threadHandle, (nint)context))
            {
                Console.Error.WriteLine(
                    $"[DEBUG][NATIVEDBG]   RIP=0x{ReadCtx(context, CTX_RIP):X16} RSP=0x{ReadCtx(context, CTX_RSP):X16} RBP=0x{ReadCtx(context, CTX_RBP):X16}");
                Console.Error.WriteLine(
                    $"[DEBUG][NATIVEDBG]   RAX=0x{ReadCtx(context, CTX_RAX):X16} RBX=0x{ReadCtx(context, CTX_RBX):X16} RCX=0x{ReadCtx(context, CTX_RCX):X16} RDX=0x{ReadCtx(context, CTX_RDX):X16}");
                Console.Error.WriteLine(
                    $"[DEBUG][NATIVEDBG]   RSI=0x{ReadCtx(context, CTX_RSI):X16} RDI=0x{ReadCtx(context, CTX_RDI):X16}");
                Console.Error.WriteLine(
                    $"[DEBUG][NATIVEDBG]   R8= 0x{ReadCtx(context, CTX_R8):X16} R9= 0x{ReadCtx(context, CTX_R9):X16} R10=0x{ReadCtx(context, CTX_R10):X16} R11=0x{ReadCtx(context, CTX_R11):X16}");
                Console.Error.WriteLine(
                    $"[DEBUG][NATIVEDBG]   R12=0x{ReadCtx(context, CTX_R12):X16} R13=0x{ReadCtx(context, CTX_R13):X16} R14=0x{ReadCtx(context, CTX_R14):X16} R15=0x{ReadCtx(context, CTX_R15):X16}");
            }
            else
            {
                Console.Error.WriteLine($"[DEBUG][NATIVEDBG]   GetThreadContext failed: {Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            NativeMemory.AlignedFree(context);
        }

        Console.Error.Flush();
    }

    private static unsafe ulong ReadCtx(void* context, int offset) => *(ulong*)((byte*)context + offset);

    private static string DescribeNativeExceptionCode(uint code) => code switch
    {
        0xC0000005 => "EXCEPTION_ACCESS_VIOLATION",
        0xC0000094 => "EXCEPTION_INT_DIVIDE_BY_ZERO",
        0xC000001D => "EXCEPTION_ILLEGAL_INSTRUCTION",
        0xC00000FD => "EXCEPTION_STACK_OVERFLOW",
        0xC0000409 => "STATUS_STACK_BUFFER_OVERRUN (fail-fast / GS cookie check)",
        0xC0000602 => "STATUS_FAIL_FAST_EXCEPTION",
        0xC0000420 => "STATUS_ASSERTION_FAILURE",
        0xE06D7363 => "MSVC C++ exception (already recovered in-process)",
        0x80000003 => "EXCEPTION_BREAKPOINT",
        0x80000004 => "EXCEPTION_SINGLE_STEP",
        _ => "unrecognized",
    };

    private static string DescribeNativeExitCode(uint exitCode) => exitCode switch
    {
        0 => "normal exit",
        0xC0000005 => "access violation",
        0xC00000FD => "stack overflow",
        0xC0000409 => "stack buffer overrun / fail-fast",
        0xC0000602 => "fail-fast exception",
        _ when exitCode < 0x100 => "SharpEmu.HLE.OrbisGen2Result, see OrbisGen2Result.cs",
        _ => "raw Win32/NTSTATUS exit code",
    };

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct DEBUG_EVENT
    {
        [FieldOffset(0)]
        public uint DebugEventCode;

        [FieldOffset(4)]
        public uint ProcessId;

        [FieldOffset(8)]
        public uint ThreadId;

        // EXCEPTION_DEBUG_INFO (DebugEventCode == EXCEPTION_DEBUG_EVENT)
        [FieldOffset(16)]
        public uint ExceptionCode;

        [FieldOffset(32)]
        public ulong ExceptionAddress;

        [FieldOffset(168)]
        public uint FirstChance;

        // CREATE_PROCESS_DEBUG_INFO (DebugEventCode == CREATE_PROCESS_DEBUG_EVENT)
        [FieldOffset(16)]
        public nint CreateProcessFileHandle;

        [FieldOffset(32)]
        public nint CreateProcessThreadHandle;

        // EXIT_PROCESS_DEBUG_INFO (DebugEventCode == EXIT_PROCESS_DEBUG_EVENT)
        [FieldOffset(16)]
        public uint ExitProcessCode;

        // OUTPUT_DEBUG_STRING_INFO (DebugEventCode == OUTPUT_DEBUG_STRING_EVENT)
        [FieldOffset(26)]
        public ushort DebugStringLength;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitForDebugEvent(out DEBUG_EVENT lpDebugEvent, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ContinueDebugEvent(uint dwProcessId, uint dwThreadId, uint dwContinueStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadContext(nint hThread, nint lpContext);
}

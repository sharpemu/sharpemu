// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Core.Diagnostics;

public static partial class WindowsCrashCapture
{
    private const uint ExceptionEvent = 1;
    private const uint CreateProcessEvent = 3;
    private const uint ExitProcessEvent = 5;
    private const uint LoadLibraryEvent = 6;
    private const uint BreakpointException = 0x80000003;
    private const uint DebugContinue = 0x00010002;
    private const uint DebugExceptionNotHandled = 0x80010001;
    private const uint ProcessReadAccess = 0x0450;
    private const uint ThreadContextAccess = 0x0008;
    private const uint DumpFullMemory = 0x00000002;
    private const uint DumpHandleData = 0x00000004;
    private const uint DumpUnloadedModules = 0x00000020;
    private const uint DumpMemoryInformation = 0x00000800;
    private const uint DumpThreadInformation = 0x00001000;
    private const uint DumpIgnoreInaccessibleMemory = 0x00020000;
    private const uint FullDumpFlags = DumpFullMemory | DumpHandleData | DumpUnloadedModules |
        DumpMemoryInformation | DumpThreadInformation | DumpIgnoreInaccessibleMemory;
    private const uint AllX64ContextFlags = 0x0010001F;

    private static unsafe void WriteDump(uint processId, DebugEvent debugEvent, string dumpPath)
    {
        using var process = OpenProcess(ProcessReadAccess, false, processId);
        if (process.IsInvalid)
            throw NativeFailure("Open the crashed process");
        using var thread = OpenThread(ThreadContextAccess, false, debugEvent.ThreadId);
        if (thread.IsInvalid)
            throw NativeFailure("Open the faulting thread");
        var context = (ThreadContext*)NativeMemory.AlignedAlloc((nuint)sizeof(ThreadContext), 16);
        if (context == null)
            throw new OutOfMemoryException();
        try
        {
            NativeMemory.Clear(context, (nuint)sizeof(ThreadContext));
            context->Flags = AllX64ContextFlags;
            if (!GetThreadContext(thread, context))
                throw NativeFailure("Read the faulting thread context");

            // The exception pointers refer to this helper, not to the crashed process.
            var record = debugEvent.Exception;
            record.NestedRecord = 0;
            var pointers = new ExceptionPointers { Record = &record, Context = context };
            var exception = new DumpExceptionInformation
            {
                ThreadId = debugEvent.ThreadId,
                Pointers = &pointers,
                ClientPointers = 0,
            };
            using var output = new FileStream(dumpPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            if (!MiniDumpWriteDump(process, processId, output.SafeFileHandle, FullDumpFlags, &exception, 0, 0))
                throw NativeFailure("Write the crash dump");
            output.Flush(flushToDisk: true);
        }
        finally
        {
            NativeMemory.AlignedFree(context);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 176)]
    internal struct DebugEvent
    {
        [FieldOffset(0)] public uint Kind;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public ExceptionRecord Exception;
        [FieldOffset(168)] public uint FirstChance;
        [FieldOffset(16)] public nint FileHandle;
        [FieldOffset(16)] public uint ExitCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ExceptionRecord
    {
        public uint Code;
        public uint Flags;
        public nint NestedRecord;
        public nint Address;
        public uint ParameterCount;
        public fixed ulong Parameters[15];
    }

    [StructLayout(LayoutKind.Explicit, Size = 1232)]
    internal struct ThreadContext
    {
        [FieldOffset(48)] public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ExceptionPointers
    {
        public ExceptionRecord* Record;
        public ThreadContext* Context;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal unsafe struct DumpExceptionInformation
    {
        public uint ThreadId;
        public void* Pointers;
        public int ClientPointers;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugActiveProcess(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugActiveProcessStop(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitForDebugEventEx(out DebugEvent debugEvent, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ContinueDebugEvent(uint processId, uint threadId, uint status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeWaitHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetThreadContext(SafeWaitHandle thread, ThreadContext* context);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool MiniDumpWriteDump(SafeProcessHandle process, uint processId,
        SafeFileHandle file, uint dumpType, DumpExceptionInformation* exception, nint userStream, nint callback);
}

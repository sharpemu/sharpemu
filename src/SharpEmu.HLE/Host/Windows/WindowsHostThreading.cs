// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

internal sealed unsafe partial class WindowsHostThreading : IHostThreading
{
    private const uint StackSizeParamIsAReservation = 0x00010000u;
    private const uint ThreadGetContext = 0x0008u;
    private const uint ThreadSuspendResume = 0x0002u;

    // Win64 CONTEXT layout (CONTROL | INTEGER only — no XMM state is requested).
    private const int Win64ContextSize = 0x4D0;
    private const int Win64ContextFlagsOffset = 0x30;
    private const uint ContextAmd64ControlInteger = 0x00100003u;
    private const int CtxRax = 120;
    private const int CtxRcx = 128;
    private const int CtxRdx = 136;
    private const int CtxRbx = 144;
    private const int CtxRsp = 152;
    private const int CtxRbp = 160;
    private const int CtxRip = 248;

    private static int _timerResolutionRequested;

    public void RequestTimerResolution()
    {
        if (Interlocked.Exchange(ref _timerResolutionRequested, 1) != 0)
        {
            return;
        }

        try
        {
            if (TimeBeginPeriod(1) != 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Host timer resolution request rejected; " +
                    "timed waits keep the default ~15.6 ms granularity.");
            }
        }
        catch (DllNotFoundException exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Host timer resolution unavailable: {exception.Message}");
        }
    }

    public uint AllocateTlsSlot() => TlsAlloc();

    public bool FreeTlsSlot(uint slot) => TlsFree(slot);

    public bool SetTlsValue(uint slot, nint value) => TlsSetValue(slot, value);

    public nint GetTlsValue(uint slot) => TlsGetValue(slot);

    public uint CurrentThreadId => GetCurrentThreadId();

    public bool TrySetCurrentThreadAffinity(nuint affinityMask)
    {
        return SetThreadAffinityMask(GetCurrentThread(), affinityMask) != 0;
    }

    public nint CreateNativeThread(nint entry, nint parameter, nuint stackReserveBytes, out uint threadId)
    {
        return CreateThread(0, stackReserveBytes, entry, parameter, StackSizeParamIsAReservation, out threadId);
    }

    public bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds)
    {
        return WaitForSingleObject(threadHandle, timeoutMilliseconds) == 0u;
    }

    public void CloseThreadHandle(nint threadHandle)
    {
        _ = CloseHandle(threadHandle);
    }

    public bool TryCaptureThreadRegisters(uint threadId, out HostCapturedRegisters registers)
    {
        registers = default;
        var threadHandle = OpenThread(ThreadGetContext | ThreadSuspendResume, false, threadId);
        if (threadHandle == 0)
        {
            return false;
        }

        void* contextRecord = null;
        var suspended = false;
        try
        {
            if (SuspendThread(threadHandle) == uint.MaxValue)
            {
                return false;
            }

            suspended = true;
            // CONTEXT requires 16-byte alignment (it embeds M128A fields);
            // NativeMemory.AllocZeroed guarantees max_align_t, stackalloc only
            // pointer-size — so this stays a native allocation.
            contextRecord = NativeMemory.AllocZeroed((nuint)Win64ContextSize);
            *(uint*)((byte*)contextRecord + Win64ContextFlagsOffset) = ContextAmd64ControlInteger;
            if (!GetThreadContext(threadHandle, contextRecord))
            {
                return false;
            }

            registers = new HostCapturedRegisters(
                ReadU64(contextRecord, CtxRip),
                ReadU64(contextRecord, CtxRsp),
                ReadU64(contextRecord, CtxRbp),
                ReadU64(contextRecord, CtxRax),
                ReadU64(contextRecord, CtxRbx),
                ReadU64(contextRecord, CtxRcx),
                ReadU64(contextRecord, CtxRdx));
            return true;
        }
        finally
        {
            if (contextRecord != null)
            {
                NativeMemory.Free(contextRecord);
            }
            if (suspended)
            {
                _ = ResumeThread(threadHandle);
            }
            _ = CloseHandle(threadHandle);
        }
    }

    private static ulong ReadU64(void* contextRecord, int offset)
    {
        return *(ulong*)((byte*)contextRecord + offset);
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint TlsAlloc();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TlsFree(uint dwTlsIndex);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TlsSetValue(uint dwTlsIndex, nint lpTlsValue);

    [LibraryImport("kernel32.dll")]
    private static partial nint TlsGetValue(uint dwTlsIndex);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateThread(
        nint lpThreadAttributes,
        nuint dwStackSize,
        nint lpStartAddress,
        nint lpParameter,
        uint dwCreationFlags,
        out uint lpThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenThread(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SuspendThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadContext(nint hThread, void* lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint uPeriod);
}

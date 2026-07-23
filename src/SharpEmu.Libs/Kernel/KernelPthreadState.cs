// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

internal static class KernelPthreadState
{
    private const int ThreadObjectSize = 0x1000;

    private static readonly ConcurrentDictionary<ulong, ThreadIdentity> Threads = new();
    private static readonly byte[] ZeroThreadObject = new byte[ThreadObjectSize];
    private static long _nextUniqueThreadId = 1;

    [ThreadStatic]
    private static ulong _currentThreadHandle;

    [ThreadStatic]
    private static ulong _currentThreadUniqueId;

    internal readonly record struct ThreadIdentity(ulong UniqueId, string Name);

    internal static ulong GetCurrentThreadHandle()
    {
        var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        // Prefer the bound guest handle even when it is not yet in Threads.
        // Falling through to a synthetic ThreadStatic handle while a guest
        // thread is bound causes mutex owner mismatches (unlock PERM → hang).
        if (guestThreadHandle != 0)
        {
            EnsureGuestThreadIdentity(guestThreadHandle);
            return guestThreadHandle;
        }

        EnsureCurrentThreadRegistered();
        return _currentThreadHandle;
    }

    internal static ulong GetCurrentThreadUniqueId()
    {
        var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        if (guestThreadHandle != 0)
        {
            return EnsureGuestThreadIdentity(guestThreadHandle).UniqueId;
        }

        EnsureCurrentThreadRegistered();
        return _currentThreadUniqueId;
    }

    internal static string DescribeThreadHandle(ulong threadHandle)
    {
        if (threadHandle == 0)
        {
            return "none";
        }

        return TryGetThreadIdentity(threadHandle, out var identity)
            ? $"0x{threadHandle:X16}('{identity.Name}')"
            : $"0x{threadHandle:X16}";
    }

    internal static ulong CreateThreadHandle(string name)
    {
        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        return AllocateThreadHandle(uniqueId, name);
    }

    internal static bool TryGetThreadIdentity(ulong threadHandle, out ThreadIdentity identity)
    {
        return Threads.TryGetValue(threadHandle, out identity);
    }

    private static ThreadIdentity EnsureGuestThreadIdentity(ulong guestThreadHandle)
    {
        if (Threads.TryGetValue(guestThreadHandle, out var existing))
        {
            return existing;
        }

        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        var identity = new ThreadIdentity(uniqueId, $"Guest-0x{guestThreadHandle:X}");
        return Threads.GetOrAdd(guestThreadHandle, identity);
    }

    private static void EnsureCurrentThreadRegistered()
    {
        if (_currentThreadHandle != 0)
        {
            return;
        }

        var uniqueId = unchecked((ulong)Interlocked.Increment(ref _nextUniqueThreadId));
        var name = $"Thread-{uniqueId:X}";
        _currentThreadHandle = AllocateThreadHandle(uniqueId, name);
        _currentThreadUniqueId = uniqueId;
    }

    private static ulong AllocateThreadHandle(ulong uniqueId, string name)
    {
        var pointer = Marshal.AllocHGlobal(ThreadObjectSize);
        Marshal.Copy(ZeroThreadObject, 0, pointer, ThreadObjectSize);

        var handle = unchecked((ulong)pointer.ToInt64());
        Threads[handle] = new ThreadIdentity(uniqueId, string.IsNullOrWhiteSpace(name) ? $"Thread-{uniqueId:X}" : name);

        return handle;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// POSIX permits signalling a condition variable without holding its mutex, so
// when a cond waiter is woken the mutex may already be free. The signal path
// (CompleteCondWaiterLocked) moves the waiter onto the mutex's wait queue to
// re-acquire it; if it fails to grant a free mutex, the waiter is stranded on a
// free mutex forever and the guest deadlocks (observed hanging Silent Hill deep
// in Unreal Engine init). These tests exercise that handoff directly.
public sealed class PthreadCondReacquireTests
{
    private const ulong Waiter = 0xC0C0_0000_0000_0001;
    private const ulong OtherOwner = 0xD0D0_0000_0000_0002;

    [Fact]
    public void SignalWithFreeMutexGrantsReacquisitionToWaiter()
    {
        var mutex = NewMutex();
        var cond = NewCond();
        var waiter = EnqueueCondWaiter(cond, mutex, Waiter);

        var completed = CompleteCondWaiterLocked(cond, waiter);

        Assert.True(completed);
        // The re-acquisition is granted immediately instead of being left to
        // rot on a free mutex: the waiter now owns it and the queue is empty.
        Assert.Equal(Waiter, OwnerThreadId(mutex));
        Assert.Equal(1, RecursionCount(mutex));
        Assert.Equal(0, MutexWaiterCount(mutex));
        Assert.Equal(1, MutexWaiterGranted(waiter));
    }

    [Fact]
    public void SignalWithHeldMutexLeavesWaiterQueued()
    {
        var mutex = NewMutex();
        SetOwner(mutex, OtherOwner, recursion: 1);
        var cond = NewCond();
        var waiter = EnqueueCondWaiter(cond, mutex, Waiter);

        var completed = CompleteCondWaiterLocked(cond, waiter);

        Assert.True(completed);
        // The mutex is held elsewhere, so the waiter must wait its turn (the
        // owning thread's unlock hands it over) — ownership is untouched.
        Assert.Equal(OtherOwner, OwnerThreadId(mutex));
        Assert.Equal(1, MutexWaiterCount(mutex));
        Assert.Equal(0, MutexWaiterGranted(waiter));
    }

    // --- reflection helpers over the private synchronization internals ---

    private static readonly Type ExportsType = typeof(KernelPthreadCompatExports);
    private static readonly Type MutexStateType = Nested("PthreadMutexState");
    private static readonly Type CondStateType = Nested("PthreadCondState");
    private static readonly Type CondWaiterType = Nested("PthreadCondWaiter");
    private static readonly Type MutexWaiterType = Nested("PthreadMutexWaiter");

    private static Type Nested(string name) =>
        ExportsType.GetNestedType(name, BindingFlags.NonPublic)!;

    private static object NewMutex() => Activator.CreateInstance(MutexStateType, nonPublic: true)!;

    private static object NewCond() => Activator.CreateInstance(CondStateType, nonPublic: true)!;

    private static object EnqueueCondWaiter(object cond, object mutex, ulong threadId)
    {
        var waiter = Activator.CreateInstance(CondWaiterType, nonPublic: true)!;
        CondWaiterType.GetProperty("ThreadId")!.SetValue(waiter, threadId);
        CondWaiterType.GetProperty("MutexState")!.SetValue(waiter, mutex);
        CondWaiterType.GetProperty("WakeKey")!.SetValue(waiter, string.Empty);
        CondWaiterType.GetProperty("Cooperative")!.SetValue(waiter, false);

        var queue = CondStateType.GetProperty("WaiterQueue")!.GetValue(cond)!;
        var addLast = queue.GetType().GetMethod("AddLast", [CondWaiterType])!;
        var node = addLast.Invoke(queue, [waiter]);
        CondWaiterType.GetProperty("Node")!.SetValue(waiter, node);

        var waiters = (int)CondStateType.GetProperty("Waiters")!.GetValue(cond)!;
        CondStateType.GetProperty("Waiters")!.SetValue(cond, waiters + 1);
        return waiter;
    }

    private static bool CompleteCondWaiterLocked(object cond, object waiter)
    {
        var method = ExportsType.GetMethod(
            "CompleteCondWaiterLocked",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        // Real callers always hold the cond's SyncRoot (the method pulses it).
        var syncRoot = CondStateType.GetProperty("SyncRoot")!.GetValue(cond)!;
        lock (syncRoot)
        {
            return (bool)method.Invoke(null, [cond, waiter, false])!;
        }
    }

    private static void SetOwner(object mutex, ulong threadId, int recursion)
    {
        MutexStateType.GetProperty("OwnerThreadId")!.SetValue(mutex, threadId);
        MutexStateType.GetProperty("RecursionCount")!.SetValue(mutex, recursion);
    }

    private static ulong OwnerThreadId(object mutex) =>
        (ulong)MutexStateType.GetProperty("OwnerThreadId")!.GetValue(mutex)!;

    private static int RecursionCount(object mutex) =>
        (int)MutexStateType.GetProperty("RecursionCount")!.GetValue(mutex)!;

    private static int MutexWaiterCount(object mutex) =>
        ((ICollection)MutexStateType.GetProperty("Waiters")!.GetValue(mutex)!).Count;

    private static int MutexWaiterGranted(object condWaiter)
    {
        var mutexWaiter = CondWaiterType.GetProperty("MutexWaiter")!.GetValue(condWaiter);
        return mutexWaiter is null
            ? -1
            : (int)MutexWaiterType.GetField("Granted")!.GetValue(mutexWaiter)!;
    }
}

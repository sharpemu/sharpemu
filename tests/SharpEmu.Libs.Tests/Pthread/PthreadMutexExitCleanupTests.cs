// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Threading;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// A guest thread that exits while owning a mutex — or while queued waiting for
// one — used to strand that mutex forever: ownership was cleared only by an
// explicit unlock, and a waiter was removed only when granted the lock. The
// fast-acquire path refuses a mutex whose wait queue is non-empty, so every
// future locker blocked permanently. ReleaseThreadSynchronizationState is the
// on-exit cleanup that fixes this; these tests exercise it directly.
public sealed class PthreadMutexExitCleanupTests
{
    private const ulong DeadThread = 0xAAAA_0000_0000_0001;
    private const ulong SurvivorThread = 0xBBBB_0000_0000_0002;

    [Fact]
    public void ExitingWaiterIsDequeuedAndSuccessorIsGranted()
    {
        var (address, state) = RegisterMutex();
        try
        {
            // Dead thread is queued at the head; a survivor is queued behind it.
            EnqueueWaiter(state, DeadThread);
            EnqueueWaiter(state, SurvivorThread);
            Assert.Equal(0UL, OwnerThreadId(state));
            Assert.Equal(2, WaiterCount(state));

            KernelPthreadCompatExports.ReleaseThreadSynchronizationState(DeadThread);

            // The ghost waiter is gone and the survivor now owns the mutex,
            // instead of being wedged behind a head that never wakes.
            Assert.Equal(SurvivorThread, OwnerThreadId(state));
            Assert.Equal(1, RecursionCount(state));
            Assert.Equal(0, WaiterCount(state));
        }
        finally
        {
            UnregisterMutex(address);
        }
    }

    [Fact]
    public void ExitingOwnerReleasesLockAndHandsItToWaiter()
    {
        var (address, state) = RegisterMutex();
        try
        {
            // Dead thread owns the mutex; a survivor is blocked waiting for it.
            SetOwner(state, DeadThread, recursion: 1);
            EnqueueWaiter(state, SurvivorThread);

            KernelPthreadCompatExports.ReleaseThreadSynchronizationState(DeadThread);

            Assert.Equal(SurvivorThread, OwnerThreadId(state));
            Assert.Equal(1, RecursionCount(state));
            Assert.Equal(0, WaiterCount(state));
        }
        finally
        {
            UnregisterMutex(address);
        }
    }

    [Fact]
    public void ExitingUncontendedOwnerLeavesMutexFree()
    {
        var (address, state) = RegisterMutex();
        try
        {
            SetOwner(state, DeadThread, recursion: 2);

            KernelPthreadCompatExports.ReleaseThreadSynchronizationState(DeadThread);

            // No waiters: the mutex is simply released, available to the next locker.
            Assert.Equal(0UL, OwnerThreadId(state));
            Assert.Equal(0, RecursionCount(state));
            Assert.Equal(0, WaiterCount(state));
        }
        finally
        {
            UnregisterMutex(address);
        }
    }

    // --- reflection helpers over the private synchronization internals ---

    private static readonly Type ExportsType = typeof(KernelPthreadCompatExports);

    private static readonly Type MutexStateType =
        ExportsType.GetNestedType("PthreadMutexState", BindingFlags.NonPublic)!;

    private static IDictionary MutexStates =>
        (IDictionary)ExportsType
            .GetField("_mutexStates", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static ulong _nextAddress = 0xDEAD_0000_0000_0000;

    private static (ulong Address, object State) RegisterMutex()
    {
        var state = Activator.CreateInstance(MutexStateType, nonPublic: true)!;
        var address = Interlocked.Increment(ref _nextAddress);
        MutexStates[address] = state;
        return (address, state);
    }

    private static void UnregisterMutex(ulong address) => MutexStates.Remove(address);

    private static void EnqueueWaiter(object state, ulong threadId)
    {
        var method = ExportsType.GetMethod(
            "EnqueueMutexWaiterLocked",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        lock (state)
        {
            method.Invoke(null, [state, threadId, false, null]);
        }
    }

    private static void SetOwner(object state, ulong threadId, int recursion)
    {
        MutexStateType.GetProperty("OwnerThreadId")!.SetValue(state, threadId);
        MutexStateType.GetProperty("RecursionCount")!.SetValue(state, recursion);
    }

    private static ulong OwnerThreadId(object state) =>
        (ulong)MutexStateType.GetProperty("OwnerThreadId")!.GetValue(state)!;

    private static int RecursionCount(object state) =>
        (int)MutexStateType.GetProperty("RecursionCount")!.GetValue(state)!;

    private static int WaiterCount(object state) =>
        ((ICollection)MutexStateType.GetProperty("Waiters")!.GetValue(state)!).Count;
}

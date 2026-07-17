// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// DirectExecutionBackend.WakeBlockedThreads and RunGuestThread both decide
// whether a blocked guest thread is actually ready to resume. A blocked
// thread's readiness predicate lives in one of two places depending on how
// it was registered: an IGuestThreadBlockWaiter (BlockWaiter), or — for the
// resumeHandler/wakeHandler compatibility bridge every pthread mutex, cond,
// rwlock and semaphore wait uses — a bare BlockWakeHandler with BlockWaiter
// left null. TryConsumeRegisteredWakeSignal is the shared decision the fix
// introduced so both call sites check the same predicate the same way.
public sealed class GuestThreadWakeSignalTests
{
    private static readonly Type GuestThreadStateType = typeof(DirectExecutionBackend)
        .GetNestedType("GuestThreadState", BindingFlags.NonPublic)!;

    private static readonly MethodInfo TryConsumeRegisteredWakeSignal = typeof(DirectExecutionBackend)
        .GetMethod("TryConsumeRegisteredWakeSignal", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static object CreateThreadState() => Activator.CreateInstance(GuestThreadStateType)!;

    private static void SetProperty(object threadState, string name, object? value) =>
        GuestThreadStateType.GetProperty(name)!.SetValue(threadState, value);

    private static bool Invoke(object threadState) =>
        (bool)TryConsumeRegisteredWakeSignal.Invoke(null, [threadState])!;

    [Fact]
    public void GuestThreadStateType_And_Method_AreResolvable()
    {
        Assert.NotNull(GuestThreadStateType);
        Assert.NotNull(TryConsumeRegisteredWakeSignal);
    }

    [Fact]
    public void WithBlockWaiter_UsesWaiterTryWake_Satisfied()
    {
        var thread = CreateThreadState();
        var waiter = new FakeBlockWaiter(tryWakeResult: true);
        SetProperty(thread, "BlockWaiter", waiter);

        Assert.True(Invoke(thread));
        Assert.Equal(1, waiter.TryWakeCallCount);
    }

    [Fact]
    public void WithBlockWaiter_UsesWaiterTryWake_NotSatisfied()
    {
        // #212/#328-adjacent regression guard: a registered IGuestThreadBlockWaiter must
        // never be bypassed even when a stale BlockWakeHandler also happens to be set.
        var thread = CreateThreadState();
        var waiter = new FakeBlockWaiter(tryWakeResult: false);
        SetProperty(thread, "BlockWaiter", waiter);

        Assert.False(Invoke(thread));
        Assert.Equal(1, waiter.TryWakeCallCount);
    }

    [Fact]
    public void WithOnlyBlockWakeHandler_InvokesItInsteadOfSkipping()
    {
        // The bug this guards: pthread_mutex_lock/pthread_cond_wait/pthread_rwlock_*/
        // sceKernelWaitSema all register through the resumeHandler/wakeHandler overload,
        // which leaves BlockWaiter null and the readiness predicate in BlockWakeHandler.
        // Before the fix, WakeBlockedThreads and RunGuestThread's exit-race check only
        // tested BlockWaiter, so this predicate was never evaluated and those waits woke
        // unconditionally on any wakeKey match regardless of whether the mutex/condition
        // was actually available.
        var thread = CreateThreadState();
        var handlerCalls = 0;
        Func<bool> wakeHandler = () =>
        {
            handlerCalls++;
            return true;
        };
        SetProperty(thread, "BlockWaiter", null);
        SetProperty(thread, "BlockWakeHandler", wakeHandler);

        Assert.True(Invoke(thread));
        Assert.Equal(1, handlerCalls);
    }

    [Fact]
    public void WithOnlyBlockWakeHandler_ThatReturnsFalse_DoesNotWake()
    {
        var thread = CreateThreadState();
        SetProperty(thread, "BlockWaiter", null);
        SetProperty(thread, "BlockWakeHandler", (Func<bool>)(() => false));

        Assert.False(Invoke(thread));
    }

    [Fact]
    public void WithNeitherWaiterNorHandler_AlwaysWakes()
    {
        // Plain wakeKey waits (event flags, event queues) register with neither a
        // waiter nor a handler; a wakeKey match alone must still be sufficient.
        var thread = CreateThreadState();
        SetProperty(thread, "BlockWaiter", null);
        SetProperty(thread, "BlockWakeHandler", null);

        Assert.True(Invoke(thread));
    }

    [Fact]
    public void BlockWaiterTakesPrecedenceOverBlockWakeHandler()
    {
        // The two registration overloads are mutually exclusive in production
        // (RegisterBlockedGuestThreadContinuation always clears the other), but the
        // decision function should still prefer BlockWaiter defensively if both are set.
        var thread = CreateThreadState();
        var waiter = new FakeBlockWaiter(tryWakeResult: true);
        var handlerCalls = 0;
        SetProperty(thread, "BlockWaiter", waiter);
        SetProperty(thread, "BlockWakeHandler", (Func<bool>)(() =>
        {
            handlerCalls++;
            return true;
        }));

        Assert.True(Invoke(thread));
        Assert.Equal(1, waiter.TryWakeCallCount);
        Assert.Equal(0, handlerCalls);
    }

    private sealed class FakeBlockWaiter(bool tryWakeResult) : IGuestThreadBlockWaiter
    {
        public int TryWakeCallCount { get; private set; }

        public int Resume() => 0;

        public bool TryWake()
        {
            TryWakeCallCount++;
            return tryWakeResult;
        }
    }
}

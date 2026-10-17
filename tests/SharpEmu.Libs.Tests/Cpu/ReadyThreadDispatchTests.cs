// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

[CollectionDefinition("ReadyThreadDispatch", DisableParallelization = true)]
public sealed class ReadyThreadDispatchCollection;

[Collection("ReadyThreadDispatch")]
public sealed class ReadyThreadDispatchTests
{
    [NativeX64Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadyNotificationWorksBeforeOrAfterDispatcherWait(bool enqueueBeforeStart)
    {
        using var harness = new DispatchHarness();
        var thread = harness.CreateThread(1);
        if (enqueueBeforeStart) harness.Enqueue(thread);
        harness.Start();
        if (!enqueueBeforeStart)
        {
            harness.WaitUntilParked();
            harness.Enqueue(thread);
        }
        harness.WaitUntilCompleted(thread);
        harness.AssertCompleted(1);
    }

    [NativeX64Fact]
    public void ActiveExecutorReleaseWakesDispatcherWithoutAnotherGuestCall()
    {
        using var harness = new DispatchHarness();
        var thread = harness.CreateThread(2, active: true);
        harness.Enqueue(thread);
        harness.Start();
        Assert.True(SpinWait.SpinUntil(() => harness.ReadDeferrals(thread) > 0, TimeSpan.FromSeconds(5)));
        harness.WaitUntilParked();
        Assert.Equal("Ready", harness.ReadState(thread));
        Assert.True(harness.ReadActive(thread));
        harness.Release(thread);
        harness.WaitUntilCompleted(thread);
        harness.AssertCompleted(2);
    }

    [NativeX64Fact]
    public void NotificationsSurviveRepeatedIdleTransitionsAndCompetingDrains()
    {
        using var harness = new DispatchHarness();
        harness.Start();
        for (ulong handle = 10; handle < 42; handle++)
        {
            harness.WaitUntilParked();
            var thread = harness.CreateThread(handle);
            harness.Enqueue(thread);
            Parallel.Invoke(harness.Drain, harness.Drain);
            harness.WaitUntilCompleted(thread);
        }
        harness.AssertCompleted(Enumerable.Range(10, 32).Select(value => (ulong)value).ToArray());
    }

    [NativeX64Fact]
    public void StopWakesIdleDispatcherAndAllowsRestart()
    {
        using var harness = new DispatchHarness();
        harness.Start();
        harness.WaitUntilParked();
        var stopped = harness.Dispatcher;
        harness.Stop();
        Assert.False(stopped.IsAlive);
        var thread = harness.CreateThread(50);
        harness.Enqueue(thread);
        harness.Start();
        harness.WaitUntilCompleted(thread);
        harness.AssertCompleted(50);
    }

    [NativeX64Fact]
    public void CompetingClaimsCannotAssignTheSameExecutorTwice()
    {
        using var harness = new DispatchHarness();
        var thread = harness.CreateThread(60);
        harness.Enqueue(thread);
        harness.Enqueue(thread);
        var claimCount = 0;

        Parallel.For(0, 16, _ =>
        {
            if (harness.TryClaim()) Interlocked.Increment(ref claimCount);
        });

        Assert.Equal(1, claimCount);
        Assert.Equal("Running", harness.ReadState(thread));
        Assert.True(harness.ReadActive(thread));
        harness.AssertQueueEmpty();
    }

    private sealed class DispatchHarness : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type BackendType = typeof(DirectExecutionBackend);
        private static readonly Type ThreadType = BackendType.GetNestedType("GuestThreadState", BindingFlags.NonPublic)!;
        private static readonly Type RunStateType = BackendType.GetNestedType("GuestThreadRunState", BindingFlags.NonPublic)!;
        private readonly DirectExecutionBackend _backend;
        private readonly object _gate;
        private readonly IDictionary _threads;

        internal DispatchHarness()
        {
            var modules = new ModuleManager();
            modules.Freeze();
            _backend = new DirectExecutionBackend(modules);
            _gate = BackendType.GetField("_guestThreadGate", PrivateInstance)!.GetValue(_backend)!;
            _threads = (IDictionary)BackendType.GetField("_guestThreads", PrivateInstance)!.GetValue(_backend)!;
            // Exercise real scheduling without entering native guest code.
            BackendType.GetField("_forcedGuestExit", PrivateInstance)!.SetValue(_backend, true);
        }

        internal Thread Dispatcher => (Thread)BackendType.GetField("_readyDispatchThread", PrivateInstance)!.GetValue(_backend)!;
        internal void Start() => Invoke("StartReadyThreadDispatcher");
        internal void Stop() => Invoke("StopReadyThreadDispatcher");
        internal void Drain() => Invoke("DispatchReadyGuestThreads");

        internal bool TryClaim()
        {
            lock (_gate) return (bool)Invoke("TryClaimReadyGuestThreadLocked", new object?[] { null })!;
        }

        internal object CreateThread(ulong handle, bool active = false)
        {
            var thread = Activator.CreateInstance(ThreadType)!;
            ThreadType.GetProperty("ThreadHandle")!.SetValue(thread, handle);
            ThreadType.GetProperty("Name")!.SetValue(thread, $"dispatch-test-{handle}");
            ThreadType.GetProperty("State")!.SetValue(thread, Enum.Parse(RunStateType, "Ready"));
            ThreadType.GetProperty("ExecutorActive")!.SetValue(thread, active);
            lock (_gate) _threads.Add(handle, thread);
            return thread;
        }

        internal void Enqueue(object thread)
        {
            lock (_gate) Invoke("EnqueueReadyGuestThreadLocked", thread);
        }

        internal void Release(object thread)
        {
            lock (_gate)
                Assert.False((bool)Invoke("TryReleaseGuestThreadExecutorLocked", thread, null)!);
        }

        internal string ReadState(object thread)
        {
            lock (_gate) return ThreadType.GetProperty("State")!.GetValue(thread)!.ToString()!;
        }

        internal bool ReadActive(object thread)
        {
            lock (_gate) return (bool)ThreadType.GetProperty("ExecutorActive")!.GetValue(thread)!;
        }

        internal long ReadDeferrals(object thread)
        {
            lock (_gate) return Convert.ToInt64(ThreadType.GetProperty("ExecutorClaimDeferrals")!.GetValue(thread));
        }

        internal void WaitUntilParked() => Assert.True(SpinWait.SpinUntil(
            () => (Dispatcher.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));

        internal void WaitUntilCompleted(object thread) => Assert.True(SpinWait.SpinUntil(
            () => ReadState(thread) == "Faulted" && !ReadActive(thread), TimeSpan.FromSeconds(5)));

        internal void AssertQueueEmpty()
        {
            lock (_gate)
                Assert.Equal(0, BackendType.GetField("_readyGuestThreadCount", PrivateInstance)!.GetValue(_backend));
        }

        internal void AssertCompleted(params ulong[] handles)
        {
            AssertQueueEmpty();
            lock (_gate)
            {
                foreach (var handle in handles)
                {
                    var thread = _threads[handle]!;
                    Assert.Equal("Faulted", ReadState(thread));
                    Assert.False(ReadActive(thread));
                    Assert.NotNull(ThreadType.GetProperty("ExecutionRunner")!.GetValue(thread));
                }
            }
        }

        private object? Invoke(string name, params object?[] arguments) =>
            BackendType.GetMethod(name, PrivateInstance)!.Invoke(_backend, arguments);

        public void Dispose()
        {
            Stop();
            lock (_gate)
                foreach (var thread in _threads.Values)
                    ThreadType.GetProperty("ExecutorActive")!.SetValue(thread, false);
            _backend.Dispose();
        }
    }
}

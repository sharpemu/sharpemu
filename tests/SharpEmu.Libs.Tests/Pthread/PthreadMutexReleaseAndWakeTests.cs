// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// The handoff policy is captured per mutex from a process-wide default, so tests that pick a
// policy must not run alongside tests that create mutexes.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PthreadMutexHandoffModeCollection
{
    public const string Name = "PthreadMutexHandoffMode";
}

/// <summary>
/// Release-and-wake (the default) frees the mutex on unlock and wakes the queue head to
/// re-contend, so a running thread may take the mutex before the parked head gets back on a
/// CPU. FIFO handoff (SHARPEMU_MUTEX_HANDOFF=fifo) gives the mutex to the head directly.
/// </summary>
[Collection(PthreadMutexHandoffModeCollection.Name)]
public sealed class PthreadMutexReleaseAndWakeTests
{
    [Theory]
    [InlineData(false, 0xB_0000_0000UL)]
    [InlineData(true, 0xB_1000_0000UL)]
    public void ContendedLockKeepsExclusionAndEveryThreadFinishes(bool fifo, ulong memoryBase)
    {
        const int threads = 8;
        const int iterations = 300;
        var mutexAddress = memoryBase + 0x100;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var setup = new CpuContext(memory, Generation.Gen5);
        Assert.True(setup.TryWriteUInt64(mutexAddress, 1));

        WithHandoffMode(fifo, () =>
        {
            setup[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(setup));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(setup));
        });

        var inside = 0;
        var overlaps = 0;
        var failures = 0;
        var counter = 0;
        var completed = new int[threads];
        using var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, threads).Select(index => new Thread(() =>
        {
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = mutexAddress;
            start.Wait();
            for (var step = 0; step < iterations; step++)
            {
                if (KernelPthreadCompatExports.PthreadMutexLock(context) != 0)
                {
                    Interlocked.Increment(ref failures);
                    continue;
                }

                if (Interlocked.Increment(ref inside) != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                counter++;
                // Vary the hold time so waiters both spin and park.
                Thread.SpinWait((step & 7) == 0 ? 2000 : 20);
                Interlocked.Decrement(ref inside);

                if (KernelPthreadCompatExports.PthreadMutexUnlock(context) != 0)
                {
                    Interlocked.Increment(ref failures);
                }

                completed[index]++;
            }
        }) { IsBackground = true }).ToArray();

        foreach (var worker in workers)
        {
            worker.Start();
        }

        start.Set();
        foreach (var worker in workers)
        {
            Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "a thread made no progress");
        }

        Assert.Equal(0, failures);
        Assert.Equal(0, overlaps);
        Assert.Equal(threads * iterations, counter);
        Assert.All(completed, count => Assert.Equal(iterations, count));
    }

    [Fact]
    public void ParkedGuestWaiterIsWokenWithoutOwnershipAndReacquiresAfterBargersStop()
    {
        const ulong memoryBase = 0xB_2000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        const ulong ownerThread = 0xB01;
        const ulong waiterThread = 0xB02;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));

        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, RecordingScheduler>();
        var recorder = (RecordingScheduler)(object)scheduler;
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            WithHandoffMode(fifo: false, () =>
                RunAsGuest(ownerThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext))));

            // The waiter spins, then parks cooperatively.
            var (blockWaiter, wakeKey) = ParkGuestWaiter(memory, mutexAddress, waiterThread);
            Assert.False(blockWaiter.TryWake()); // the park recheck: still owned, stay parked

            // The unlock frees the mutex and wakes the parked head through the scheduler;
            // the wake makes the waiter runnable but does not give it the mutex.
            recorder.OnWake = key => key == wakeKey && blockWaiter.TryWake();
            RunAsGuest(ownerThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext)));
            Assert.Equal(new[] { wakeKey }, recorder.WakeKeys);
            Assert.Equal(1, recorder.WakesGranted);

            // A running thread barges in before the woken waiter gets a CPU.
            var bargerContext = new CpuContext(memory, Generation.Gen5);
            bargerContext[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexTrylock(bargerContext));

            // The woken waiter resumes, loses, and waits; it keeps its queue position.
            using var acquired = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var resumeResult = -1;
            var unlockResult = -1;
            var resumed = new Thread(() => RunAsGuest(waiterThread, () =>
            {
                resumeResult = blockWaiter.Resume();
                acquired.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                var waiterContext = new CpuContext(memory, Generation.Gen5);
                waiterContext[CpuRegister.Rdi] = mutexAddress;
                unlockResult = KernelPthreadCompatExports.PthreadMutexUnlock(waiterContext);
            })) { IsBackground = true };
            resumed.Start();
            Assert.False(acquired.Wait(TimeSpan.FromMilliseconds(100)), "the woken waiter took an owned mutex");

            // More bargers keep the mutex busy for a while, then stop. The waiter may win
            // during the barging or after it; it must not stay parked once the bargers stop.
            using var stopBargers = new ManualResetEventSlim(false);
            var bargeCount = 0;
            var bargerFailures = 0;
            var bargers = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
            {
                var context = new CpuContext(memory, Generation.Gen5);
                context[CpuRegister.Rdi] = mutexAddress;
                while (!stopBargers.IsSet)
                {
                    if (KernelPthreadCompatExports.PthreadMutexLock(context) != 0)
                    {
                        Interlocked.Increment(ref bargerFailures);
                        return;
                    }

                    Interlocked.Increment(ref bargeCount);
                    Thread.SpinWait(50);
                    if (KernelPthreadCompatExports.PthreadMutexUnlock(context) != 0)
                    {
                        Interlocked.Increment(ref bargerFailures);
                        return;
                    }
                }
            }) { IsBackground = true }).ToArray();
            foreach (var barger in bargers)
            {
                barger.Start();
            }

            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(bargerContext));
            Thread.Sleep(50);
            stopBargers.Set();
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)), "the woken waiter never reacquired the mutex");
            Assert.Equal(0, resumeResult);
            // Once it waits on its host signal, unlocks no longer go through the scheduler.
            Assert.Single(recorder.WakeKeys);

            // The waiter owns the mutex now: nobody else can take it until it unlocks.
            var probeContext = new CpuContext(memory, Generation.Gen5);
            probeContext[CpuRegister.Rdi] = mutexAddress;
            Assert.NotEqual(0, KernelPthreadCompatExports.PthreadMutexTrylock(probeContext));
            release.Set();
            Assert.True(resumed.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, unlockResult);
            foreach (var barger in bargers)
            {
                Assert.True(barger.Join(TimeSpan.FromSeconds(10)), "a barger never got the mutex back");
            }

            Assert.Equal(0, bargerFailures);
            Assert.True(Volatile.Read(ref bargeCount) > 0);
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexTrylock(probeContext));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(probeContext));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void ParkedGuestWaiterThatFindsTheMutexFreeAtParkTimeIsNotLeftParked()
    {
        const ulong memoryBase = 0xB_3000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        const ulong ownerThread = 0xB11;
        const ulong waiterThread = 0xB12;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));

        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, RecordingScheduler>();
        var recorder = (RecordingScheduler)(object)scheduler;
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            WithHandoffMode(fifo: false, () =>
                RunAsGuest(ownerThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext))));
            var (blockWaiter, _) = ParkGuestWaiter(memory, mutexAddress, waiterThread);

            // The unlock lands before the scheduler has marked the waiter blocked, so its wake
            // finds nobody; the park recheck must then see the free mutex.
            recorder.OnWake = _ => false;
            RunAsGuest(ownerThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext)));
            Assert.Single(recorder.WakeKeys);
            Assert.True(blockWaiter.TryWake());

            // Another thread locks and unlocks meanwhile: the already-woken head is not woken again.
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexTrylock(otherContext));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(otherContext));
            Assert.Single(recorder.WakeKeys);

            var resumeResult = -1;
            RunAsGuest(waiterThread, () => resumeResult = blockWaiter.Resume());
            Assert.Equal(0, resumeResult);
            var waiterContext = new CpuContext(memory, Generation.Gen5);
            waiterContext[CpuRegister.Rdi] = mutexAddress;
            RunAsGuest(waiterThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(waiterContext)));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void FifoHandoffStillGivesTheMutexToTheParkedHead()
    {
        const ulong memoryBase = 0xB_4000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        const ulong ownerThread = 0xB21;
        const ulong waiterThread = 0xB22;
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x4000);
        var ownerContext = new CpuContext(memory, Generation.Gen5);
        ownerContext[CpuRegister.Rdi] = mutexAddress;
        Assert.True(ownerContext.TryWriteUInt64(mutexAddress, 1));

        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, RecordingScheduler>();
        var recorder = (RecordingScheduler)(object)scheduler;
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            WithHandoffMode(fifo: true, () =>
                RunAsGuest(ownerThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ownerContext))));
            var (blockWaiter, wakeKey) = ParkGuestWaiter(memory, mutexAddress, waiterThread);
            Assert.False(blockWaiter.TryWake());

            recorder.OnWake = key => key == wakeKey && blockWaiter.TryWake();
            RunAsGuest(ownerThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ownerContext)));
            Assert.Equal(new[] { wakeKey }, recorder.WakeKeys);

            // The parked head owns the mutex already; a running thread cannot barge in.
            var bargerContext = new CpuContext(memory, Generation.Gen5);
            bargerContext[CpuRegister.Rdi] = mutexAddress;
            Assert.NotEqual(0, KernelPthreadCompatExports.PthreadMutexTrylock(bargerContext));

            var resumeResult = -1;
            RunAsGuest(waiterThread, () => resumeResult = blockWaiter.Resume());
            Assert.Equal(0, resumeResult);
            var waiterContext = new CpuContext(memory, Generation.Gen5);
            waiterContext[CpuRegister.Rdi] = mutexAddress;
            RunAsGuest(waiterThread, () => Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(waiterContext)));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexTrylock(bargerContext));
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(bargerContext));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    // Runs scePthreadMutexLock as a guest thread inside an import call so the waiter spins and
    // then requests a cooperative block, and returns the staged waiter as the scheduler sees it.
    private static (IGuestThreadBlockWaiter Waiter, string WakeKey) ParkGuestWaiter(
        PthreadMutexSemanticsTests.AllocatingCpuMemory memory,
        ulong mutexAddress,
        ulong threadHandle)
    {
        IGuestThreadBlockWaiter? staged = null;
        var stagedKey = string.Empty;
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = mutexAddress;
        RunAsGuest(threadHandle, () =>
        {
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(context));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out var wakeKey,
                out var waiter));
            Assert.Equal("pthread_mutex_lock", reason);
            Assert.True(hasContinuation);
            staged = waiter;
            stagedKey = wakeKey;
        });
        return (Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(staged), stagedKey);
    }

    private static void WithHandoffMode(bool fifo, Action action)
    {
        var previous = KernelPthreadCompatExports.DefaultMutexFifoHandoff;
        KernelPthreadCompatExports.DefaultMutexFifoHandoff = fifo;
        try
        {
            action();
        }
        finally
        {
            KernelPthreadCompatExports.DefaultMutexFifoHandoff = previous;
        }
    }

    private static void RunAsGuest(ulong threadHandle, Action action)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000 + threadHandle,
            resumeRsp: 0x2_0000 + threadHandle,
            returnSlotAddress: 0x3_0000 + threadHandle);
        try
        {
            action();
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    public class RecordingScheduler : DispatchProxy
    {
        private readonly object _gate = new();
        private readonly List<string> _wakeKeys = new();

        public Func<string, bool>? OnWake { get; set; }

        public int WakesGranted { get; private set; }

        public string[] WakeKeys
        {
            get
            {
                lock (_gate)
                {
                    return _wakeKeys.ToArray();
                }
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGuestThreadScheduler.WakeBlockedThreads))
            {
                var key = (string)args![0]!;
                lock (_gate)
                {
                    _wakeKeys.Add(key);
                }

                // Like the real scheduler, consult the blocked thread's waiter under the wake.
                if (OnWake?.Invoke(key) == true)
                {
                    WakesGranted++;
                    return 1;
                }

                return 0;
            }

            var returnType = targetMethod?.ReturnType;
            return returnType is null || returnType == typeof(void) || !returnType.IsValueType
                ? null
                : Activator.CreateInstance(returnType);
        }
    }
}

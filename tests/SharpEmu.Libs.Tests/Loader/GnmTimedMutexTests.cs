// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.GnmMode;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Pthread;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GnmTimedMutexTests
{
    private static long _nextMemory;
    private static readonly SysAbiFunction Timedlock =
        GnmExportRegistry.CreateExports().Single(export => export.Nid == "IafI2PxcPnQ").Function;

    [Fact]
    public void TimedMutexExportIsGnmOnly()
    {
        var export = Assert.Single(GnmExportRegistry.CreateExports(), export => export.Nid == "IafI2PxcPnQ");
        Assert.Equal("scePthreadMutexTimedlock", export.Name);
        Assert.Equal(Generation.Gen4, export.Target);
        Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            export => export.Nid == "IafI2PxcPnQ");
    }

    [Fact]
    public void RelativeMicrosecondsAreAnIntegerAndTimeoutDoesNotAcquireTheMutex()
    {
        using var mutex = new MutexFixture();
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexLock(mutex.Context)));
        mutex.Context[CpuRegister.Rsi] = 100;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            AsThread(0xF101, () => Timedlock(mutex.Context)));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
            AsThread(0xF102, () => KernelPthreadCompatExports.PthreadMutexTrylock(mutex.Context)));
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
        mutex.Context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, AsThread(0xF101, () => Timedlock(mutex.Context)));
        Assert.Equal(0, AsThread(0xF101, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
    }

    [Fact]
    public void CooperativeWaitReservesOwnershipForTheWaitingGuest()
    {
        using var mutex = new MutexFixture();
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexLock(mutex.Context)));
        var (waiter, _) = Park(mutex.Context, 5_000_000);
        Assert.False(waiter.TryWake());
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
        Assert.True(waiter.TryWake());
        Assert.Equal(0, AsThread(0xF101, waiter.Resume));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
            AsThread(0xF102, () => KernelPthreadCompatExports.PthreadMutexTrylock(mutex.Context)));
        Assert.Equal(0, AsThread(0xF101, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
    }

    [Fact]
    public void DeadlineResumeRemovesTheExpiredWaiterBeforeTheNextLock()
    {
        using var mutex = new MutexFixture();
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexLock(mutex.Context)));
        var (waiter, deadline) = Park(mutex.Context, 20_000);
        Assert.True(SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() >= deadline, TimeSpan.FromSeconds(1)));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, AsThread(0xF101, waiter.Resume));
        Assert.True(waiter.TryWake());
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
        Assert.Equal(0, AsThread(0xF102, () => KernelPthreadCompatExports.PthreadMutexLock(mutex.Context)));
        Assert.Equal(0, AsThread(0xF102, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
    }

    [Fact]
    public void RecursiveTimedAcquisitionStillRequiresBothUnlocks()
    {
        using var mutex = new MutexFixture(type: 2);
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexLock(mutex.Context)));
        mutex.Context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, AsThread(0xF100, () => Timedlock(mutex.Context)));
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            AsThread(0xF101, () => Timedlock(mutex.Context)));
        Assert.Equal(0, AsThread(0xF100, () => KernelPthreadCompatExports.PthreadMutexUnlock(mutex.Context)));
    }

    private static (IGuestThreadBlockWaiter Waiter, long Deadline) Park(CpuContext context, ulong microseconds)
    {
        IGuestThreadBlockWaiter? waiter = null;
        long deadline = 0;
        AsThread(0xF101, () =>
        {
            var previous = GuestThreadExecution.EnterImportCallFrame(0x12345678, 0x12345000, 0x12344FF8);
            try
            {
                context[CpuRegister.Rsi] = microseconds;
                Assert.Equal(0, Timedlock(context));
                Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _, out _, out var continuation,
                    out _, out waiter, out deadline));
                Assert.True(continuation);
                Assert.True(deadline > Stopwatch.GetTimestamp());
                return 0;
            }
            finally { GuestThreadExecution.RestoreImportCallFrame(previous); }
        });
        return (Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter), deadline);
    }

    private static int AsThread(ulong handle, Func<int> action)
    {
        var previous = GuestThreadExecution.EnterGuestThread(handle);
        try { return action(); }
        finally { GuestThreadExecution.RestoreGuestThread(previous); }
    }

    private sealed class MutexFixture : IDisposable
    {
        public CpuContext Context { get; }
        private readonly ulong _mutex;
        private readonly ulong _attribute;

        public MutexFixture(int type = 3)
        {
            var address = 0x7_8000_0000UL + (ulong)Interlocked.Increment(ref _nextMemory) * 0x10000;
            Context = new CpuContext(new PthreadMutexSemanticsTests.AllocatingCpuMemory(address, 0x4000), Generation.Gen4);
            _attribute = address + 0x100;
            _mutex = address + 0x200;
            Context[CpuRegister.Rdi] = _attribute;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrInit(Context));
            Context[CpuRegister.Rsi] = (ulong)type;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrSettype(Context));
            Context[CpuRegister.Rdi] = _mutex;
            Context[CpuRegister.Rsi] = _attribute;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(Context));
        }

        public void Dispose()
        {
            Context[CpuRegister.Rdi] = _mutex;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexDestroy(Context));
            Context[CpuRegister.Rdi] = _attribute;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrDestroy(Context));
        }
    }
}

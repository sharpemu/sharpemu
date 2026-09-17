// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class RegionLockTests
{
    private sealed class LockFailureException(string message) : Exception(message);

    [Theory]
    [InlineData(2, RegionLock.Category.MemoryTracker)]
    [InlineData(8, RegionLock.Category.MemoryTracker)]
    [InlineData(2, RegionLock.Category.ImageCache)]
    [InlineData(8, RegionLock.Category.ImageCache)]
    public async Task ContendingThreadsPublishCompleteUpdatesAndAllFinish(int workerCount, RegionLock.Category category)
    {
        GpuMemoryAccessProfile.Initialize();
        var regionLock = new RegionLock(category);
        var before = GpuMemoryAccessProfile.Snapshot();
        using var start = new ManualResetEventSlim();
        var sequence = 0;
        var complement = ~sequence;
        const int iterations = 5000;
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            start.Wait();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                using var held = regionLock.Hold();
                Assert.Equal(~sequence, complement);
                sequence++;
                Thread.SpinWait(8);
                complement = ~sequence;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        start.Set();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
        var after = GpuMemoryAccessProfile.Snapshot();
        var holdOperation = category == RegionLock.Category.ImageCache
            ? GpuMemoryAccessProfile.Operation.ImageCacheLockHold : GpuMemoryAccessProfile.Operation.TrackerLockHold;
        var waitOperation = category == RegionLock.Category.ImageCache
            ? GpuMemoryAccessProfile.Operation.ImageCacheLockWait : GpuMemoryAccessProfile.Operation.TrackerLockWait;
        Assert.Equal(GpuMemoryAccessProfile.Enabled ? workerCount * iterations : 0,
            after[(int)holdOperation].Calls - before[(int)holdOperation].Calls);
        Assert.Equal(after[(int)waitOperation].Calls - before[(int)waitOperation].Calls,
            after[(int)GpuMemoryAccessProfile.Operation.RegionLockWait].Calls - before[(int)GpuMemoryAccessProfile.Operation.RegionLockWait].Calls);
        Assert.Equal(after[(int)waitOperation].Ticks - before[(int)waitOperation].Ticks,
            after[(int)GpuMemoryAccessProfile.Operation.RegionLockWait].Ticks - before[(int)GpuMemoryAccessProfile.Operation.RegionLockWait].Ticks);
        using var finalRead = regionLock.Hold();
        Assert.Equal(workerCount * iterations, sequence);
        Assert.Equal(~sequence, complement);
    }

    [Fact]
    public void RecursionAndForeignReleaseKeepTheOriginalOwner()
    {
        var previousFatal = PageGuard.OnFatal;
        PageGuard.OnFatal = message => throw new LockFailureException(message);
        var regionLock = new RegionLock();
        try
        {
            regionLock.Enter();
            try
            {
                Assert.Throws<LockFailureException>(regionLock.Enter);
                Exception? foreignFailure = null;
                var foreignThread = new Thread(() =>
                {
                    try { regionLock.Exit(); }
                    catch (Exception failure) { foreignFailure = failure; }
                });
                foreignThread.Start();
                Assert.True(foreignThread.Join(TimeSpan.FromSeconds(5)));
                Assert.IsType<LockFailureException>(foreignFailure);
                Assert.Throws<LockFailureException>(regionLock.Enter);
            }
            finally
            {
                regionLock.Exit();
            }
            using var reacquired = regionLock.Hold();
        }
        finally
        {
            PageGuard.OnFatal = previousFatal;
        }
    }

    [Theory]
    [InlineData(RegionLock.Category.MemoryTracker)]
    [InlineData(RegionLock.Category.ImageCache)]
    public void UncontendedAcquisitionDoesNotAllocate(RegionLock.Category category)
    {
        var regionLock = new RegionLock(category);
        using var fault = GpuMemoryAccessProfile.MeasureFault(FaultKind.Write);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            using var held = regionLock.Hold();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            using var held = regionLock.Hold();
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

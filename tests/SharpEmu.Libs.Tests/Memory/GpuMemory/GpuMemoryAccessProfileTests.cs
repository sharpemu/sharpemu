// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class GpuMemoryAccessProfileTests
{
    [Fact]
    public void CountersAccumulateConcurrentCallsWithoutResettingReports()
    {
        var counters = new GpuMemoryAccessProfile.Measurements();
        Parallel.For(0, 10000, _ => counters.Record(GpuMemoryAccessProfile.Operation.HostProtection, 7, 4096));
        var snapshot = counters.Snapshot();
        Assert.Equal(new GpuMemoryAccessProfile.Measurement(10000, 70000, 7, 40960000),
            snapshot[(int)GpuMemoryAccessProfile.Operation.HostProtection]);
        Assert.Equal(snapshot, counters.Snapshot());
    }

    [Fact]
    public void ReportRequiresSecondarySwitchAndSeparatesFaultProtection()
    {
        GpuMemoryAccessProfile.Initialize();
        using var output = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(output);
            using (GpuMemoryAccessProfile.MeasureFault(FaultKind.Write))
            {
                using var protection = GpuMemoryAccessProfile.MeasureAccess(
                    GpuMemoryAccessProfile.Operation.HostProtection, GpuMemoryAccessProfile.Operation.FaultHostProtection, 4096);
            }
            using (GpuMemoryAccessProfile.MeasureAccess(
                GpuMemoryAccessProfile.Operation.HostProtection, GpuMemoryAccessProfile.Operation.FaultHostProtection, 4096)) { }
            GpuMemoryAccessProfile.WriteReport();
        }
        finally
        {
            Console.SetError(previous);
        }

        var enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE") == "1" &&
            (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ||
             Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1");
        Assert.Equal(enabled, GpuMemoryAccessProfile.Enabled);
        if (enabled)
        {
            Assert.Contains("operation=WriteFault cumulative=1", output.ToString());
            Assert.Contains("operation=FaultHostProtection cumulative=1", output.ToString());
            Assert.Contains("operation=HostProtection cumulative=1", output.ToString());
        }
        else
            Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void FaultMeasurementDoesNotAllocateAfterInitialization()
    {
        GpuMemoryAccessProfile.Initialize();
        for (var index = 0; index < 100; index++) MeasureFault();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++) MeasureFault();
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void FirstFaultDoesNotAllocateAfterGuestThreadEntry()
    {
        GpuMemoryAccessProfile.Initialize();
        MeasureFault();
        long allocated = -1;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var previous = GuestThreadExecution.EnterGuestThread(1);
            try
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                MeasureFault();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            catch (Exception exception) { failure = exception; }
            finally { GuestThreadExecution.RestoreGuestThread(previous); }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.Equal(0, allocated);
    }

    [Theory]
    [InlineData(FaultKind.Write, GpuMemoryAccessProfile.Operation.WriteFault)]
    [InlineData(FaultKind.Read, GpuMemoryAccessProfile.Operation.ReadFault)]
    [InlineData(FaultKind.Unknown, GpuMemoryAccessProfile.Operation.UnknownFault)]
    public void FaultMeasurementsKeepTheirAccessKind(FaultKind kind, GpuMemoryAccessProfile.Operation expected)
    {
        var before = GpuMemoryAccessProfile.Snapshot();
        using (GpuMemoryAccessProfile.MeasureFault(kind)) { }
        var after = GpuMemoryAccessProfile.Snapshot();
        foreach (var operation in new[]
        {
            GpuMemoryAccessProfile.Operation.WriteFault,
            GpuMemoryAccessProfile.Operation.ReadFault,
            GpuMemoryAccessProfile.Operation.UnknownFault,
        })
            Assert.Equal(GpuMemoryAccessProfile.Enabled && operation == expected ? 1 : 0,
                after[(int)operation].Calls - before[(int)operation].Calls);
    }

    [Fact]
    public void ThreadInitializationPreservesNestedFaultContext()
    {
        var before = GpuMemoryAccessProfile.Snapshot();
        using (GpuMemoryAccessProfile.MeasureFault(FaultKind.Write))
        {
            using (GpuMemoryAccessProfile.MeasureFault(FaultKind.Read))
            {
                GpuMemoryAccessProfile.InitializeCurrentThread();
                GpuMemoryAccessProfile.CountImageCpuWrite();
            }
            GpuMemoryAccessProfile.CountImageCpuWrite();
        }
        GpuMemoryAccessProfile.CountImageCpuWrite();
        var after = GpuMemoryAccessProfile.Snapshot();
        Assert.Equal(GpuMemoryAccessProfile.Enabled ? 2 : 0,
            after[(int)GpuMemoryAccessProfile.Operation.FaultImageCpuWrite].Calls -
            before[(int)GpuMemoryAccessProfile.Operation.FaultImageCpuWrite].Calls);
        Assert.Equal(GpuMemoryAccessProfile.Enabled ? 1 : 0,
            after[(int)GpuMemoryAccessProfile.Operation.ImageCpuWrite].Calls -
            before[(int)GpuMemoryAccessProfile.Operation.ImageCpuWrite].Calls);
    }

    [Theory]
    [InlineData(RegionLock.Category.MemoryTracker, false, GpuMemoryAccessProfile.Operation.TrackerLockHold)]
    [InlineData(RegionLock.Category.MemoryTracker, true, GpuMemoryAccessProfile.Operation.FaultTrackerLockHold)]
    [InlineData(RegionLock.Category.ImageCache, false, GpuMemoryAccessProfile.Operation.ImageCacheLockHold)]
    [InlineData(RegionLock.Category.ImageCache, true, GpuMemoryAccessProfile.Operation.FaultImageCacheLockHold)]
    public void LockHoldUsesItsCategoryAndAcquisitionContext(
        RegionLock.Category category, bool inFault, GpuMemoryAccessProfile.Operation expected)
    {
        var regionLock = new RegionLock(category);
        var before = GpuMemoryAccessProfile.Snapshot();
        using (inFault ? GpuMemoryAccessProfile.MeasureFault(FaultKind.Write) : default)
            regionLock.Enter();
        regionLock.Exit();
        var after = GpuMemoryAccessProfile.Snapshot();
        foreach (var operation in new[]
        {
            GpuMemoryAccessProfile.Operation.TrackerLockHold,
            GpuMemoryAccessProfile.Operation.FaultTrackerLockHold,
            GpuMemoryAccessProfile.Operation.ImageCacheLockHold,
            GpuMemoryAccessProfile.Operation.FaultImageCacheLockHold,
            GpuMemoryAccessProfile.Operation.RegionLockWait,
            GpuMemoryAccessProfile.Operation.FaultRegionLockWait,
        })
        {
            var calls = GpuMemoryAccessProfile.Enabled && operation == expected ? 1 : 0;
            Assert.Equal(calls, after[(int)operation].Calls - before[(int)operation].Calls);
            Assert.True(after[(int)operation].Ticks >= before[(int)operation].Ticks);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockWaitCategoriesPreserveCombinedTotals(bool inFault)
    {
        if (!GpuMemoryAccessProfile.Enabled) return;
        var before = GpuMemoryAccessProfile.Snapshot();
        using (inFault ? GpuMemoryAccessProfile.MeasureFault(FaultKind.Write) : default)
        {
            GpuMemoryAccessProfile.RecordLockWait(RegionLock.Category.MemoryTracker, 7);
            GpuMemoryAccessProfile.RecordLockWait(RegionLock.Category.ImageCache, 11);
        }
        var after = GpuMemoryAccessProfile.Snapshot();
        var tracker = inFault ? GpuMemoryAccessProfile.Operation.FaultTrackerLockWait : GpuMemoryAccessProfile.Operation.TrackerLockWait;
        var image = inFault ? GpuMemoryAccessProfile.Operation.FaultImageCacheLockWait : GpuMemoryAccessProfile.Operation.ImageCacheLockWait;
        var combined = inFault ? GpuMemoryAccessProfile.Operation.FaultRegionLockWait : GpuMemoryAccessProfile.Operation.RegionLockWait;
        Assert.Equal(1, after[(int)tracker].Calls - before[(int)tracker].Calls);
        Assert.Equal(7, after[(int)tracker].Ticks - before[(int)tracker].Ticks);
        Assert.Equal(1, after[(int)image].Calls - before[(int)image].Calls);
        Assert.Equal(11, after[(int)image].Ticks - before[(int)image].Ticks);
        Assert.Equal(2, after[(int)combined].Calls - before[(int)combined].Calls);
        Assert.Equal(18, after[(int)combined].Ticks - before[(int)combined].Ticks);
    }

    private static void MeasureFault()
    {
        using var fault = GpuMemoryAccessProfile.MeasureFault(FaultKind.Write);
        using var protection = GpuMemoryAccessProfile.MeasureAccess(
            GpuMemoryAccessProfile.Operation.HostProtection, GpuMemoryAccessProfile.Operation.FaultHostProtection, 4096);
        MeasureProtectionDetails();
    }

    private static void MeasureProtectionDetails()
    {
        using (GpuMemoryAccessProfile.MeasureAddressSpaceProtectionWait()) { }
        using (GpuMemoryAccessProfile.MeasureBackingProtectionWait()) { }
        using (GpuMemoryAccessProfile.MeasureHostProtectionCall(4096)) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtectionDetailsRequireSecondarySwitchAndKeepFaultContext(bool inFault)
    {
        var before = GpuMemoryAccessProfile.Snapshot();
        using (inFault ? GpuMemoryAccessProfile.MeasureFault(FaultKind.Write) : default)
            MeasureProtectionDetails();
        var after = GpuMemoryAccessProfile.Snapshot();
        foreach (var (regular, fault) in new[]
        {
            (GpuMemoryAccessProfile.Operation.AddressSpaceProtectionWait, GpuMemoryAccessProfile.Operation.FaultAddressSpaceProtectionWait),
            (GpuMemoryAccessProfile.Operation.BackingProtectionWait, GpuMemoryAccessProfile.Operation.FaultBackingProtectionWait),
            (GpuMemoryAccessProfile.Operation.HostProtectionCall, GpuMemoryAccessProfile.Operation.FaultHostProtectionCall),
        })
        {
            Assert.Equal(GpuMemoryAccessProfile.Enabled && !inFault ? 1 : 0,
                after[(int)regular].Calls - before[(int)regular].Calls);
            Assert.Equal(GpuMemoryAccessProfile.Enabled && inFault ? 1 : 0,
                after[(int)fault].Calls - before[(int)fault].Calls);
        }
        var expectedBytes = GpuMemoryAccessProfile.Enabled ? 4096 : 0;
        var call = inFault ? GpuMemoryAccessProfile.Operation.FaultHostProtectionCall : GpuMemoryAccessProfile.Operation.HostProtectionCall;
        Assert.Equal(expectedBytes, after[(int)call].Bytes - before[(int)call].Bytes);
    }

    [Fact]
    public void ImageCallerCountsAreGatedAndSeparateFaultWrites()
    {
        var before = GpuMemoryAccessProfile.Snapshot();
        GpuMemoryAccessProfile.CountImageCpuWrite();
        using (GpuMemoryAccessProfile.MeasureFault(FaultKind.Write)) GpuMemoryAccessProfile.CountImageCpuWrite();
        GpuMemoryAccessProfile.CountImageQuery(gpuDirtyOnly: false);
        GpuMemoryAccessProfile.CountImageQuery(gpuDirtyOnly: true);
        var after = GpuMemoryAccessProfile.Snapshot();
        foreach (var operation in new[]
        {
            GpuMemoryAccessProfile.Operation.ImageCpuWrite,
            GpuMemoryAccessProfile.Operation.FaultImageCpuWrite,
            GpuMemoryAccessProfile.Operation.ImageRegionQuery,
            GpuMemoryAccessProfile.Operation.ImageGpuDirtyQuery,
        })
        {
            Assert.Equal(GpuMemoryAccessProfile.Enabled ? 1 : 0, after[(int)operation].Calls - before[(int)operation].Calls);
            Assert.Equal(before[(int)operation].Ticks, after[(int)operation].Ticks);
        }
    }
}

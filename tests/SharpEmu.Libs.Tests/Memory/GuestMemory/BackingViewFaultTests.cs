// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[Collection(GuestMemoryStateCollection.Name)]
public sealed class BackingViewFaultTests
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private const string WorkerEnvironmentVariable = "SHARPEMU_BACKING_VIEW_FAULT_WORKER";

    [Fact]
    public void PartialUnmapRestoresTheSurvivorsBeforeFaultRecovery()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        using var manager = new GuestGpuMemory(memory);
        Assert.True(memory.TryHoldRangeAtOrAbove(0x2_0000_0000, 4 * Segment, host.Granularity, out var address));
        Assert.True(memory.TryMapBacked(address, 4 * Segment, 0, ReadWrite, out _));
        manager.Register(address, 4 * Segment, ReadWrite);
        Assert.True(memory.TryWriteBacking(address, BitConverter.GetBytes(Marker)));
        Assert.False(manager.TryResolveFault(FaultKind.Read, address));

        var observedGap = false;
        host.AfterUnmapView = (removedAddress, removedSize) =>
        {
            Assert.Equal(address, removedAddress);
            Assert.Equal(4 * Segment, removedSize);
            Assert.True(PlatformMemory.Query(address, out var region));
            Assert.NotEqual(HostRegionState.Committed, region.State);
            observedGap = true;
        };
        manager.Unregister(address + Segment, Segment);
        Assert.True(memory.TryUnmapBacked(address + Segment, Segment));
        host.AfterUnmapView = null;

        Assert.True(observedGap);
        Assert.True(manager.TryResolveFault(FaultKind.Read, address), "The restored survivor must permit the failed read to retry.");
        Assert.True(manager.TryResolveFault(FaultKind.Write, address + 2 * Segment));
        Assert.False(manager.TryResolveFault(FaultKind.Read, address + Segment));
        Assert.False(manager.TryResolveFault(FaultKind.Execute, address));
        Assert.False(manager.TryResolveFault(FaultKind.Unknown, address));
        var bytes = new byte[8];
        Assert.True(memory.TryReadBacking(address, bytes));
        Assert.Equal(Marker, BitConverter.ToUInt64(bytes));

        // Host access alone must not bypass a pending GPU readback.
        manager.Pages.AddWatch(address, Segment, blockReads: true);
        Assert.True(memory.TryProtect(address, Segment, ReadWrite));
        Assert.False(manager.TryResolveFault(FaultKind.Read, address));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        manager.Pages.RemoveWatch(address, Segment, blockReads: true);

        Assert.True(manager.NoteProtected(address, Segment, GuestPageProtection.None));
        Assert.False(manager.TryResolveFault(FaultKind.Read, address));
        Assert.True(manager.NoteProtected(address, Segment, GuestPageProtection.Read));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        Assert.True(manager.TryResolveFault(FaultKind.Read, address));
        Assert.True(memory.TryProtect(address, Segment, GuestPageProtection.None));
        Assert.False(manager.TryResolveFault(FaultKind.Read, address));

        Assert.True(manager.NoteProtected(address, Segment, ReadWrite));
        Assert.True(memory.TryProtect(address, Segment, GuestPageProtection.Read));
        Assert.False(manager.TryResolveFault(FaultKind.Write, address));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(PlatformMemory.ProtectRaw(address, Segment, 0x104, out _));
            Assert.False(manager.TryResolveFault(FaultKind.Read, address));
        }

        manager.Unregister(address, Segment);
        Assert.True(memory.TryUnmapBacked(address, Segment));
        Assert.True(memory.TryMapBacked(address, Segment, 0, ReadWrite, out _));
        manager.Register(address, Segment, ReadWrite);
        Assert.False(manager.TryResolveFault(FaultKind.Read, address));
    }

    [Fact]
    public void FailedPartialUnmapAllowsAccessAfterTheOriginalViewIsRestored()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var memory = new PhysicalVirtualMemory(viewHost: host, backingBytes: BackingSize);
        using var manager = new GuestGpuMemory(memory);
        Assert.True(memory.TryHoldRangeAtOrAbove(0x2_0000_0000, 4 * Segment, host.Granularity, out var address));
        Assert.True(memory.TryMapBacked(address, 4 * Segment, 0, ReadWrite, out _));
        manager.Register(address, 4 * Segment, ReadWrite);
        Assert.True(memory.TryWriteBacking(address, BitConverter.GetBytes(Marker)));

        host.FailNext(FailingHostViews.Op.MapView);
        Assert.False(memory.TryUnmapBacked(address + Segment, Segment));
        Assert.True(memory.IsBackedRange(address, 4 * Segment));
        Assert.True(manager.TryResolveFault(FaultKind.Read, address));
        Assert.True(manager.TryResolveFault(FaultKind.Write, address + Segment));
        Assert.True(manager.TryResolveFault(FaultKind.Read, address + 3 * Segment));
        var bytes = new byte[8];
        Assert.True(memory.TryReadBacking(address, bytes));
        Assert.Equal(Marker, BitConverter.ToUInt64(bytes));
    }

    [Fact]
    public async Task NativeGuestAccessWaitsForPartialViewRestoration()
    {
        if (!GuestFaultWorker.CanRun) return;
        if (!GuestFaultWorker.IsWorker(WorkerEnvironmentVariable))
        {
            await GuestFaultWorker.RunIsolatedAsync(WorkerEnvironmentVariable,
                typeof(BackingViewFaultTests), nameof(NativeGuestAccessWaitsForPartialViewRestoration));
            return;
        }

        using var vulkan = HeadlessVulkan.TryCreate();
        if (!GatePrerequisites.Ready(vulkan))
        {
            GuestFaultWorker.Report(GuestFaultWorker.Skipped, "no Vulkan device");
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var harness = new CacheHarness(vulkan, viewHost: host);
        using var fatal = new FatalScope();
        GuestGpuMemoryHook.Attach(harness.Gpu);
        var signals = PlatformMemory.Allocate(0, 0x1000, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, signals);
        try
        {
            using var guest = new SyntheticGuest(harness, "backing-view-fault-worker");
            var mappingLock = Assert.IsType<ReaderWriterLockSlim>(typeof(PhysicalVirtualMemory)
                .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Memory));
            foreach (var write in new[] { false, true })
            {
                var address = harness.MapBacked(4 * Segment, ReadWrite);
                var accessAddress = write ? address + 2 * Segment : address;
                Assert.True(harness.Memory.TryWriteBacking(accessAddress, BitConverter.GetBytes(Marker)));
                harness.Gpu.Unregister(address + Segment, Segment);
                harness.Gpu.AttachStores(null, null);
                WriteSignal(signals, 0);
                WriteSignal(signals + 4, 0);
                var access = Task.Run(() => guest.AccessAfterSignal(write, accessAddress, ~Marker, signals, signals + 4));
                var observedFaultWait = false;
                host.AfterUnmapView = (removedAddress, removedSize) =>
                {
                    Assert.Equal(address, removedAddress);
                    Assert.Equal(4 * Segment, removedSize);
                    WriteSignal(signals + 4, 1);
                    observedFaultWait = SpinWait.SpinUntil(() => mappingLock.WaitingReadCount != 0, TimeSpan.FromSeconds(10));
                };

                try
                {
                    Assert.True(SpinWait.SpinUntil(() => ReadSignal(signals) == 1, TimeSpan.FromSeconds(10)),
                        "The native guest did not reach the access barrier.");
                    Assert.True(harness.Memory.TryUnmapBacked(address + Segment, Segment));
                    Assert.True(observedFaultWait, "The guest fault did not wait for the mapping lock.");
                    var result = await access.WaitAsync(TimeSpan.FromSeconds(10));
                    if (!write) Assert.Equal(Marker, result);
                    Assert.Equal(write ? ~Marker : Marker, guest.Read(accessAddress));
                }
                finally
                {
                    WriteSignal(signals + 4, 1);
                    await access.WaitAsync(TimeSpan.FromSeconds(10));
                    host.AfterUnmapView = null;
                    harness.Gpu.AttachStores(harness.Cache, harness.Images);
                }
            }

            Assert.Contains("faults_resolved=2 ", GuestGpuMemoryHook.GetSummary());
            harness.Shutdown();
            GuestFaultWorker.Report(GuestFaultWorker.Completed, GuestGpuMemoryHook.GetSummary());
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
            Assert.True(PlatformMemory.Free(signals));
        }
    }

    private static unsafe int ReadSignal(ulong address) => Volatile.Read(ref *(int*)address);

    private static unsafe void WriteSignal(ulong address, int value) => Volatile.Write(ref *(int*)address, value);
}

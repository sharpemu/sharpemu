// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// Guest code on a guest stack is the only context whose faults reach the store; a child
// process runs it so the host fault handler is the real one.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferFaultTests
{
    private const string WorkerEnvironmentVariable = "SHARPEMU_BUFFER_FAULT_WORKER";
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;

    [Fact]
    public async Task GuestFaultsRecoverThroughTheBufferStore()
    {
        if (!GuestFaultWorker.CanRun)
        {
            return;
        }

        if (GuestFaultWorker.IsWorker(WorkerEnvironmentVariable))
        {
            RunGuest();
            return;
        }

        await GuestFaultWorker.RunIsolatedAsync(WorkerEnvironmentVariable, typeof(GuestBufferFaultTests), nameof(GuestFaultsRecoverThroughTheBufferStore));
    }

    private static void RunGuest()
    {
        using var vulkan = HeadlessVulkan.TryCreate();
        if (!GatePrerequisites.Ready(vulkan))
        {
            GuestFaultWorker.Report(GuestFaultWorker.Skipped, "no Vulkan device");
            return;
        }

        using var harness = new CacheHarness(vulkan);
        using var fatal = new FatalScope();
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            using var guest = new SyntheticGuest(harness, "buffer-fault-worker");
            ulong GuestRead(ulong address) => guest.Read(address);
            void GuestWrite(ulong address, ulong value) => guest.Write(address, value);

            // Check write recovery before the first GPU download.
            var probe = harness.MapBacked(0x10000, ReadWrite);
            harness.Worker.Run(() => harness.Cache.ObtainBuffer(probe, 0x4100, isWritten: false));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(probe));
            GuestWrite(probe + 0x30, 0x1234);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(probe));
            Assert.Equal(0x1234UL, GuestRead(probe + 0x30));
            Assert.Contains("faults_resolved=1 ", GuestGpuMemoryHook.GetSummary());

            // Read fault: the GPU result is not readable until the fault downloads it.
            var backed = harness.MapBacked(0x10000, ReadWrite);
            harness.Worker.Run(() =>
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(backed, 0x100, isWritten: true);
                buffer.Fill(offset, 0x100, 0x11223344);
            });
            Assert.Equal(HostPageProtection.NoAccess, harness.Protection(backed));
            Assert.Equal(0x1122334411223344UL, GuestRead(backed + 8));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(backed));
            Assert.Contains("faults_resolved=2 ", GuestGpuMemoryHook.GetSummary());

            // Write fault: the page turns CPU-dirty, the write lands, and the next obtain re-uploads it.
            GuestWrite(backed + 0x10, 0xDEADBEEF);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(backed));
            Assert.Equal(0xDEADBEEFUL, GuestRead(backed + 0x10));
            Assert.Contains("faults_resolved=3 ", GuestGpuMemoryHook.GetSummary());
            var (reuploaded, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(backed, 0x4100, isWritten: false));
            Assert.Equal(0xDEADBEEFUL, BitConverter.ToUInt64(harness.ReadBack(reuploaded, 0x10, 8)));
            Assert.Equal(0x11223344u, BitConverter.ToUInt32(harness.ReadBack(reuploaded, 0x20, 4)));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(backed));

            // Private memory uploads on use and faults on writes; a GPU write into it is refused.
            var privateRange = harness.MapPrivate(0x10000);
            GuestWrite(privateRange + 0x20, 0x5555AAAA);
            var (privateBuffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(privateRange, 0x4100, isWritten: false));
            Assert.Equal(0x5555AAAAUL, BitConverter.ToUInt64(harness.ReadBack(privateBuffer, 0x20, 8)));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(privateRange));
            GuestWrite(privateRange + 0x28, 1);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(privateRange));
            Assert.Contains("faults_resolved=4 ", GuestGpuMemoryHook.GetSummary());
            harness.Worker.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Cache.ObtainBuffer(privateRange, 0x100, isWritten: true)));

            // Unrelated faults are declined at the hook the fault handler calls.
            var noAccess = harness.MapBacked(0x10000, GuestPageProtection.None);
            Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Read, noAccess + 8));
            Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Write, 0x3_0000_0000));
            Assert.Contains("faults_declined=2", GuestGpuMemoryHook.GetSummary());

            // Two guest threads fault on the same GPU-dirty page.
            // Store synchronization must let both resume after the download.
            var raced = harness.MapBacked(0x10000, ReadWrite);
            harness.Worker.Run(() =>
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(raced, 0x100, isWritten: true);
                buffer.Fill(offset, 0x100, 0x77777777);
            });
            var outcomes = guest.Race(2, write: false, raced + 8, _ => 0);
            Assert.All(outcomes, outcome => Assert.True(outcome.Ok, outcome.Error));
            Assert.All(outcomes, outcome => Assert.Equal(0x7777777777777777UL, outcome.Value));
            Assert.Contains("faults_resolved=6 ", GuestGpuMemoryHook.GetSummary());
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(raced));

            harness.Shutdown();
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(backed));
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(privateRange));
            Assert.Equal(HostPageProtection.NoAccess, harness.Protection(noAccess));
            GuestFaultWorker.Report(GuestFaultWorker.Completed, GuestGpuMemoryHook.GetSummary());
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }
}

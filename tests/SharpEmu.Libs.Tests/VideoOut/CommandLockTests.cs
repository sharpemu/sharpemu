// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class CommandLockTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void OverflowDrainsInStreamOrder()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan) || !OperatingSystem.IsWindows()) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan);
        var thread = CommandStreamStoreIndexTests.NewThread(presenter);
        var type = thread.GetType();
        var post = type.GetMethod("EnqueueDeferred")!;
        var drain = type.GetMethod("RefillDraining", PresenterUnderTest.InstanceMembers)!;
        var batch = type.GetField("_draining", PresenterUnderTest.InstanceMembers)!;
        var seen = new List<long>();
        for (var i = 0; i < 620; i++)
        {
            if (i is 267 or 358)
                type.GetMethod("Post")!.Invoke(thread, [(Action)(() => { }), "test"]);
            else
                Assert.Equal(i + 1L, post.Invoke(thread, [AgcExports.GlobalBarrierWork.Instance, false]));
            if (i is 340 or 355) Drain();
        }
        Drain();
        Assert.Equal(Enumerable.Range(1, 620).Select(i => (long)i), seen);

        void Drain()
        {
            while ((bool)drain.Invoke(thread, null)!)
                foreach (var item in (IEnumerable)batch.GetValue(thread)!)
                    seen.Add((long)item.GetType().GetField("Sequence")!.GetValue(item)!);
        }
    }

    [Theory]
    [InlineData(0x1004, 4, 0x1000, 4, true)]
    [InlineData(0x2002, 4, 0x1000, 4, false)]
    [InlineData(0x1FFE, 8, 0x3000, 2, false)]
    public void DisjointStoreProofIncludesPhysicalAliases(int address, int length, int store, int bytes, bool disjoint) =>
        Assert.Equal(disjoint, CommandStoreLanding.DisjointOffsets((ulong)address, length, (ulong)store, bytes));

    [Fact]
    public async Task AnUnrelatedOverlayReadsWhileTheGuestStoreIsReserved()
    {
        if (!GatePrerequisites.Ready(fixture.Vulkan) || !OperatingSystem.IsWindows()) return;
        using var presenter = new PresenterUnderTest(fixture.Vulkan);
        var h = presenter.Harness;
        var address = h.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        var thread = CommandStreamStoreIndexTests.NewThread(presenter);
        var type = thread.GetType();
        var post = type.GetMethod("PostOwnedWrite")!;
        post.Invoke(thread, [address, new byte[] { 11 }, false]);
        post.Invoke(thread, [address + 16, new byte[] { 22 }, false]);
        var store = CommandStreamStoreIndexTests.Stores(thread)[0];
        using var reserved = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var previous = GuestGpuMemoryHook.Current;
        GuestGpuMemoryHook.Attach(h.Gpu);
        h.Gpu.BeforeManagedWriteReservationForTest = _ => { reserved.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        Task? landing = null;
        try
        {
            landing = Task.Run(() => h.Worker.Run(() => type.GetMethod("ApplyWrite", PresenterUnderTest.InstanceMembers)!.Invoke(thread, [store])));
            Assert.True(reserved.Wait(TimeSpan.FromSeconds(5)));
            var disjoint = Task.Run(() => Read(address + 16));
            Assert.Equal(22, await disjoint.WaitAsync(TimeSpan.FromSeconds(3)));
            using var reading = new ManualResetEventSlim();
            var same = Task.Run(() => { reading.Set(); return Read(address); });
            Assert.True(reading.Wait(TimeSpan.FromSeconds(3)));
            await Task.Delay(30);
            Assert.False(same.IsCompleted);
            release.Set();
            await landing;
            Assert.Equal(22, await disjoint);
            Assert.Equal(11, await same);
        }
        finally
        {
            release.Set();
            if (landing is not null) await landing;
            h.Gpu.BeforeManagedWriteReservationForTest = null;
            GuestGpuMemoryHook.Attach(previous);
        }
        byte Read(ulong target) => ((byte[])type.GetMethod("ReadOverlayForTest", PresenterUnderTest.InstanceMembers)!.Invoke(thread, [target, 1])!)[0];
    }
}

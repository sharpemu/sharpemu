// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PendingDumpPreservesDirtyNeighborAndLaterOverlappingFill(bool laterFill, bool deferredLabel)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var previous = GuestGpuMemoryHook.Current;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            presenter.Run(() =>
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(address + 16, 4, true);
                buffer.Fill(offset, 4, 0x12345678);
                var pending = presenter.GetField<PendingGuestPublications>("_guestPublications");
                var entry = pending.Add(0, address, 8);
                pending.Register(entry, harness.Scheduler.CurrentTick);
                harness.Scheduler.QueueMemoryPublication(() => pending.Publish(entry, () =>
                {
                    Assert.False(SubmissionScheduler.InDeferredOperation);
                    presenter.InvokeMethod("PublishOcclusionDump", address, 1u, 8u, 37UL);
                }));
                harness.Scheduler.Flush();
                if (deferredLabel)
                    Assert.True(((ICommandStreamHost)presenter.Instance).TryDeferEndOfPipe(0,
                        new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 1, address, 91)));
                if (laterFill)
                    harness.Cache.FillBuffer(address, 8, 0x11223344, false);
                harness.Scheduler.FinishMemoryAccess();
                Assert.Equal(0, pending.Count);
            });
            var bytes = harness.Read(address, 20);
            Assert.Equal(laterFill ? 0x1122334411223344UL : deferredLabel ? 91UL : (1UL << 63) | 37,
                BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        }
        finally
        {
            GuestGpuMemoryHook.Attach(previous);
        }
        harness.Shutdown();
    }
}

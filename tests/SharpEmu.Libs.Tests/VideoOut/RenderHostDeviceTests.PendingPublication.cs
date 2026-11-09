// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

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

    // A shader resource read behind a held-back command write sees the written bytes, as the command processor's
    // own reads do; it used to read memory the write had not reached yet.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceReadCompletesAnOverlappingHeldWrite(bool resident)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var previous = GuestGpuMemoryHook.Current;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var (read, left) = presenter.Run(() =>
            {
                var pending = presenter.GetField<PendingGuestPublications>("_guestPublications");
                var entry = pending.AddWrite(33, address + 0x40, 4);
                pending.Register(entry, harness.Scheduler.CurrentTick);
                harness.Scheduler.QueueMemoryPublication(() => pending.Publish(entry, () =>
                    Assert.True(harness.Memory.TryWrite(address + 0x40, BitConverter.GetBytes(0x55667788u)))));
                harness.Scheduler.Flush();
                var host = (IShaderPipelineHost)presenter.Instance;
                uint word;
                if (resident)
                {
                    var bytes = new byte[sizeof(uint)];
                    Assert.True(host.TryReadResidentGuestBytes(address + 0x40, bytes, false));
                    word = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                }
                else
                {
                    Assert.True(host.TryReadGuestWord(address + 0x40, out word));
                }

                return (word, pending.Count);
            });
            Assert.Equal(0x55667788u, read);
            Assert.Equal(0, left);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(previous);
        }
        harness.Shutdown();
    }

    // A held command-processor write or a pending occlusion dump over a range lands before a shader reads it, through a
    // buffer binding or a planned device address alike.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void APendingPublicationIsVisibleToAGpuReader(bool dump, bool deviceAddress)
    {
        if (!Ready()) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, ReadWrite) + 0x40;
        harness.Write(address, BitConverter.GetBytes(0x1111111111111111UL));
        var program = FixedProgramProvider.EmptyProgram(ShaderStageKind.Compute, 3);
        var snapshot = new ResourceSnapshot
        {
            DeviceAddressRanges = [new DeviceAddressRange(0, address, 32, true, false)],
        };
        var expected = dump ? (1UL << 63) | 37 : 0x5566778899AABBCCUL;
        var previous = GuestGpuMemoryHook.Current;
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var (buffer, offset, left) = presenter.Run(() =>
            {
                var pending = presenter.GetField<PendingGuestPublications>("_guestPublications");
                var entry = dump ? pending.Add(0, address, 8) : pending.AddWrite(33, address, 8);
                pending.Register(entry, harness.Scheduler.CurrentTick);
                harness.Scheduler.QueueMemoryPublication(() => pending.Publish(entry, () =>
                {
                    if (dump) presenter.InvokeMethod("PublishOcclusionDump", address, 1u, 8u, 37UL);
                    else Assert.True(harness.Memory.TryWrite(address, BitConverter.GetBytes(0x5566778899AABBCCUL)));
                }));
                harness.Scheduler.Flush();
                if (deviceAddress)
                {
                    using var preparation = presenter.RenderHost.BeginPreparation();
                    var prepared = presenter.RenderHost.PrepareBindings(new ShaderStageResources(program, snapshot));
                    presenter.RenderHost.PrepareDeviceAddresses();
                    presenter.RenderHost.BindResources(prepared);
                    var found = harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, 32));
                    return (found, found.Offset(address), pending.Count);
                }

                var (obtained, obtainedOffset) = harness.Cache.ObtainBuffer(address, 32, false);
                return (obtained, obtainedOffset, pending.Count);
            });
            harness.Finish();
            var gpuValue = BinaryPrimitives.ReadUInt64LittleEndian(harness.ReadBack(buffer, offset, 8));
            Assert.Equal(expected, gpuValue);
            Assert.Equal(0, left);
        }
        finally
        {
            GuestGpuMemoryHook.Attach(previous);
        }
        harness.Shutdown();
    }
}

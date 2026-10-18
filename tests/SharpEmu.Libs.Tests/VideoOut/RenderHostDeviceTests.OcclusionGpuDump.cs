// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Fact]
    public void PendingDumpReachesLaterGpuReadBeforeCpuPublication()
    {
        if (!Ready()) return;
        _vulkan!.Vk.GetPhysicalDeviceFeatures(_vulkan.Physical, out var features);
        if (!features.OcclusionQueryPrecise) return;
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        presenter.SetField("_supportsPreciseOcclusion", true);
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var results = harness.MapBacked(0x10000, ReadWrite);
        var copy = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var banks = Banks(RegisterWords.Color(target, Size, Size));
        banks.Context.DepthCountControl = 0x100;
        var executor = new RenderExecutor(presenter.RenderHost,
            new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices));
        presenter.Run(() =>
        {
            var host = (ICommandStreamHost)presenter.Instance;
            executor.DrawAuto(1, banks, Draw());
            Assert.True(host.DumpOcclusionCounter(0, results + 8, 0b101, 16));
            var tick = harness.Scheduler.CurrentTick;
            // A wait on the unsubmitted dump tick would never return; the read must take the GPU value.
            var (source, sourceOffset) = harness.Cache.ObtainBuffer(results, 0x100, false);
            Assert.Equal(tick, harness.Scheduler.CurrentTick);
            Assert.Equal(1, presenter.GetField<PendingGuestPublications>("_guestPublications").Count);
            var (destination, destinationOffset) = harness.Cache.ObtainBuffer(copy, 0x100, true);
            var native = new CommandBuffer(harness.Scheduler.Current.Handle);
            var region = new BufferCopy(sourceOffset, destinationOffset, 0x100);
            _vulkan.Vk.CmdCopyBuffer(native, source.Handle, destination.Handle, 1, &region);
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            };
            VulkanSynchronization.PipelineBarrier(_vulkan.Vk, native, PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.AllCommandsBit, 0, 1, &barrier, 0, null, 0, null);
            harness.Scheduler.Finish();
        });
        Assert.True(harness.Cache.DownloadToCpu(copy, 0x100));
        var copied = harness.Read(copy, 0x100);
        const ulong ready = 1UL << 63;
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(copied));
        Assert.Equal(ready | Size * Size, BinaryPrimitives.ReadUInt64LittleEndian(copied.AsSpan(8)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(copied.AsSpan(24)));
        Assert.Equal(ready, BinaryPrimitives.ReadUInt64LittleEndian(copied.AsSpan(40)));
        presenter.Run(() => harness.Scheduler.RunCompletedOperations());
        Assert.Equal(ready | Size * Size, BinaryPrimitives.ReadUInt64LittleEndian(harness.Read(results + 8, 8)));
    }
}

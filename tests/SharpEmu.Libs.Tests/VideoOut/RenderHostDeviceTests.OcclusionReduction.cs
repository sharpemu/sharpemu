// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(258, true)]
    public void OcclusionDumpsCaptureDrawBoundariesAndResetBeforeSubmission(int draws, bool submit)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        _vulkan.Vk.GetPhysicalDeviceFeatures(_vulkan.Physical, out var features);
        Assert.True(features.OcclusionQueryPrecise);
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        presenter.SetField("_supportsPreciseOcclusion", true);
        var harness = presenter.Harness;
        var target = harness.MapBacked(0x10000, ReadWrite);
        var vertices = harness.MapBacked(0x10000, ReadWrite);
        var results = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
        var banks = Banks(RegisterWords.Color(target, Size, Size));
        banks.Context.DepthCountControl = 0x100;
        var executor = new RenderExecutor(presenter.RenderHost,
            new FixedProgramProvider((IShaderPipelineHost)presenter.Instance, vertices));
        byte[] gpuValues = [];
        presenter.Run(() =>
        {
            var host = (ICommandStreamHost)presenter.Instance;
            executor.DrawAuto(1, banks, Draw());
            Assert.True(host.DumpOcclusionCounter(0, results, 1, 16));
            for (var index = 1; index < draws; index++)
            {
                executor.DrawAuto((ulong)index + 1, banks, Draw());
                presenter.RenderHost.EndRendering();
            }
            if (submit) harness.Scheduler.Flush();
            Assert.True(host.DumpOcclusionCounter(0, results + 8, 1, 16));
            Assert.True(host.ResetOcclusionCounter(0));
            Assert.True(host.DumpOcclusionCounter(0, results + 16, 1, 16));
            executor.DrawAuto((ulong)draws + 1, banks, Draw());
            Assert.True(host.DumpOcclusionCounter(0, results + 24, 1, 16));
            using var download = new GpuBuffer(_vulkan.DeviceInfo, harness.Scheduler,
                GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 32);
            try
            {
                var (source, offset) = harness.Cache.ObtainBuffer(results, 32, false);
                var command = new CommandBuffer(harness.Scheduler.Current.Handle);
                var copy = new BufferCopy(offset, 0, 32);
                _vulkan.Vk.CmdCopyBuffer(command, source.Handle, download.Handle, 1, &copy);
                var barrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.HostReadBit,
                };
                _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
                harness.Scheduler.Finish();
                download.Invalidate(0, 32);
                gpuValues = download.Mapped[..32].ToArray();
            }
            finally { harness.Scheduler.Finish(); }
        });
        presenter.Run(() => harness.Scheduler.RunCompletedOperations());
        var values = harness.Read(results, 32);
        Assert.Equal(values, gpuValues);
        const ulong ready = 1UL << 63;
        Assert.Equal(ready | Size * Size, BinaryPrimitives.ReadUInt64LittleEndian(values));
        Assert.Equal(ready | ((ulong)draws * Size * Size), BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(8)));
        Assert.Equal(ready, BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(16)));
        Assert.Equal(ready | Size * Size, BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(24)));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(0x100000001UL)]
    [InlineData(ulong.MaxValue)]
    public void OcclusionReductionPreservesPrefixCarryAndBorrow(ulong resetBase)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Worker.Run(() =>
        {
            var scheduler = harness.Scheduler;
            using var pipeline = new OcclusionSumPipeline(_vulkan.DeviceInfo);
            using var input = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 24);
            using var sum = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 8);
            using var output = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 32);
            try
            {
                ulong* samples = stackalloc ulong[3] { uint.MaxValue, 9, ulong.MaxValue };
                _vulkan.Vk.CmdUpdateBuffer(new CommandBuffer(scheduler.Current.Handle), input.Handle, 0, 24, samples);
                sum.Fill(0, 8, 0);
                output.Fill(0, 32, 0);
                pipeline.Record(scheduler, input, 0, 1, sum, 0, true);
                pipeline.Record(scheduler, sum, 0, 1, output, 0, false, resetBase, true);
                pipeline.Record(scheduler, input, 2, 2, sum, 0, true);
                pipeline.Record(scheduler, sum, 0, 1, output, 8, false, resetBase, true);
                pipeline.Record(scheduler, sum, 0, 1, output, 16, false, resetBase, true);
                sum.Fill(0, 8, 0);
                pipeline.Record(scheduler, sum, 0, 1, output, 24, false, 0, true);
                scheduler.Finish();
                output.Invalidate(0, 32);
                Assert.Equal(unchecked((ulong)uint.MaxValue - resetBase) | (1UL << 63), BinaryPrimitives.ReadUInt64LittleEndian(output.Mapped));
                var expected = unchecked((ulong)uint.MaxValue + 8 - resetBase) | (1UL << 63);
                Assert.Equal(expected, BinaryPrimitives.ReadUInt64LittleEndian(output.Mapped[8..]));
                Assert.Equal(expected, BinaryPrimitives.ReadUInt64LittleEndian(output.Mapped[16..]));
                Assert.Equal(1UL << 63, BinaryPrimitives.ReadUInt64LittleEndian(output.Mapped[24..]));
            }
            finally { scheduler.Finish(); }
        });
    }

}

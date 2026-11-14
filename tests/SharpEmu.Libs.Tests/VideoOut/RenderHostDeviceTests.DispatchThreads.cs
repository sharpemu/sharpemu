// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    // Thread counts a shader wrote become groups of the program's size in the pipeline's axis order, clamped to the device's
    // group limits, and the counts themselves become the program's thread limits; a zero count launches nothing. The
    // arguments and the limits sit at unaligned words of their buffers.
    [Theory]
    [InlineData(100u, 1u, 1u, 64u, 1u, 1u, 0, 1, 2, true)]
    [InlineData(300u, 40u, 2u, 8u, 4u, 2u, 2, 0, 1, true)]
    [InlineData(64u, 64u, 1u, 64u, 1u, 1u, 1, 2, 0, false)]
    [InlineData(0u, 5u, 1u, 64u, 1u, 1u, 0, 1, 2, true)]
    [InlineData(4u, 0u, 1u, 1u, 1u, 1u, 0, 1, 2, false)]
    [InlineData(uint.MaxValue, 1u, 1u, 1u, 1u, 1u, 0, 1, 2, true)]
    public void GpuThreadCountsBecomeGroupsAndThreadLimits(uint threadsX, uint threadsY, uint threadsZ, uint sizeX, uint sizeY,
        uint sizeZ, int axisX, int axisY, int axisZ, bool writesLimits)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var worker = new CacheWorker(_vulkan);
        worker.Run(() =>
        {
            var scheduler = worker.Scheduler;
            scheduler.Begin(new SubmissionContext { QueueName = "dispatch-threads.test", SubmissionId = 1 });
            using var arguments = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 64);
            using var limits = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, 64);
            using var download = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 128);
            using var pipeline = new DispatchThreadsPipeline(_vulkan.DeviceInfo);
            uint[] groupSize = [sizeX, sizeY, sizeZ];
            int[] axis = [axisX, axisY, axisZ];
            using var prepared = pipeline.Prepare(scheduler, new BufferBinding(arguments.Handle.Handle, 4),
                writesLimits ? new BufferBinding(limits.Handle.Handle, 8) : null, groupSize, axis);
            try
            {
                var command = new CommandBuffer(scheduler.Current.Handle);
                uint* words = stackalloc uint[4] { 0xDEAD, threadsX, threadsY, threadsZ };
                _vulkan.Vk.CmdUpdateBuffer(command, arguments.Handle, 0, 16, words);
                uint* sentinel = stackalloc uint[8] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7 };
                _vulkan.Vk.CmdUpdateBuffer(command, limits.Handle, 0, 32, sentinel);
                prepared.Record(command);
                var barrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                };
                _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.TransferBit,
                    PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
                var groupsCopy = new BufferCopy(0, 0, 12);
                _vulkan.Vk.CmdCopyBuffer(command, prepared.GroupsBuffer, download.Handle, 1, &groupsCopy);
                var limitsCopy = new BufferCopy(0, 64, 32);
                _vulkan.Vk.CmdCopyBuffer(command, limits.Handle, download.Handle, 1, &limitsCopy);
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.HostReadBit;
                _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
                scheduler.Finish();
                download.Invalidate(0, 128);
                var result = MemoryMarshal.Cast<byte, uint>(download.Mapped[..128]);
                var maximum = _vulkan.DeviceInfo.MaxComputeWorkGroupCount;
                uint[] physicalLimit = [maximum.X, maximum.Y, maximum.Z];
                uint[] threads = [threadsX, threadsY, threadsZ];
                var empty = threadsX == 0 || threadsY == 0 || threadsZ == 0;
                for (var logical = 0; logical < 3; logical++)
                {
                    var groups = Math.Min(RenderExecutor.GroupsFromThreads(threads[logical], groupSize[logical]), physicalLimit[axis[logical]]);
                    Assert.Equal(empty ? 0u : groups, result[axis[logical]]);
                }

                uint[] expectedLimits = writesLimits
                    ? [0xA0, 0xA1, threadsX, threadsY, threadsZ, 0xA5, 0xA6, 0xA7]
                    : [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7];
                Assert.Equal(expectedLimits, result.Slice(16, 8).ToArray());
            }
            finally
            {
                scheduler.Finish();
            }
        });
        _vulkan.AssertNoValidationMessages();
    }
}

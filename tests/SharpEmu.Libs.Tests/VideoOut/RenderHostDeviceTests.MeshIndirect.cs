// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IndirectMeshResourcesSurviveAbandonmentAndCompletion(bool record, bool invalid)
    {
        if (!GatePrerequisites.Ready(_vulkan, shaderInt64: true)) return;
        if (!_vulkan.SupportsMeshShaders)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh shader support.");
            return;
        }
        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(_vulkan);
        presenter.LoadRenderingCommands();
        presenter.SetField("_supportsMeshShader", true);
        presenter.SetField("_cmdDrawMeshTasksIndirect",
            (IntPtr)(void*)_vulkan.Vk.GetDeviceProcAddr(_vulkan.Device, "vkCmdDrawMeshTasksIndirectEXT"));
        var harness = presenter.Harness;
        var memory = harness.MapBacked(0x10000, ImageCacheTestSupport.ReadWrite);
        // An instance count over the group limit is fatal, as on the CPU path.
        harness.Write(memory, [3, 0, 0, 0, invalid ? (byte)255 : (byte)1, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0]);
        var request = new IndirectMeshDrawRequest(memory, memory + 64, 8, 2, false, 1,
            new MeshExecutionLimits { MaxGroupCountX = 64, MaxGroupCountY = 64, MaxGroupTotalCount = 64 });
        presenter.Run(() =>
        {
            for (var index = 0; index < 3; index++)
            {
                using var preparation = presenter.RenderHost.BeginPreparation();
                using var draw = presenter.RenderHost.PrepareIndirectMeshDraw(request);
                if (record) draw.Prepare();
            }
            if (invalid)
            {
                Assert.Throws<SchedulerFatalException>(() => harness.Scheduler.Finish());
                // All completion callbacks must release their resources even when one reports an error.
                for (var index = 0; index < 2; index++)
                    Assert.Throws<SchedulerFatalException>(() => harness.Scheduler.RunCompletedOperations());
            }
            else harness.Scheduler.Finish();
        });
        harness.Shutdown();
    }

    [Theory]
    [InlineData(2u, false, 6u, 1u, 2u, 64u, 0u)]
    [InlineData(4u, false, 7u, 1u, 2u, 64u, 0u)]
    [InlineData(2u, true, 7u, 1u, 2u, 64u, 0u)]
    [InlineData(4u, true, 20u, 0u, 1u, 64u, 0u)]
    [InlineData(2u, false, 0u, uint.MaxValue, 2u, 64u, 0u)]
    [InlineData(4u, false, 6u, uint.MaxValue, 0u, 64u, 0u)]
    [InlineData(2u, false, 6u, 7u, 1u, 64u, 1u)]
    [InlineData(4u, false, 6u, uint.MaxValue, 1u, 64u, 1u)]
    [InlineData(2u, true, 6u, 0u, 3u, 16u, 2u)]
    [InlineData(4u, false, 2u, 0u, 32u, 1u, 0u)]
    public void IndirectMeshPreparationChecksGpuArguments(uint elementSize, bool points, uint count,
        uint firstIndex, uint instances, uint totalLimit, uint expectedStatus)
    {
        if (!GatePrerequisites.Ready(_vulkan, shaderInt64: true)) return;
        if (!_vulkan.SupportsMeshShaders)
        {
            Assert.False(GatePrerequisites.DeviceRequired, "This test requires mesh shader support.");
            return;
        }
        using var worker = new CacheWorker(_vulkan);
        worker.Run(() =>
        {
            var scheduler = worker.Scheduler;
            scheduler.Begin(new SubmissionContext { QueueName = "mesh-indirect.test", SubmissionId = 1 });
            using var arguments = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal,
                0, GpuBuffer.AllFlags, 20);
            using var indices = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal,
                0, GpuBuffer.AllFlags, 36);
            using var download = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.Download,
                0, GpuBuffer.AllFlags, 96);
            using var pipeline = new MeshIndirectPipeline(_vulkan.DeviceInfo);
            var request = new IndirectMeshDrawRequest(0, 0, 8, elementSize, points, 1,
                new MeshExecutionLimits { MaxGroupCountX = 64, MaxGroupCountY = 64, MaxGroupTotalCount = totalLimit });
            // Destroying an unrecorded preparation must release all of its native resources.
            using (pipeline.Prepare(scheduler, new(arguments.Handle.Handle, 0), new(indices.Handle.Handle, 0), 2, request)) { }
            var prefix = elementSize == 2 ? 2u : 0u;
            using var prepared = pipeline.Prepare(scheduler, new(arguments.Handle.Handle, 0),
                new(indices.Handle.Handle, 0), prefix, request);
            try
            {
                var command = new CommandBuffer(scheduler.Current.Handle);
                uint* words = stackalloc uint[5] { count, instances, firstIndex, uint.MaxValue, 7 };
                _vulkan.Vk.CmdUpdateBuffer(command, arguments.Handle, 0, 20, words);
                var indexBytes = new byte[36];
                for (var index = 0; index < 8; index++)
                {
                    var destination = indexBytes.AsSpan((int)prefix + index * (int)elementSize);
                    if (elementSize == 2) BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)(index + 1));
                    else BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)(index + 1));
                }
                fixed (byte* data = indexBytes) _vulkan.Vk.CmdUpdateBuffer(command, indices.Handle, 0, 36, data);
                prepared.Record(command);
                var barrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                };
                _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
                var copy = new BufferCopy(0, 0, 96);
                _vulkan.Vk.CmdCopyBuffer(command, prepared.CommandBuffer, download.Handle, 1, &copy);
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.HostReadBit;
                _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
                scheduler.Finish();
                Assert.Equal(expectedStatus, prepared.ReadStatus());
                download.Invalidate(0, 96);
                var result = MemoryMarshal.Cast<byte, uint>(download.Mapped[..96]);
                var room = 8 - Math.Min(firstIndex, 8u);
                var boundedCount = Math.Min(Math.Min(count, 8u), room);
                var execute = expectedStatus != 2 && boundedCount != 0 && instances != 0;
                var vertices = execute ? points ? boundedCount : boundedCount / 3 * 3 : 0;
                Assert.Equal(execute ? points ? boundedCount : boundedCount / 3 : 0, result[0]);
                Assert.Equal(execute ? instances : 0, result[1]);
                Assert.Equal(1u, result[2]);
                Assert.Equal(vertices, result[4]);
                Assert.Equal(7u, result[6]);
                Assert.Equal(4u, result[7]);
                for (var index = 0; index < vertices; index++) Assert.Equal(firstIndex + (uint)index, result[16 + index]);
            }
            finally
            {
                scheduler.Finish();
            }
        });
        _vulkan.AssertNoValidationMessages();
    }
}

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
        // An instance count over the group limit launches nothing (status 2) and is left out, as on the CPU path.
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
            harness.Scheduler.Finish();
            Assert.Empty(fatal.Messages);
        });
        harness.Shutdown();
    }

    // The helper writes its status word on every exit, so a reused status buffer needs no host clear: a draw over the
    // group limits (status 2) returns its status buffer, and the next draw, which rents the same buffer, reports 0.
    [Fact]
    public void AReusedIndirectMeshStatusReportsTheNewDrawWithoutAHostClear()
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
            scheduler.Begin(new SubmissionContext { QueueName = "mesh-indirect-status.test", SubmissionId = 1 });
            using var arguments = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal,
                0, GpuBuffer.AllFlags, 16);
            using var pipeline = new MeshIndirectPipeline(_vulkan.DeviceInfo);
            var request = IndirectMeshDrawRequest.ForAutomaticDraw(0, false, 1,
                new MeshExecutionLimits { MaxGroupCountX = 32, MaxGroupCountY = 64, MaxGroupTotalCount = 64 });
            var binding = new BufferBinding(arguments.Handle.Handle, 0);
            uint Run(uint count, uint instances)
            {
                using var prepared = pipeline.Prepare(scheduler, binding, binding, 0, request);
                var command = new CommandBuffer(scheduler.Current.Handle);
                uint* words = stackalloc uint[4] { count, instances, 0, 0 };
                _vulkan.Vk.CmdUpdateBuffer(command, arguments.Handle, 0, 16, words);
                prepared.Record(command);
                scheduler.Finish();
                return prepared.ReadStatus();
            }

            Assert.Equal(2u, Run(120, 2));
            Assert.Equal(0u, Run(6, 2));
            Assert.Equal(2u, Run(120, 2));
            Assert.Equal(0u, Run(0, 2));
            scheduler.Finish();
        });
        _vulkan.AssertNoValidationMessages();
    }

    // An automatic draw: the groups follow the vertex count, and the parameters carry the
    // first vertex and instance with no index buffer.
    [Theory]
    [InlineData(true, 6u, 2u, 0u)]
    [InlineData(false, 7u, 2u, 0u)]
    [InlineData(true, 0u, 2u, 0u)]
    [InlineData(true, 6u, 0u, 0u)]
    [InlineData(true, 40u, 2u, 2u)]
    public void AutomaticIndirectMeshPreparationReadsVertexArguments(bool points, uint count, uint instances, uint expectedStatus)
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
            scheduler.Begin(new SubmissionContext { QueueName = "mesh-indirect-auto.test", SubmissionId = 1 });
            using var arguments = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.DeviceLocal,
                0, GpuBuffer.AllFlags, 16);
            using var download = new GpuBuffer(_vulkan.DeviceInfo, scheduler, GpuBufferUsage.Download,
                0, GpuBuffer.AllFlags, 64);
            using var pipeline = new MeshIndirectPipeline(_vulkan.DeviceInfo);
            var request = IndirectMeshDrawRequest.ForAutomaticDraw(0, points, 1,
                new MeshExecutionLimits { MaxGroupCountX = 32, MaxGroupCountY = 64, MaxGroupTotalCount = 64 });
            var binding = new BufferBinding(arguments.Handle.Handle, 0);
            using var prepared = pipeline.Prepare(scheduler, binding, binding, 0, request);
            try
            {
                var command = new CommandBuffer(scheduler.Current.Handle);
                uint* words = stackalloc uint[4] { count, instances, 9, 5 };
                _vulkan.Vk.CmdUpdateBuffer(command, arguments.Handle, 0, 16, words);
                prepared.Record(command);
                var barrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                };
                _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.TransferBit, 0, 1, &barrier, 0, null, 0, null);
                var copy = new BufferCopy(0, 0, 64);
                _vulkan.Vk.CmdCopyBuffer(command, prepared.CommandBuffer, download.Handle, 1, &copy);
                barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
                barrier.DstAccessMask = AccessFlags.HostReadBit;
                _vulkan.Vk.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit,
                    PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
                scheduler.Finish();
                Assert.Equal(expectedStatus, prepared.ReadStatus());
                download.Invalidate(0, 64);
                var result = MemoryMarshal.Cast<byte, uint>(download.Mapped[..64]);
                var primitives = points ? count : count / 3;
                var execute = expectedStatus != 2 && count != 0 && instances != 0;
                Assert.Equal(execute ? primitives : 0, result[0]);
                Assert.Equal(execute ? instances : 0, result[1]);
                Assert.Equal(1u, result[2]);
                Assert.Equal(execute ? points ? count : primitives * 3 : 0, result[4]);
                Assert.Equal(9u, result[5]);
                Assert.Equal(5u, result[6]);
                Assert.Equal(0u, result[7]);
                Assert.Equal(0u, result[8]);
                Assert.Equal(0u, result[9]);
            }
            finally
            {
                scheduler.Finish();
            }
        });
        _vulkan.AssertNoValidationMessages();
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

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class PresenterDispatchThreadLimitTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(0u)]
    [InlineData(32u)]
    public void BufferBindingPreservesThreadLimitsForQueuedDispatches(uint pushDataStart)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var allocation = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        var address = allocation + sizeof(uint);
        harness.Write(allocation, BitConverter.GetBytes(0xABCD1234u));
        harness.Write(address, new byte[sizeof(uint)]);
        var decoded = Program(MoveVector(0, 4, 1), BufferAtomicAdd(8, 0), EndProgram(16));
        var plan = Extract(decoded, userDataCount: 4);
        uint[] userData = [(uint)address, (uint)(address >> 32), sizeof(uint), 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, [0, 1, 2, 3], false, false, false,
            pushDataStart, usesDispatchThreadLimits: true);
        Assert.Equal(pushDataStart == 0, layout.UsesPushData);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, LocalSizeY = 2, LocalSizeZ = 2,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var stageInfo = new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute,
            Hash = Hash,
            UserDataCount = 4,
            Resources = resources,
            Bindings = layout,
        };
        DispatchThreadLimits[] limits = [new(0, 3, 3), new(1, 1, 1), new(63, 2, 2), new(64, 2, 2), new(65, 3, 3)];
        var buffer = presenter.Run(() =>
        {
            var pipelineHost = (IShaderPipelineHost)presenter.Instance;
            var program = new ShaderProgram(1, pipelineHost.CreateShaderModule(
                new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 1));
            var pipeline = pipelineHost.CreateComputePipeline(new ComputePipelineDescription
            {
                Program = program, Stage = stageInfo,
                Input = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 2, ThreadsZ = 2 },
            });
            foreach (var limit in limits)
            {
                var stage = new ShaderStageResources(stageInfo, snapshot) { ThreadLimits = limit };
                using var preparation = presenter.RenderHost.BeginPreparation();
                var prepared = presenter.RenderHost.PrepareBindings(stage);
                presenter.RenderHost.BindResources(prepared);
                presenter.RenderHost.CommitBindings(PipelineBindPoint.Compute, in pipeline, [prepared]);
                presenter.RenderHost.ShaderWriteHazardBarrier();
                presenter.RenderHost.BindPipeline(PipelineBindPoint.Compute, in pipeline);
                presenter.RenderHost.Dispatch(2, 2, 2);
                presenter.RenderHost.ShaderAccessBarrier();
            }
            presenter.RenderHost.ResetBindings();
            return harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, sizeof(uint)));
        });

        var result = harness.ReadBack(buffer, buffer.Offset(allocation), 2 * sizeof(uint));
        Assert.Equal(0xABCD1234u, BitConverter.ToUInt32(result, 0));
        Assert.Equal(limits.Sum(limit => (long)limit.X * limit.Y * limit.Z),
            (long)BitConverter.ToUInt32(result, sizeof(uint)));
        harness.Shutdown();
        vulkan!.AssertNoValidationMessages();
    }
}

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
public sealed class PresenterNullBufferTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANullBufferDescriptorReadsZeroAndDiscardsItsWrites(bool explicitLengths)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true) || (!explicitLengths && !vulkan!.SupportsNullDescriptor)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        presenter.SetField("_minStorageBufferOffsetAlignment", 256UL);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var address = harness.MapBacked(0x10000, GuestPageProtection.Read | GuestPageProtection.Write);
        harness.Write(address, BitConverter.GetBytes(0xFFFFFFFFu));
        var decoded = Program(
            MoveVector(0, 0, 0),
            MoveVector(8, 4, 0x1234),
            BufferStore(16, 0),
            BufferLoad(24, 0),
            BufferStore(32, 4),
            EndProgram(40));
        var plan = Extract(decoded, userDataCount: 8);
        uint[] userData = [0, 0, 0, 0, (uint)address, (uint)(address >> 32), sizeof(uint), 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(2, resources.Info.Buffers.Count);
        var layout = BindingLayout.Allocate(resources.Info, [0, 1, 2, 3, 4, 5, 6, 7], false, false, false,
            usesBufferLengths: explicitLengths);
        Assert.Equal(explicitLengths, layout.UsesBufferLengths);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, LocalSizeY = 1, LocalSizeZ = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var stageInfo = new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute,
            Hash = Hash,
            UserDataCount = 8,
            Resources = resources,
            Bindings = layout,
        };
        var buffer = presenter.Run(() =>
        {
            var pipelineHost = (IShaderPipelineHost)presenter.Instance;
            var program = new ShaderProgram(1, pipelineHost.CreateShaderModule(
                new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 1));
            var pipeline = pipelineHost.CreateComputePipeline(new ComputePipelineDescription
            {
                Program = program, Stage = stageInfo,
                Input = new ComputeInputInfo { ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1 },
            });
            var stage = new ShaderStageResources(stageInfo, snapshot);
            using var preparation = presenter.RenderHost.BeginPreparation();
            var prepared = presenter.RenderHost.PrepareBindings(stage);
            presenter.RenderHost.BindResources(prepared);
            presenter.RenderHost.CommitBindings(PipelineBindPoint.Compute, in pipeline, [prepared]);
            presenter.RenderHost.ShaderWriteHazardBarrier();
            presenter.RenderHost.BindPipeline(PipelineBindPoint.Compute, in pipeline);
            presenter.RenderHost.Dispatch(1, 1, 1);
            presenter.RenderHost.ShaderAccessBarrier();
            presenter.RenderHost.ResetBindings();
            return harness.Cache.GetBuffer(harness.Cache.FindBuffer(address, sizeof(uint)));
        });

        var result = harness.ReadBack(buffer, buffer.Offset(address), sizeof(uint));
        Assert.Equal(0u, BitConverter.ToUInt32(result, 0));
        harness.Shutdown();
        vulkan!.AssertNoValidationMessages();
    }
}

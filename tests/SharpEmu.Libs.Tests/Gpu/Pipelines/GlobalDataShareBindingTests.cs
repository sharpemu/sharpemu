// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.Tests.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A program on the global data share binds the cache's buffer whole, and its commit closes the rendering scope first.
[Collection(SchedulingStateCollection.Name)]
public sealed class GlobalDataShareBindingTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static byte[] CreateEmptyComputeShader()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var voidType = module.TypeVoid();
        var main = module.BeginFunction(voidType, module.TypeFunction(voidType));
        module.AddLabel();
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, main, "main", []);
        module.AddExecutionMode(main, SpirvExecutionMode.LocalSize, 1, 1, 1);
        return module.Build();
    }

    private static object Member(object instance, string name)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name, PresenterUnderTest.InstanceMembers);
        if (property is not null)
        {
            return property.GetValue(instance)!;
        }

        return type.GetField(name, PresenterUnderTest.InstanceMembers)!.GetValue(instance)!;
    }

    [Fact]
    public void Commit_EndsTheRenderingScopeAndBindsTheWholeShareBuffer()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan) || !vulkan.SupportsDynamicRendering)
        {
            return;
        }

        using var fatal = new FatalScope();
        using var presenter = new PresenterUnderTest(vulkan);
        presenter.LoadRenderingCommands();
        var harness = presenter.Harness;
        var info = new ShaderResourceInfo();
        var program = new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute,
            Hash = 0x77,
            Resources = new SpecializedResourceInfo { Info = info },
            Bindings = BindingLayout.Allocate(info, [], usesGlobalDataShare: true, usesFlattenedTable: false, usesShaderBase: false),
        };
        Assert.NotNull(program.Bindings.Find(DescriptorBindingKind.GlobalDataShare));
        var stage = new ShaderStageResources(program, new ResourceSnapshot());
        var input = new ComputeInputInfo { ThreadsX = 1, ThreadsY = 1, ThreadsZ = 1, Stage = stage };
        var host = (IShaderPipelineHost)presenter.Instance;
        presenter.Run(() =>
        {
            presenter.RenderHost.ResetBindings();
            var rendering = new RenderingState { Width = 1, Height = 1, Layers = 1, Samples = 1 };
            presenter.RenderHost.BeginRendering(in rendering);
            Assert.True(presenter.GetField<bool>("_renderingActive"));
            using var preparation = presenter.RenderHost.BeginPreparation();
            var bindings = presenter.RenderHost.PrepareBindings(stage);
            presenter.RenderHost.BindResources(bindings);
            var view = Member(Member(bindings, "Descriptors"), "GlobalDataShare");
            var buffer = (Silk.NET.Vulkan.Buffer)Member(view, "Buffer");
            Assert.Equal(harness.Cache.GdsBuffer.Handle.Handle, buffer.Handle);
            Assert.Equal(0UL, (ulong)Member(view, "Offset"));
            Assert.Equal(Vk.WholeSize, (ulong)Member(view, "Range"));

            var module = host.CreateShaderModule(new VulkanCompiledGuestShader(CreateEmptyComputeShader()), ShaderStage.Compute, program.Hash, 1);
            var pipeline = host.CreateComputePipeline(new ComputePipelineDescription { Input = input, Program = new ShaderProgram(1, module), Stage = program });
            presenter.RenderHost.CommitBindings(PipelineBindPoint.Compute, pipeline, [bindings]);
            Assert.False(presenter.GetField<bool>("_renderingActive"));
            presenter.RenderHost.BindPipeline(PipelineBindPoint.Compute, pipeline);
            presenter.RenderHost.Dispatch(1, 1, 1);
            presenter.RenderHost.ShaderAccessBarrier();
        });
        presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
        harness.Finish();
        harness.Shutdown();
        vulkan.AssertNoValidationMessages();
    }
}

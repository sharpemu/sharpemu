// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ComputePipelineContentKeyTests
{
    private static DescriptorSetLayoutBinding Binding(uint index, DescriptorType type = DescriptorType.StorageBuffer, uint count = 1) =>
        new() { Binding = index, DescriptorType = type, DescriptorCount = count, StageFlags = ShaderStageFlags.ComputeBit };

    private static ComputePipelineContentKey Key(string module, DescriptorSetLayoutBinding[] bindings, uint subgroup = 0, bool push = true) =>
        ComputePipelineContentKey.Create(module, bindings, push, ShaderStageFlags.ComputeBit, 128, subgroup);

    [Fact]
    public void IdenticalModuleAndLayoutShareAKey()
    {
        Assert.Equal(Key("AA", [Binding(0), Binding(1)]), Key("AA", [Binding(0), Binding(1)]));
    }

    [Fact]
    public void DifferentModulesBindingsAndSubgroupSizesDoNotShareAKey()
    {
        var baseline = Key("AA", [Binding(0), Binding(1)]);
        Assert.NotEqual(baseline, Key("BB", [Binding(0), Binding(1)]));
        Assert.NotEqual(baseline, Key("AA", [Binding(0)]));
        Assert.NotEqual(baseline, Key("AA", [Binding(0), Binding(2)]));
        Assert.NotEqual(baseline, Key("AA", [Binding(0), Binding(1, DescriptorType.UniformBuffer)]));
        Assert.NotEqual(baseline, Key("AA", [Binding(0), Binding(1, count: 4)]));
        Assert.NotEqual(baseline, Key("AA", [Binding(0), Binding(1)], subgroup: 32));
        Assert.NotEqual(baseline, Key("AA", [Binding(0), Binding(1)], push: false));
    }

    [Fact]
    public void PushConstantRangeIsPartOfTheKey()
    {
        DescriptorSetLayoutBinding[] bindings = [Binding(0)];
        Assert.NotEqual(
            ComputePipelineContentKey.Create("AA", bindings, true, ShaderStageFlags.ComputeBit, 128, 0),
            ComputePipelineContentKey.Create("AA", bindings, true, ShaderStageFlags.ComputeBit, 64, 0));
        Assert.NotEqual(
            ComputePipelineContentKey.Create("AA", bindings, true, ShaderStageFlags.ComputeBit, 128, 0),
            ComputePipelineContentKey.Create("AA", bindings, true, ShaderStageFlags.VertexBit, 128, 0));
    }
}

[Collection(SchedulingStateCollection.Name)]
public sealed class PresenterComputePipelineReuseTests(HeadlessVulkanFixture fixture)
    : IClassFixture<HeadlessVulkanFixture>
{
    [Fact]
    public void ProgramsWithTheSameModuleAndLayoutShareOnePipeline()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        using var presenter = new PresenterUnderTest(vulkan!);
        presenter.LoadRenderingCommands();
        var decoded = Program(MoveVector(0, 4, 1), BufferAtomicAdd(8, 0), EndProgram(16));
        var plan = Extract(decoded, userDataCount: 4);
        uint[] userData = [0x1000, 0, sizeof(uint), 0];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, [0, 1, 2, 3], false, false, false, 0, usesDispatchThreadLimits: false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 64, LocalSizeY = 1, LocalSizeZ = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var stageInfo = new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute,
            Hash = Hash,
            UserDataCount = 4,
            Resources = resources,
            Bindings = layout,
        };
        var input = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1 };

        presenter.Run(() =>
        {
            var host = (IShaderPipelineHost)presenter.Instance;
            // The cache gives each specialization of a guest shader its own program id and module.
            var first = new ShaderProgram(1, host.CreateShaderModule(new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 1));
            var second = new ShaderProgram(2, host.CreateShaderModule(new VulkanCompiledGuestShader(shader.Spirv), ShaderStage.Compute, Hash, 2));
            Assert.NotEqual(first.Module, second.Module);

            // Both programs ask while the first compile is in flight: the second must wait on it, not start its own.
            Assert.False(host.TryCreateComputePipeline(new ComputePipelineDescription { Program = first, Stage = stageInfo, Input = input }, out _));
            Assert.False(host.TryCreateComputePipeline(new ComputePipelineDescription { Program = second, Stage = stageInfo, Input = input }, out _));
            Assert.Single(presenter.GetField<IDictionary>("_pendingComputeByContent"));

            var a = Create(host, first);
            var b = Create(host, second);
            Assert.NotEqual(a.Pipeline, b.Pipeline);
            Assert.Equal(a.Layout, b.Layout);

            var entries = presenter.GetField<IDictionary>("_pipelineEntries");
            var entryA = entries[a.Pipeline]!;
            var entryB = entries[b.Pipeline]!;
            var pipelineField = entryA.GetType().GetField("Pipeline")!;
            var sharesField = entryA.GetType().GetField("SharesNativeObjects")!;
            Assert.Equal(((Pipeline)pipelineField.GetValue(entryA)!).Handle, ((Pipeline)pipelineField.GetValue(entryB)!).Handle);
            Assert.False((bool)sharesField.GetValue(entryA)!);
            Assert.True((bool)sharesField.GetValue(entryB)!);
            Assert.Single(presenter.GetField<IDictionary>("_computePipelinesByContent"));

            PipelineHandle Create(IShaderPipelineHost target, ShaderProgram program)
            {
                var description = new ComputePipelineDescription { Program = program, Stage = stageInfo, Input = input };
                PipelineHandle handle = default;
                for (var attempt = 0; attempt < 6000 && !target.TryCreateComputePipeline(description, out handle); attempt++)
                {
                    Thread.Sleep(10);
                }

                Assert.NotEqual(0ul, handle.Pipeline);
                return handle;
            }
        });
    }
}

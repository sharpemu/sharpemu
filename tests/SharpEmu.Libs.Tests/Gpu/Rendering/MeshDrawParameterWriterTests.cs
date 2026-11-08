// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class MeshDrawParameterWriterTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(21u)]
    [InlineData(22u)]
    [InlineData(32u)]
    public void Writer_PreservesPackedDataAndWritesAllSixParameters(uint pushCursor)
    {
        var layout = Layout(pushCursor);
        var stage = Stage(layout) with { MeshDraw = new MeshDrawParameters(91, 12, 7, 4, 0xFEDCBA9876543210UL) };
        var data = Enumerable.Repeat(0xA5A5A5A5u, (int)layout.ShaderDataDwordCount).ToArray();
        stage.WriteMeshDrawParameters(data);
        Assert.Equal(5u, layout.MeshDrawParametersDword);
        Assert.All(data.Take(5), value => Assert.Equal(0xA5A5A5A5u, value));
        Assert.Equal(new uint[] { 91, 12, 7, 4, 0x76543210u, 0xFEDCBA98u }, data[5..]);
        Assert.Equal(pushCursor <= 21, layout.UsesPushData);
    }

    [Fact]
    public void MissingParameters_StopBeforeWritingShaderData()
    {
        using var fatal = new FatalScope();
        var layout = Layout(0);
        var data = new uint[layout.ShaderDataDwordCount];
        Assert.Throws<SchedulerFatalException>(() => Stage(layout).WriteMeshDrawParameters(data));
        Assert.All(data, value => Assert.Equal(0u, value));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void InvalidDataLength_StopsBeforeWriting(int adjustment)
    {
        using var fatal = new FatalScope();
        var layout = Layout(0);
        var stage = Stage(layout) with { MeshDraw = new MeshDrawParameters(3, 0, 0, 0, 0) };
        var data = new uint[(int)layout.ShaderDataDwordCount + adjustment];
        Assert.Throws<SchedulerFatalException>(() => stage.WriteMeshDrawParameters(data));
        Assert.All(data, value => Assert.Equal(0u, value));
    }

    [Fact]
    public void OrdinaryLayout_DoesNotRequireOrWriteMeshParameters()
    {
        var layout = BindingLayout.Allocate(new ShaderResourceInfo(), [0u], false, false, false);
        var data = new uint[] { 71 };
        Stage(layout).WriteMeshDrawParameters(data);
        Assert.Equal(new uint[] { 71 }, data);
    }

    [Fact]
    public void MeshStageFlags_RetainFragmentAndComputeAccess()
    {
        Assert.Equal(ShaderStageFlags.MeshBitExt, DescriptorWriter.ShaderStageFlag(ShaderStage.Mesh));
        Assert.Equal(PipelineStageFlags.MeshShaderBitExt | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
            DescriptorWriter.PipelineStageFlag(ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit));
    }

    private static BindingLayout Layout(uint pushCursor) => BindingLayout.Allocate(
        new ShaderResourceInfo { Buffers = Enumerable.Range(0, 5).Select(_ => new BufferResource { Read = true }).ToList() },
        [0u], false, false, true, pushCursor, usesMeshDrawParameters: true);

    private static ShaderStageResources Stage(BindingLayout layout) => new(
        new ShaderProgramInfo { Stage = ShaderStageKind.Mesh, Bindings = layout }, new ResourceSnapshot());
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class MeshDrawBindingLayoutTests
{
    [Theory]
    [InlineData(0u, true)]
    [InlineData(21u, true)]
    [InlineData(22u, false)]
    [InlineData(32u, false)]
    public void ParametersFollowPackedOffsetsAndUseTheFullAllocation(uint cursor, bool usesPushData)
    {
        var info = BufferInfo();
        var layout = BindingLayout.Allocate(info, [0u], false, false, true, cursor,
            usesMeshDrawParameters: true);

        Assert.True(layout.UsesMeshDrawParameters);
        Assert.Equal(3u, layout.MemoryOffsetDword);
        Assert.Equal(5u, layout.MeshDrawParametersDword);
        Assert.Equal(11u, layout.ShaderDataDwordCount);
        Assert.Equal(usesPushData, layout.UsesPushData);
        Assert.Equal(usesPushData ? cursor : PushData.NoStart, layout.PushDataStartDword);
        Assert.Equal(!usesPushData, layout.Find(DescriptorBindingKind.ShaderData) is not null);
        var nextCursor = cursor;
        layout.AdvancePushData(ref nextCursor);
        Assert.Equal(usesPushData ? cursor + 11 : cursor, nextCursor);
        BindingLayoutValidator.Validate(layout, info, [0u], false, false, true, Hash, ShaderStage.Mesh);
    }

    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    [InlineData(ShaderStage.Compute)]
    public void ParametersAreRejectedForOtherStages(ShaderStage stage)
    {
        var info = new ShaderResourceInfo();
        var layout = BindingLayout.Allocate(info, [], false, false, false, usesMeshDrawParameters: true);
        Assert.Throws<ResourcePlanException>(() =>
            BindingLayoutValidator.Validate(layout, info, [], false, false, false, Hash, stage));
    }

    [Theory]
    [InlineData(ShaderStage.Mesh)]
    [InlineData(ShaderStage.Compute)]
    public void ParametersCannotShareALayoutWithComputeDispatchLimits(ShaderStage stage)
    {
        var info = new ShaderResourceInfo();
        var layout = BindingLayout.Allocate(info, [], false, false, false,
            usesDispatchThreadLimits: true, usesMeshDrawParameters: true);
        Assert.Equal(3u, layout.MeshDrawParametersDword);
        Assert.Equal(9u, layout.ShaderDataDwordCount);
        Assert.Throws<ResourcePlanException>(() =>
            BindingLayoutValidator.Validate(layout, info, [], false, false, false, Hash, stage));
    }

    [Fact]
    public void EqualityIncludesParametersButNotASpilledAllocationCursor()
    {
        var info = BufferInfo();
        var first = BindingLayout.Allocate(info, [0u], false, false, true, 31,
            usesMeshDrawParameters: true);
        var second = BindingLayout.Allocate(info, [0u], false, false, true, 32,
            usesMeshDrawParameters: true);
        var plain = BindingLayout.Allocate(info, [0u], false, false, true, 32);
        Assert.False(first.UsesPushData);
        Assert.False(second.UsesPushData);
        Assert.False(plain.UsesPushData);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, plain);
    }

    [Fact]
    public void PreparedMeshLayoutReservesParametersWithoutChangingCompute()
    {
        var program = Program(MoveScalarRegister(0, 8, 0), EndProgram(4));
        var (_, meshResources, mesh) = Prepare(program, stage: ShaderStage.Mesh);
        var (_, _, compute) = Prepare(program);
        Assert.True(mesh.UsesMeshDrawParameters);
        Assert.False(compute.UsesMeshDrawParameters);
        Assert.Equal(compute.ShaderDataDwordCount + 6, mesh.ShaderDataDwordCount);
        BindingLayoutValidator.Validate(mesh, meshResources.Info, mesh.UserDataRegisters,
            false, false, false, Hash, ShaderStage.Mesh);
    }

    private static ShaderResourceInfo BufferInfo() => new()
    {
        Buffers = Enumerable.Range(0, 5).Select(_ => new BufferResource { Read = true }).ToList(),
    };
}

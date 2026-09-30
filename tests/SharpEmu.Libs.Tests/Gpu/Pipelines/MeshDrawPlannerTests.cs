// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class MeshDrawPlannerTests
{
    private static readonly MeshShaderLimits Limits = new(128, 256, 256, 32768,
        65535, 65535, 65535, 128, 32768, 32768, 32, 32, 128);

    [Theory]
    [InlineData(false, 0x2000u, true)]
    [InlineData(true, 0x2000u, true)]
    [InlineData(false, 0x02002000u, false)]
    [InlineData(false, 0x2004u, false)]
    [InlineData(false, 0u, false)]
    public void PrimitiveShaderPlanValidatesStagesAndOutputCapacity(bool triangleStrip, uint stages, bool accepted)
    {
        var banks = Banks();
        banks.Context.ShaderStages = stages;
        banks.UserConfig.PrimitiveType = triangleStrip ? 6u : 4u;
        var plan = Create(banks);
        if (!accepted)
        {
            Assert.Null(plan);
            return;
        }
        Assert.NotNull(plan);
        Assert.Equal(triangleStrip ? 62u : 21u, plan.Value.Geometry.InputPrimitiveCountPerWorkgroup);
        Assert.Equal(triangleStrip ? 64u : 63u, plan.Value.Geometry.InputVertexCountPerWorkgroup);
        Assert.Equal(2304u, plan.Value.Geometry.LocalDataShareDwords);
    }

    [Theory]
    [InlineData("invocations")]
    [InlineData("workgroup")]
    [InlineData("vertices")]
    [InlineData("primitives")]
    [InlineData("shared-memory")]
    [InlineData("subgroup")]
    public void HostLimitBelowTheRequiredSize_RejectsThePlan(string limit)
    {
        var limits = limit switch
        {
            "invocations" => Limits with { MaxInvocations = 31 },
            "workgroup" => Limits with { MaxWorkGroupSizeX = 31 },
            "vertices" => Limits with { OutputVertexCapacity = 63 },
            "primitives" => Limits with { OutputPrimitiveCapacity = 63 },
            "shared-memory" => Limits with { MaxSharedMemoryBytes = 9491 },
            _ => Limits,
        };
        Assert.Null(Create(Banks(), limits, limit == "subgroup" ? 16u : 32u));
    }

    [Fact]
    public void ExactHostLimits_AcceptThePlanAndKeepGroupLimits()
    {
        var limits = Limits with
        {
            MaxInvocations = 32, MaxWorkGroupSizeX = 32,
            OutputVertexCapacity = 64, OutputPrimitiveCapacity = 64, MaxSharedMemoryBytes = 9492,
            MaxGroupCountX = 17, MaxGroupCountY = 23, MaxGroupTotalCount = 101,
        };
        var plan = Assert.IsType<MeshDrawPlan>(Create(Banks(), limits));
        Assert.Equal(64u, plan.Geometry.ThreadsPerGroup);
        Assert.Equal(32u, plan.Execution.DeviceSubgroupLaneCount);
        Assert.Equal(17u, plan.Execution.MaxGroupCountX);
        Assert.Equal(23u, plan.Execution.MaxGroupCountY);
        Assert.Equal(101u, plan.Execution.MaxGroupTotalCount);
    }

    [Theory]
    [InlineData(64u, 32u, true)]
    [InlineData(64u, 64u, true)]
    [InlineData(32u, 32u, true)]
    [InlineData(32u, 64u, false)]
    public void DeviceSubgroupMustFitTheGuestWave(uint guestWave, uint subgroup, bool accepted)
    {
        var banks = Banks();
        if (guestWave == 32) banks.Context.ShaderStages |= 1u << 22;
        var plan = Create(banks, Limits, subgroup);
        if (!accepted) { Assert.Null(plan); return; }
        Assert.NotNull(plan);
        Assert.Equal(guestWave, plan.Value.Geometry.WaveSize);
        Assert.Equal(subgroup, plan.Value.Execution.DeviceSubgroupLaneCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FusedGeometry_KeepsTriangleAssemblyAndProvokingVertex(bool triangleStrip)
    {
        var banks = Banks();
        banks.Context.ShaderStages = 0x20;
        banks.Context.ShaderInterface.MaxOutputPerSubgroup = 192;
        banks.Context.ShaderInterface.GeometryMaxVerticesOut = 6;
        banks.Context.RasterMode.ProvokingVertexLast = true;
        banks.UserConfig.PrimitiveType = triangleStrip ? 6u : 4u;
        banks.UserConfig.GeometryEngineControl.PrimitiveGroupSize = 8;
        banks.UserConfig.GeometryEngineControl.VertexGroupSize = 24;
        var plan = Assert.IsType<MeshDrawPlan>(Create(banks));
        Assert.Equal(8u, plan.Geometry.InputPrimitiveCountPerWorkgroup);
        Assert.Equal(triangleStrip ? 10u : 24u, plan.Geometry.InputVertexCountPerWorkgroup);
        Assert.Equal(192u, plan.Geometry.OutputVertexCapacity);
        Assert.Equal(32u, plan.Geometry.OutputPrimitiveCapacity);
        Assert.Equal(2u, plan.Geometry.ProvokingVertex);
        Assert.Equal(triangleStrip, plan.Geometry.InputTriangleStrip);
    }

    private static MeshDrawPlan? Create(RegisterBanks banks, MeshShaderLimits? limits = null, uint subgroup = 32) =>
        MeshDrawPlanner.Create(banks.Shader.Vertex, banks.Context.ShaderInterface,
            banks.Context, banks.UserConfig, limits ?? Limits, subgroup);

    private static RegisterBanks Banks()
    {
        var banks = new RegisterBanks(static message => new InvalidOperationException(message));
        banks.Context.ShaderStages = 0x2000;
        banks.Context.ShaderInterface.MaxOutputPerSubgroup = 64;
        banks.Context.ShaderInterface.GeometryOutputPrimitiveType = 2;
        banks.Shader.Vertex.GeometryResource2.LocalDataShareSize = 18;
        banks.UserConfig.GeometryEngineControl.PrimitiveGroupSize = 64;
        banks.UserConfig.GeometryEngineControl.VertexGroupSize = 64;
        banks.UserConfig.PrimitiveType = 4;
        return banks;
    }
}

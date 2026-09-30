// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal readonly record struct MeshDrawPlan
{
    internal GuestGeometryConfiguration Geometry { get; init; }
    internal MeshExecutionLimits Execution { get; init; }
}

internal static class MeshDrawPlanner
{
    internal static MeshDrawPlan? Create(VertexStageRegisters vertex,
        ShaderInterfaceRegisters shaderInterface, ContextRegisters context,
        UserConfigRegisters userConfig, MeshShaderLimits limits, uint subgroupSize)
    {
        var group = userConfig.GeometryEngineControl;
        var outputVertexCapacity = shaderInterface.MaxOutputPerSubgroup;
        var verticesPerPrimitive = shaderInterface.GeometryMaxVerticesOut;
        var fusedGeometry = (context.ShaderStages & 0x20u) != 0;
        var waveSize = (context.ShaderStages & 0x00400000u) != 0 ? 32u : 64u;
        var triangleStrip = userConfig.PrimitiveType == (uint)GuestPrimitiveType.TriangleStrip;
        if (!fusedGeometry && ((context.ShaderStages & 0x0200203Fu) != 0x2000u || verticesPerPrimitive != 0))
            return null;
        if ((!triangleStrip && userConfig.PrimitiveType != (uint)GuestPrimitiveType.TriangleList) ||
            shaderInterface.GeometryOutputPrimitiveType != 2 ||
            (fusedGeometry && verticesPerPrimitive < 3) || group.PrimitiveGroupSize == 0 ||
            group.VertexGroupSize < 3 || outputVertexCapacity == 0)
        {
            return null;
        }

        var inputPrimitives = triangleStrip ? (uint)group.VertexGroupSize - 2 : (uint)group.VertexGroupSize / 3;
        var outputCapacity = fusedGeometry ? outputVertexCapacity / verticesPerPrimitive
            : triangleStrip ? (outputVertexCapacity >= 3 ? outputVertexCapacity - 2 : 0) : outputVertexCapacity / 3;
        var inputPrimitiveCountPerWorkgroup = Math.Min((uint)group.PrimitiveGroupSize,
            Math.Min(inputPrimitives, outputCapacity));
        var outputPrimitiveCapacity = fusedGeometry
            ? checked((uint)group.PrimitiveGroupSize * (verticesPerPrimitive - 2))
            : (uint)group.PrimitiveGroupSize;
        var threadsPerGroup = checked(((outputVertexCapacity + waveSize - 1) / waveSize) * waveSize);
        var deviceInvocationCount = waveSize == 64 && subgroupSize == 32 ? threadsPerGroup / 2 : threadsPerGroup;
        var localDataShareDwords = (uint)vertex.GeometryResource2.LocalDataShareSize * 128;
        var sharedBytes = checked((localDataShareDwords + outputVertexCapacity + 5) * sizeof(uint));
        if (inputPrimitiveCountPerWorkgroup == 0 || deviceInvocationCount > limits.MaxInvocations || deviceInvocationCount > limits.MaxWorkGroupSizeX ||
            subgroupSize is not (32 or 64) || subgroupSize > waveSize ||
            outputVertexCapacity > limits.OutputVertexCapacity || outputPrimitiveCapacity > limits.OutputPrimitiveCapacity ||
            sharedBytes > limits.MaxSharedMemoryBytes)
        {
            return null;
        }

        return new MeshDrawPlan
        {
            Execution = new MeshExecutionLimits
            {
                DeviceSubgroupLaneCount = subgroupSize,
                MaxGroupCountX = limits.MaxGroupCountX,
                MaxGroupCountY = limits.MaxGroupCountY,
                MaxGroupTotalCount = limits.MaxGroupTotalCount,
            },
            Geometry = new GuestGeometryConfiguration
            {
                ThreadsPerGroup = threadsPerGroup,
                InputTriangleStrip = triangleStrip,
                InputPrimitiveCountPerWorkgroup = inputPrimitiveCountPerWorkgroup,
                InputVertexCountPerWorkgroup = triangleStrip ? inputPrimitiveCountPerWorkgroup + 2 : inputPrimitiveCountPerWorkgroup * 3,
                OutputVertexCapacity = outputVertexCapacity,
                OutputPrimitiveCapacity = outputPrimitiveCapacity,
                ProvokingVertex = context.RasterMode.ProvokingVertexLast ? 2u : 0u,
                WaveSize = waveSize,
                LocalDataShareDwords = localDataShareDwords,
                PositionExportControl = shaderInterface.VertexOutputControl,
            },
        };
    }
}

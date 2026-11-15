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
    // A primitive shader subgroup exports at most 256 primitives.
    private const uint MaximumSubgroupPrimitives = 256;

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
        var pointList = userConfig.PrimitiveType == (uint)GuestPrimitiveType.PointList;
        var lineList = userConfig.PrimitiveType == (uint)GuestPrimitiveType.LineList;
        if (!fusedGeometry && ((context.ShaderStages & 0x0200203Fu) != 0x2000u || verticesPerPrimitive != 0))
            return null;
        if ((!triangleStrip && !pointList && !lineList && userConfig.PrimitiveType != (uint)GuestPrimitiveType.TriangleList) ||
            ((pointList || lineList) && !fusedGeometry) ||
            shaderInterface.GeometryOutputPrimitiveType != 2 ||
            (fusedGeometry && verticesPerPrimitive < 3) || group.PrimitiveGroupSize == 0 ||
            group.VertexGroupSize < (pointList ? 1 : lineList ? 2 : 3) || outputVertexCapacity == 0)
        {
            return null;
        }

        var inputPrimitives = pointList ? (uint)group.VertexGroupSize
            : lineList ? (uint)group.VertexGroupSize / 2
            : triangleStrip ? (uint)group.VertexGroupSize - 2 : (uint)group.VertexGroupSize / 3;
        var outputCapacity = fusedGeometry ? outputVertexCapacity / verticesPerPrimitive
            : triangleStrip ? (outputVertexCapacity >= 3 ? outputVertexCapacity - 2 : 0) : outputVertexCapacity / 3;
        var inputPrimitiveCountPerWorkgroup = Math.Min((uint)group.PrimitiveGroupSize,
            Math.Min(inputPrimitives, outputCapacity));
        var outputPrimitiveCapacity = fusedGeometry
            ? checked((uint)group.PrimitiveGroupSize * (verticesPerPrimitive - 2))
            : (uint)group.PrimitiveGroupSize;
        var threadsPerGroup = WholeWaves(outputVertexCapacity, waveSize);
        // GE_NGG_SUBGRP_CNTL.PRIM_AMP_FACTOR bounds the primitives one input primitive produces. A mesh-style primitive
        // shader (one input point per subgroup) exports its own triangles, more than a strip of VGT_GS_MAX_VERT_OUT
        // vertices holds, and each lane exports one of them, so the subgroup needs a lane per primitive too. The plan
        // only grows, and only while the device can run it.
        var amplification = shaderInterface.PrimitiveShaderSubgroupControl & 0x1FFu;
        if (fusedGeometry && amplification != 0)
        {
            var amplified = Math.Min(MaximumSubgroupPrimitives, checked((uint)group.PrimitiveGroupSize * amplification));
            var amplifiedThreads = WholeWaves(Math.Max(outputVertexCapacity, amplified), waveSize);
            var amplifiedInvocations = DeviceInvocations(amplifiedThreads, waveSize, subgroupSize);
            if (amplified > outputPrimitiveCapacity && amplified <= limits.OutputPrimitiveCapacity &&
                amplifiedInvocations <= limits.MaxInvocations && amplifiedInvocations <= limits.MaxWorkGroupSizeX)
            {
                outputPrimitiveCapacity = amplified;
                threadsPerGroup = amplifiedThreads;
            }
        }
        var deviceInvocationCount = DeviceInvocations(threadsPerGroup, waveSize, subgroupSize);
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
                InputPointList = pointList,
                InputLineList = lineList,
                InputPrimitiveCountPerWorkgroup = inputPrimitiveCountPerWorkgroup,
                InputVertexCountPerWorkgroup = pointList ? inputPrimitiveCountPerWorkgroup
                    : lineList ? inputPrimitiveCountPerWorkgroup * 2
                    : triangleStrip ? inputPrimitiveCountPerWorkgroup + 2 : inputPrimitiveCountPerWorkgroup * 3,
                OutputVertexCapacity = outputVertexCapacity,
                OutputPrimitiveCapacity = outputPrimitiveCapacity,
                ProvokingVertex = context.RasterMode.ProvokingVertexLast ? 2u : 0u,
                WaveSize = waveSize,
                LocalDataShareDwords = localDataShareDwords,
                PositionExportControl = shaderInterface.VertexOutputControl,
            },
        };
    }

    private static uint WholeWaves(uint threads, uint waveSize) => checked((threads + waveSize - 1) / waveSize * waveSize);

    // A wave64 subgroup runs as two 32-lane halves on a 32-lane device.
    private static uint DeviceInvocations(uint threads, uint waveSize, uint subgroupSize) =>
        waveSize == 64 && subgroupSize == 32 ? threads / 2 : threads;
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Reconstructs the workgroup shape used by a merged legacy ES+GS program.
internal static class MeshInputResolver
{
    private const uint Wave32StageBit = 0x0040_0000;
    private const uint TriangleOutputPrimitive = 2;

    public static MeshInputInfo Resolve(
        VertexStageRegisters vertex,
        RegisteredShader shader,
        ShaderInterfaceRegisters shaderInterface,
        GeometryEngineControlRegisters geometryControl,
        GuestPrimitiveType inputPrimitive,
        uint shaderStages,
        bool provokingVertexLast,
        uint hostSubgroupSize)
    {
        if (!shader.IsFused)
        {
            throw SubmissionScheduler.Fatal(
                $"A mesh program must have a registered continuation: shader=0x{shader.CodeAddress:X16}.");
        }

        if (vertex.GeometryResource1.GeometryVectorComponentCount != 3 ||
            vertex.GeometryResource2.ExportVectorComponentCount != 3)
        {
            throw SubmissionScheduler.Fatal(
                $"The merged geometry vector ABI is unsupported: shader=0x{shader.CodeAddress:X16} " +
                $"geometryComponents={vertex.GeometryResource1.GeometryVectorComponentCount} " +
                $"exportComponents={vertex.GeometryResource2.ExportVectorComponentCount}.");
        }

        if (inputPrimitive is not GuestPrimitiveType.PointList and
            not GuestPrimitiveType.LineList and
            not GuestPrimitiveType.TriangleList and
            not GuestPrimitiveType.TriangleStrip)
        {
            throw SubmissionScheduler.Fatal(
                $"The merged geometry input primitive is unsupported: shader=0x{shader.CodeAddress:X16} primitive={(uint)inputPrimitive}.");
        }

        var primitiveSize = inputPrimitive switch
        {
            GuestPrimitiveType.PointList => 1u,
            GuestPrimitiveType.LineList => 2u,
            _ => 3u,
        };
        var primitiveStep = inputPrimitive == GuestPrimitiveType.TriangleStrip ? 1u : primitiveSize;
        var maxVerticesOut = shaderInterface.GeometryMaxVerticesOut;
        var maxVertices = shaderInterface.MaxOutputPerSubgroup;
        if (shaderInterface.GeometryOutputPrimitiveType != TriangleOutputPrimitive ||
            maxVerticesOut < 3 ||
            geometryControl.VertexGroupSize < primitiveSize ||
            maxVertices == 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The merged geometry assembly is unsupported: shader=0x{shader.CodeAddress:X16} " +
                $"input={(uint)inputPrimitive} output={shaderInterface.GeometryOutputPrimitiveType} " +
                $"vertices={maxVerticesOut} primitiveGroup={geometryControl.PrimitiveGroupSize} " +
                $"vertexGroup={geometryControl.VertexGroupSize} maxOutput={maxVertices}.");
        }

        var vertexGroup = (uint)geometryControl.VertexGroupSize;
        var inputPrimitiveCount = vertexGroup < primitiveSize
            ? 0u
            : ((vertexGroup - primitiveSize) / primitiveStep) + 1u;
        var primitivesPerGroup = Math.Min(
            (uint)geometryControl.PrimitiveGroupSize,
            Math.Min(inputPrimitiveCount, maxVertices / maxVerticesOut));
        if (primitivesPerGroup == 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The merged geometry workgroup contains no input primitives: shader=0x{shader.CodeAddress:X16}.");
        }

        var verticesPerGroup = checked(((primitivesPerGroup - 1u) * primitiveStep) + primitiveSize);
        var maxPrimitives = checked((uint)geometryControl.PrimitiveGroupSize * (maxVerticesOut - 2u));
        var waveSize = (shaderStages & Wave32StageBit) != 0 ? 32u : 64u;
        var threadsX = checked(((maxVertices + waveSize - 1u) / waveSize) * waveSize);
        return new MeshInputInfo
        {
            InputPrimitive = inputPrimitive,
            PrimitivesPerGroup = primitivesPerGroup,
            VerticesPerGroup = verticesPerGroup,
            MaxVertices = maxVertices,
            MaxPrimitives = maxPrimitives,
            OutputPrimitive = shaderInterface.GeometryOutputPrimitiveType,
            ProvokingVertex = provokingVertexLast ? 2u : 0u,
            ThreadsX = threadsX,
            LocalDataShareDwords = checked((uint)vertex.GeometryResource2.LocalDataShareSize * 128u),
            ScratchDwords = shader.MaximumScratchDwords,
            HostSubgroupSize = hostSubgroupSize,
            WaveSize = waveSize,
        };
    }
}

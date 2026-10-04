// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Workgroup shapes observed in Poppy Playtime Chapter 3's merged legacy ES+GS programs.
[Collection(SchedulingStateCollection.Name)]
public sealed class MeshInputResolverTests : IDisposable
{
    private const ulong EntryCode = 0x0000_0041_2EA2_4E00;
    private const ulong ContinuationCode = EntryCode + 0x1000;
    private const uint LegacyEsGsStages = 0x0000_2030;

    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    private static RegisteredShader Shader(uint frontScratch = 5, uint continuationScratch = 13) =>
        new(EntryCode, EntryCode - 0x100, 4, frontScratch, 0x1000, 0, 0, ContinuationCode, 4)
        {
            ContinuationHeaderAddress = ContinuationCode - 0x100,
            ContinuationScratchDwords = continuationScratch,
            ContinuationUserDataAddress = 0x2000,
        };

    private static VertexStageRegisters Vertex(byte localDataShareSize = 0) => new()
    {
        GeometryResource1 = new GeometryResource1 { GeometryVectorComponentCount = 3 },
        GeometryResource2 = new GeometryResource2
        {
            ExportVectorComponentCount = 3,
            LocalDataShareSize = localDataShareSize,
        },
    };

    private static MeshInputInfo Resolve(
        GuestPrimitiveType inputPrimitive,
        ushort primitiveGroupSize,
        ushort vertexGroupSize,
        uint maxOutput,
        uint maxVerticesOut,
        bool provokingVertexLast = false,
        byte localDataShareSize = 0,
        uint outputPrimitive = 2,
        uint hostSubgroupSize = 32) =>
        MeshInputResolver.Resolve(
            Vertex(localDataShareSize),
            Shader(),
            new ShaderInterfaceRegisters
            {
                MaxOutputPerSubgroup = maxOutput,
                GeometryMaxVerticesOut = maxVerticesOut,
                GeometryOutputPrimitiveType = outputPrimitive,
            },
            new GeometryEngineControlRegisters
            {
                PrimitiveGroupSize = primitiveGroupSize,
                VertexGroupSize = vertexGroupSize,
            },
            inputPrimitive,
            LegacyEsGsStages,
            provokingVertexLast,
            hostSubgroupSize);

    [Fact]
    public void TriangleListPoppyShapeUsesTwentyOneCompleteInputPrimitives()
    {
        var input = Resolve(
            GuestPrimitiveType.TriangleList,
            primitiveGroupSize: 64,
            vertexGroupSize: 64,
            maxOutput: 192,
            maxVerticesOut: 3,
            provokingVertexLast: true,
            localDataShareSize: 7);

        Assert.Equal(GuestPrimitiveType.TriangleList, input.InputPrimitive);
        Assert.Equal(21u, input.PrimitivesPerGroup);
        Assert.Equal(63u, input.VerticesPerGroup);
        Assert.Equal(192u, input.MaxVertices);
        Assert.Equal(64u, input.MaxPrimitives);
        Assert.Equal(2u, input.OutputPrimitive);
        Assert.Equal(2u, input.ProvokingVertex);
        Assert.Equal(192u, input.ThreadsX);
        Assert.Equal(1u, input.ThreadsY);
        Assert.Equal(1u, input.ThreadsZ);
        Assert.Equal(7u * 128u, input.LocalDataShareDwords);
        Assert.Equal(13u, input.ScratchDwords);
        Assert.Equal(32u, input.HostSubgroupSize);
        Assert.Equal(64u, input.WaveSize);
        Assert.True(input.IsActive);
    }

    [Fact]
    public void TriangleStripPoppyShapeUsesTenPrimitivesAndTwelveSharedVertices()
    {
        var input = Resolve(
            GuestPrimitiveType.TriangleStrip,
            primitiveGroupSize: 10,
            vertexGroupSize: 24,
            maxOutput: 240,
            maxVerticesOut: 24);

        // Twenty-four available vertices could form twenty-two strip primitives, but
        // the primitive-group and output budgets both cap this workgroup at ten.
        Assert.Equal(10u, input.PrimitivesPerGroup);
        Assert.Equal(12u, input.VerticesPerGroup);
        Assert.Equal(240u, input.MaxVertices);
        Assert.Equal(220u, input.MaxPrimitives);
        Assert.Equal(256u, input.ThreadsX);
        Assert.Equal(0u, input.ProvokingVertex);
        Assert.Equal(10u, input.InputPrimitiveCount(input.VerticesPerGroup));
        Assert.Equal(12u, input.InputVertexCount(input.PrimitivesPerGroup));
    }

    [Theory]
    [InlineData(GuestPrimitiveType.PointList, 7u, 1u, 1u, 7u, 7u)]
    [InlineData(GuestPrimitiveType.LineList, 7u, 2u, 2u, 3u, 6u)]
    public void PointAndLineCountHelpersUseTheirPrimitiveWidths(
        GuestPrimitiveType primitive,
        uint availableVertices,
        uint expectedSize,
        uint expectedStep,
        uint expectedPrimitives,
        uint expectedVertices)
    {
        var input = new MeshInputInfo { InputPrimitive = primitive };

        Assert.Equal(expectedSize, input.InputPrimitiveSize());
        Assert.Equal(expectedStep, input.InputPrimitiveStep());
        Assert.Equal(expectedPrimitives, input.InputPrimitiveCount(availableVertices));
        Assert.Equal(expectedVertices, input.InputVertexCount(expectedPrimitives));
        Assert.Equal(0u, input.InputVertexCount(0));
    }

    [Fact]
    public void ScratchUsesTheLargerContinuationRequirement()
    {
        var input = MeshInputResolver.Resolve(
            Vertex(),
            Shader(frontScratch: 29, continuationScratch: 41),
            new ShaderInterfaceRegisters
            {
                MaxOutputPerSubgroup = 192,
                GeometryMaxVerticesOut = 3,
                GeometryOutputPrimitiveType = 2,
            },
            new GeometryEngineControlRegisters { PrimitiveGroupSize = 64, VertexGroupSize = 64 },
            GuestPrimitiveType.TriangleList,
            LegacyEsGsStages,
            provokingVertexLast: false,
            hostSubgroupSize: 64);

        Assert.Equal(41u, input.ScratchDwords);
    }

    [Fact]
    public void NonTriangleOutputPrimitiveIsRejected()
    {
        var exception = Assert.Throws<SchedulerFatalException>(() => Resolve(
            GuestPrimitiveType.TriangleList,
            primitiveGroupSize: 64,
            vertexGroupSize: 64,
            maxOutput: 192,
            maxVerticesOut: 3,
            outputPrimitive: 1));

        Assert.Contains("output=1", exception.Message);
    }
}

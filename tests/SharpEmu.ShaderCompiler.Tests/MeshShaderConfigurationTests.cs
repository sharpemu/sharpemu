// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class MeshShaderConfigurationTests
{
    [Theory]
    [InlineData(1u, 1u, 0, 1u, 1u, 48ul)]
    [InlineData(33u, 9u, 2, 32u, 8u, 3584ul)]
    public void OutputStorageIncludesPaddedVerticesAndPrimitiveBuiltins(
        uint vertices, uint primitives, int parameters, uint vertexGranularity,
        uint primitiveGranularity, ulong expectedBytes)
    {
        var mesh = new MeshShaderConfiguration(vertices, primitives, 0, 1, 3, 0);
        Assert.Equal(expectedBytes, mesh.OutputMemoryBytes(parameters, vertexGranularity, primitiveGranularity));
    }

    [Fact]
    public void OutputLocationsIncludeShaderExportsAndRequiredPixelInputsOnce()
    {
        var program = ResourceTestProgram.Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u], [], [],
                new Gen5ExportControl(32, 15, false, false, false)),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u], [], [],
                new Gen5ExportControl(35, 15, false, false, false)), ResourceTestProgram.EndProgram(16));
        Assert.Equal(new uint[] { 0, 1, 3 }, MeshShaderConfiguration.ParameterLocations(program, 2));
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 0u)]
    public void OutputStorageRejectsZeroAllocationGranularity(uint vertexGranularity, uint primitiveGranularity)
    {
        var mesh = new MeshShaderConfiguration(32, 16, 0, 1, 3, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.OutputMemoryBytes(1, vertexGranularity, primitiveGranularity));
    }

    [Fact]
    public void OutputStorageRoundsLargeCountsWithoutWrapping()
    {
        var mesh = new MeshShaderConfiguration(uint.MaxValue, uint.MaxValue, 0, 1, 3, 0);
        Assert.Equal(274877906944ul, mesh.OutputMemoryBytes(1, 32, 32));
    }
}

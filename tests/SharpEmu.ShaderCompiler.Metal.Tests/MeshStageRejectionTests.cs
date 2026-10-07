// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class MeshStageRejectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeshRequestsDoNotProduceComputeShaders(bool hasConfiguration)
    {
        var program = new Gen5ShaderProgram(0, [
            new(0, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null)]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Mesh, 1, 0, 0);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false,
            usesMeshDrawParameters: hasConfiguration);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32,
            LocalSizeX = 32,
            Mesh = hasConfiguration ? new MeshShaderConfiguration(32, 16, 0, 8, 24, 0, true, 32) : null,
        };
        var compiled = Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error);
        Assert.False(compiled, compiled
            ? $"Mesh request produced a {shader.Stage} shader with entry point {shader.EntryPoint}."
            : error);
        Assert.Null(shader);
        Assert.Equal("Metal does not support mesh shaders.", error);
    }

    [Theory]
    [InlineData(ShaderStage.Vertex, Gen5MslStage.Vertex)]
    [InlineData(ShaderStage.Pixel, Gen5MslStage.Pixel)]
    [InlineData(ShaderStage.Compute, Gen5MslStage.Compute)]
    public void SupportedStagesKeepTheirStage(ShaderStage stage, Gen5MslStage expected)
    {
        var program = new Gen5ShaderProgram(0, [
            new(0, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null)]);
        var request = Gen5ComputeFixtures.RequestOrThrow(program, stage);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(expected, shader.Stage);
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class SystemUserDataPolicyTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(0xFCUL)]
    [InlineData(ulong.MaxValue)]
    public void ExcludedRegisters_AgreeInTheGraphAndBindingLayout(ulong mask)
    {
        var program = Program(ScalarLoad(0, 0, 8), ScalarLoad(8, 2, 10), ScalarLoad(16, 64, 12), EndProgram(24));
        var graph = ScalarValueGraph.Build(program, 0, 68, excludedUserDataRegisters: mask);
        Assert.Equal(mask, graph.ExcludedUserDataRegisters);
        var expected = new uint[] { 0, 1, 2, 3, 64, 65 }.Where(register => register >= 64 || (mask & (1UL << (int)register)) == 0);
        Assert.Equal(expected, BindingLayout.CollectUserDataRegisters(program, 0, 68, mask));
        for (var index = 0; index < 3; index++)
        {
            var register = new uint[] { 0, 2, 64 }[index];
            var handle = graph.Accesses[index]!.Handle!;
            Assert.Equal(register < 64 && (mask & (1UL << (int)register)) != 0, handle.Operands[0].IsUndefined);
            if (register >= 64) Assert.Equal(ScalarValueKind.UserData, handle.Operands[0].Kind);
        }
    }

    [Theory]
    [InlineData(ShaderStage.Compute, 0UL)]
    [InlineData(ShaderStage.Mesh, 0xFCUL)]
    public void CompileRequests_KeepTheStagePolicy(ShaderStage stage, ulong expectedMask)
    {
        var program = Program(Vop1(0, "VMovB32", 0, Gen5Operand.Scalar(3)), EndProgram(4));
        var plan = ShaderResourcePlan.Extract(program, stage, Hash, 0, 16);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var registers = BindingLayout.CollectUserDataRegisters(program, 0, 16, plan.Graph.ExcludedUserDataRegisters);
        var layout = BindingLayout.Allocate(resources.Info, registers, false, false, false);
        var request = new ShaderCompileRequest(plan, resources, layout);
        Assert.Equal(expectedMask, request.ExcludedUserDataRegisters);
        Assert.Equal(stage == ShaderStage.Mesh ? Array.Empty<uint>() : new uint[] { 3 }, registers);
        BindingLayoutValidator.Validate(layout, resources.Info, registers, false, false, false, Hash, stage);
    }

    [Fact]
    public void MeshSystemInputs_CannotMakeAUniformBufferDescriptor()
    {
        var program = Program(BufferLoad(0, 2), EndProgram(8));
        Assert.Single(ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 16).Info.Buffers);
        Assert.Throws<ResourcePlanException>(() => ShaderResourcePlan.Extract(program, ShaderStage.Mesh, Hash, 0, 16));
    }
}

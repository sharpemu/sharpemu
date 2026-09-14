// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class EmbeddedVertexOffsetTests
{
    private static readonly Gen5ShaderInstruction OffsetAddition = new(0, Gen5ShaderEncoding.Vop3, "VSadU32", [0, 0],
        [Gen5Operand.Scalar(18), Gen5Operand.Source(128), Gen5Operand.Vector(5)], [Gen5Operand.Vector(5)], null);
    private static readonly Gen5ShaderInstruction VertexFetch = new(8, Gen5ShaderEncoding.Mubuf, "BufferLoadFormatXyzw", [0, 0], [], [],
        new Gen5BufferMemoryControl(4, 5, 0, 0, 0, true, false, false, false));
    private static readonly Gen5ShaderInstruction ProgramEnd = new(16, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);

    private static Gen5VertexInputBinding CreateVertexInputBinding(uint address = 8, bool perInstance = false) =>
        new(address, 0, 4, 10, 0, VertexBase, 16, 4, [], 1024, false, perInstance);

    private static Gen5ShaderState CreateShaderState(params Gen5ShaderInstruction[] instructions) =>
        new(new Gen5ShaderProgram(0, instructions.Length == 0 ? [OffsetAddition, VertexFetch, ProgramEnd] : instructions),
            new uint[12], null, UserDataScalarRegisterBase: 8);

    private static Gen5ShaderEvaluation CreateShaderEvaluation(int offset, IReadOnlyList<Gen5VertexInputBinding>? inputs = null)
    {
        var scalars = new uint[256];
        scalars[10] = 99;
        scalars[18] = unchecked((uint)offset);
        return new Gen5ShaderEvaluation(scalars, scalars, [], [], VertexInputs: inputs ?? [CreateVertexInputBinding()]);
    }

    private static VertexInputInfo CreateProductionVertexInput(Gen5ShaderState state, Gen5ShaderEvaluation evaluation)
    {
        var provider = typeof(AgcExports).GetNestedType("ShaderProgramProvider", BindingFlags.NonPublic)!;
        var createStage = provider.GetMethod("CreateStageProgram", BindingFlags.Static | BindingFlags.NonPublic)!;
        var stage = (AgcExports.CompiledStageProgram)createStage.Invoke(null,
            [ShaderStageKind.Vertex, 0UL, new VulkanCompiledGuestShader([]), state, evaluation,
             Array.Empty<GuestDrawTexture>(), evaluation.VertexInputs!, 0, 0, 0, -1, false, null])!;
        var createInput = provider.GetMethod("CreateVertexInput", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (VertexInputInfo)createInput.Invoke(null, [stage, evaluation])!;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-8)]
    public void ProductionProvider_UsesTheAbsoluteScalarRegisterAndRelativeUserData(int offset)
    {
        var input = CreateProductionVertexInput(CreateShaderState(), CreateShaderEvaluation(offset));
        Assert.Equal(18, input.Stage.Program!.VertexOffsetScalarRegister);
        Assert.Equal(unchecked((uint)offset), input.Stage.Resources.UserData[10]);
        Assert.Equal(offset, RenderExecutor.ResolveVertexOffset(0, input));
        Assert.Equal(new VertexInputBuffer(VertexBase, 16, 64), Assert.Single(input.Buffers));
        var program = Assert.IsType<AgcExports.CompiledStageProgram>(input.Stage.Program);
        Assert.Equal(4u, Assert.Single(program.VertexAttributes).OffsetBytes);
    }

    [Theory]
    [InlineData(false, false, 8, 0, 2, 10)]
    [InlineData(true, false, 8, 0, -3, 5)]
    [InlineData(true, false, -8, 0, 10, 2)]
    [InlineData(false, false, 8, 7, 2, 9)]
    [InlineData(false, true, 8, 7, 2, 2)]
    [InlineData(true, true, 8, 7, -3, -3)]
    public void ProductionOffset_ReachesTheDrawWithoutChangingBufferOrAttributeOffsets(
        bool indexed, bool indirect, int scalarOffset, uint registerOffset, int argumentOffset, int expectedOffset)
    {
        using var fatal = new FatalScope();
        var host = new RecordingRenderHost();
        var vertexInput = CreateProductionVertexInput(CreateShaderState(), CreateShaderEvaluation(scalarOffset));
        var defaults = Programs();
        var provider = new FakePipelineProvider
        {
            Graphics = new GraphicsPrograms
            {
                Vertex = defaults.Vertex, Pixel = defaults.Pixel,
                VertexInput = vertexInput, PixelInput = defaults.PixelInput,
            },
        };
        var banks = Banks();
        banks.UserConfig.IndexOffset = registerOffset;
        var source = indirect ? DrawOffsetSource.IndirectArguments : DrawOffsetSource.Packet;
        var executor = new RenderExecutor(host, provider);
        if (indexed)
        {
            executor.DrawIndexed(1, banks, Indexed(3, baseVertex: argumentOffset, source: source));
            Assert.Contains($"draw_indexed 3 1 0 {expectedOffset} 0", host.Calls);
        }
        else
        {
            executor.DrawAuto(1, banks, Auto(3, firstVertex: (uint)argumentOffset, source: source));
            Assert.Contains($"draw 3 1 {expectedOffset} 0", host.Calls);
        }

        Assert.Contains(host.Calls, call => call.StartsWith("obtain 100400000 400 written=False", StringComparison.Ordinal));
        Assert.Contains(host.Calls, call => call.StartsWith("bind_vertex ", StringComparison.Ordinal) && call.EndsWith(":0", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedFunctionFetch_KeepsBothShaderBackendsUnchanged()
    {
        var state = CreateShaderState();
        var evaluation = CreateShaderEvaluation(8);
        Assert.True(Gen5SpirvTranslator.TryCompileVertexShader(state, evaluation, out var vulkan, out var vulkanError), vulkanError);
        Assert.NotEmpty(vulkan.Spirv);
        Assert.True(Gen5MslTranslator.TryCompileVertexShader(state, evaluation, out var metal, out var metalError), metalError);
        Assert.Contains("sharpemu_vin.in0", metal.Source);
        Assert.Contains("max(s[18], 0u)", metal.Source);
    }

    [Theory]
    [InlineData("index_read_before")]
    [InlineData("index_read_after")]
    [InlineData("second_addition")]
    [InlineData("scalar_written")]
    [InlineData("nonzero_difference")]
    [InlineData("modified_addition")]
    [InlineData("branch")]
    [InlineData("execution_mask")]
    [InlineData("saved_execution_mask")]
    [InlineData("relative_register")]
    [InlineData("fetch_before_addition")]
    [InlineData("unresolved_fetch")]
    [InlineData("instance_fetch")]
    [InlineData("mixed_vertex_fetch")]
    [InlineData("wrong_user_data_base")]
    [InlineData("short_user_data")]
    public void UnprovenOffsets_KeepTheExistingPath(string scenario)
    {
        var state = CreateShaderState();
        Gen5VertexInputBinding[] inputs = [CreateVertexInputBinding()];
        var readIndex = new Gen5ShaderInstruction(24, Gen5ShaderEncoding.Vop1, "VMovB32", [],
            [Gen5Operand.Vector(5)], [Gen5Operand.Vector(12)], null);
        state = scenario switch
        {
            "index_read_before" => CreateShaderState(readIndex, OffsetAddition, VertexFetch, ProgramEnd),
            "index_read_after" => CreateShaderState(OffsetAddition, VertexFetch, readIndex, ProgramEnd),
            "second_addition" => CreateShaderState(OffsetAddition, OffsetAddition with { Pc = 4 }, VertexFetch, ProgramEnd),
            "scalar_written" => CreateShaderState(new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Smem, "SLoadDwordx4", [], [],
                [Gen5Operand.Scalar(16)], new Gen5ScalarMemoryControl(4, 0, null)), OffsetAddition, VertexFetch, ProgramEnd),
            "nonzero_difference" => CreateShaderState(OffsetAddition with { Sources = [Gen5Operand.Scalar(18), Gen5Operand.Source(129), Gen5Operand.Vector(5)] }, VertexFetch, ProgramEnd),
            "modified_addition" => CreateShaderState(OffsetAddition with { Control = new Gen5Vop3Control(0, 1, 0, false, 0, null) }, VertexFetch, ProgramEnd),
            "branch" => CreateShaderState(OffsetAddition, VertexFetch, ProgramEnd with { Opcode = "SCbranchExecz" }),
            "execution_mask" => CreateShaderState(ProgramEnd with { Opcode = "VCmpxEqU32" }, OffsetAddition, VertexFetch, ProgramEnd),
            "saved_execution_mask" => CreateShaderState(ProgramEnd with { Opcode = "SAndSaveexecB64" }, OffsetAddition, VertexFetch, ProgramEnd),
            "relative_register" => CreateShaderState(OffsetAddition, VertexFetch, ProgramEnd with { Opcode = "VMovrelsB32" }),
            "fetch_before_addition" => CreateShaderState(VertexFetch, OffsetAddition, ProgramEnd),
            "wrong_user_data_base" => state with { UserDataScalarRegisterBase = 0 },
            "short_user_data" => state with { UserData = new uint[10] },
            _ => state,
        };
        inputs = scenario switch
        {
            "unresolved_fetch" => [CreateVertexInputBinding(24)],
            "instance_fetch" => [CreateVertexInputBinding(perInstance: true)],
            "mixed_vertex_fetch" => [CreateVertexInputBinding(), CreateVertexInputBinding(24)],
            _ => inputs,
        };
        Assert.False(Gen5ShaderTranslator.TryGetEmbeddedVertexOffsetRegister(state, inputs, out var scalarRegister));
        Assert.Equal(-1, scalarRegister);
        Assert.Equal(-1, CreateProductionVertexInput(state, CreateShaderEvaluation(8, inputs)).Stage.Program!.VertexOffsetScalarRegister);
    }

    [Fact]
    public void AliasedFetches_RequireEveryFetchToUseTheFixedFunctionInput()
    {
        var state = CreateShaderState(OffsetAddition, VertexFetch, VertexFetch with { Pc = 16 }, ProgramEnd with { Pc = 24 });
        Assert.True(Gen5ShaderTranslator.TryGetEmbeddedVertexOffsetRegister(state, [CreateVertexInputBinding() with { AliasPcs = [16] }], out var scalarRegister));
        Assert.Equal(18, scalarRegister);
        Assert.False(Gen5ShaderTranslator.TryGetEmbeddedVertexOffsetRegister(state, [CreateVertexInputBinding()], out _));
    }

    [Fact]
    public void InstanceInputs_KeepTheirOwnRateAndDoNotSupplyAVertexOffset()
    {
        var instanceFetch = VertexFetch with
        {
            Pc = 16,
            Control = new Gen5BufferMemoryControl(4, 8, 12, 0, 0, true, false, false, false),
        };
        var state = CreateShaderState(OffsetAddition, VertexFetch, instanceFetch, ProgramEnd with { Pc = 24 });
        var evaluation = CreateShaderEvaluation(8,
            [CreateVertexInputBinding(), CreateVertexInputBinding(16, perInstance: true) with { Location = 1 }]);
        var input = CreateProductionVertexInput(state, evaluation);
        Assert.Equal(8, RenderExecutor.ResolveVertexOffset(0, input));
        Assert.Equal(0u, RenderExecutor.ResolveInstanceOffset(input));
        var program = Assert.IsType<AgcExports.CompiledStageProgram>(input.Stage.Program);
        Assert.False(program.VertexAttributes[0].PerInstance);
        Assert.True(program.VertexAttributes[1].PerInstance);
        Assert.Equal(2, input.Buffers.Length);
        Assert.Equal(input.Buffers[0], input.Buffers[1]);
    }
}

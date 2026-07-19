// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.ShaderCompiler;

public sealed class Gen5PixelOutputComponentSwapTests
{
    private const ulong ProgramAddress = 0x1_0000_0000;

    [Theory]
    [InlineData(Gen5PixelOutputComponentSwap.Standard, 1, 0, -1, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.Standard, 2, 0, 1, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.Standard, 3, 0, 1, 2, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.Standard, 4, 0, 1, 2, 3)]
    [InlineData(Gen5PixelOutputComponentSwap.Alternate, 1, 1, -1, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.Alternate, 2, 0, 3, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.Alternate, 3, 0, 1, 3, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.Alternate, 4, 2, 1, 0, 3)]
    [InlineData(Gen5PixelOutputComponentSwap.StandardReverse, 1, 2, -1, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.StandardReverse, 2, 1, 0, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.StandardReverse, 3, 2, 1, 0, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.StandardReverse, 4, 3, 2, 1, 0)]
    [InlineData(Gen5PixelOutputComponentSwap.AlternateReverse, 1, 3, -1, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.AlternateReverse, 2, 3, 0, -1, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.AlternateReverse, 3, 3, 1, 0, -1)]
    [InlineData(Gen5PixelOutputComponentSwap.AlternateReverse, 4, 3, 0, 1, 2)]
    public void MapsGuestExportComponentsToHostOutputLanes(
        Gen5PixelOutputComponentSwap componentSwap,
        uint componentCount,
        int red,
        int green,
        int blue,
        int alpha)
    {
        var binding = new Gen5PixelOutputBinding(
            0,
            0,
            Gen5PixelOutputKind.Float,
            componentSwap,
            componentCount);

        Assert.Equal(red, binding.GetSourceComponent(0));
        Assert.Equal(green, binding.GetSourceComponent(1));
        Assert.Equal(blue, binding.GetSourceComponent(2));
        Assert.Equal(alpha, binding.GetSourceComponent(3));
    }

    [Fact]
    public void AlternateSwapCompilesThroughTheVulkanPixelPath()
    {
        uint[] words =
        [
            0x7E000280,             // v_mov_b32 v0, 0
            0x7E0202F2,             // v_mov_b32 v1, 1.0
            0x7E0402F0,             // v_mov_b32 v2, 0.5
            0x7E0602F2,             // v_mov_b32 v3, 1.0
            0xF800180F, 0x03020100, // exp mrt0 v0, v1, v2, v3 done vm
            0xBF810000,             // s_endpgm
        ];
        var memory = new FakeCpuMemory(ProgramAddress, 0x100);
        var code = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                code.AsSpan(index * sizeof(uint)),
                words[index]);
        }
        Assert.True(memory.TryWrite(ProgramAddress, code));

        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ProgramAddress,
                out var program,
                out var decodeError),
            decodeError);
        var state = new Gen5ShaderState(program!, new uint[16], Metadata: null);
        var evaluation = new Gen5ShaderEvaluation(
            new uint[128],
            new uint[128],
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                [new Gen5PixelOutputBinding(
                    0,
                    0,
                    Gen5PixelOutputKind.Float,
                    Gen5PixelOutputComponentSwap.Alternate,
                    ComponentCount: 4)],
                out var shader,
                out var compileError),
            compileError);
        Assert.NotEmpty(shader.Spirv);
    }

    [Fact]
    public void ElevenElevenTenRemapsToTheHostPackedOrder()
    {
        var binding = new Gen5PixelOutputBinding(
            0,
            0,
            Gen5PixelOutputKind.Float,
            Gen5PixelOutputComponentSwap.Standard,
            ComponentCount: 3,
            DataFormat: 7);

        Assert.Equal(2, binding.GetSourceComponent(0));
        Assert.Equal(1, binding.GetSourceComponent(1));
        Assert.Equal(0, binding.GetSourceComponent(2));
        Assert.Equal(-1, binding.GetSourceComponent(3));
    }

    [Fact]
    public void SingleChannelAlphaSelectionUsesTheHostRedLane()
    {
        var binding = new Gen5PixelOutputBinding(
            0,
            0,
            Gen5PixelOutputKind.Float,
            Gen5PixelOutputComponentSwap.AlternateReverse,
            ComponentCount: 1,
            DataFormat: 4);

        Assert.Equal(0, binding.GetSourceComponent(0));
    }
}

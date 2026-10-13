// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageAtomic64Tests
{
    // The atomic names its texel width by the data dwords: two (compare-swap: four) address a 64-bit texel.
    [Theory]
    [InlineData("ImageAtomicUmax", 3u, SpirvOp.AtomicUMax, ShaderStage.Compute)]
    [InlineData("ImageAtomicAdd", 3u, SpirvOp.AtomicIAdd, ShaderStage.Compute)]
    [InlineData("ImageAtomicCmpswap", 0xFu, SpirvOp.AtomicCompareExchange, ShaderStage.Compute)]
    [InlineData("ImageAtomicUmax", 3u, SpirvOp.AtomicUMax, ShaderStage.Pixel)]
    public void AnAtomicOnA64BitTexelDeclaresAnR64ImageAndUsesA64BitAtomic(string opcode, uint dmask, SpirvOp atomic, ShaderStage stage)
    {
        var shader = Compile(opcode, dmask, stage, supportsImageInt64Atomics: true, out var error);

        Assert.True(shader is not null, error);
        var module = new SpirvModuleInspector(shader!);
        Assert.Contains((uint)SpirvCapability.Int64ImageExt, module.Capabilities);
        Assert.Contains((uint)SpirvCapability.Int64Atomics, module.Capabilities);
        Assert.Contains((ushort)SpirvOp.ImageTexelPointer, module.Opcodes);
        Assert.Contains((ushort)atomic, module.Opcodes);
        Assert.Contains("SPV_EXT_shader_image_int64", Encoding.ASCII.GetString(shader!));
    }

    [Fact]
    public void AnAtomicOnA64BitTexelHasItsOwnBinding()
    {
        var shader = Compile("ImageAtomicUmax", 3u, ShaderStage.Compute, supportsImageInt64Atomics: true, out var error);

        Assert.True(shader is not null, error);
        var module = new SpirvModuleInspector(shader!);
        // The 64-bit class of a 2D storage atomic: the image binding numbers 46 to 50 are 1D, 1D array, 2D, 2D array and 3D.
        Assert.Equal(48u, module.BindingOf("48"));
    }

    [Fact]
    public void AnAtomicOnA32BitTexelKeepsItsR32Image()
    {
        var shader = Compile("ImageAtomicUmax", 1u, ShaderStage.Compute, supportsImageInt64Atomics: true, out var error);

        Assert.True(shader is not null, error);
        var module = new SpirvModuleInspector(shader!);
        Assert.DoesNotContain((uint)SpirvCapability.Int64ImageExt, module.Capabilities);
        Assert.DoesNotContain("SPV_EXT_shader_image_int64", Encoding.ASCII.GetString(shader!));
    }

    [Fact]
    public void AnAtomicOnA64BitTexelNeedsTheDeviceFeature()
    {
        var shader = Compile("ImageAtomicUmax", 3u, ShaderStage.Compute, supportsImageInt64Atomics: false, out var error);

        Assert.Null(shader);
        Assert.Contains("shaderImageInt64Atomics", error);
    }

    private static byte[]? Compile(string opcode, uint dmask, ShaderStage stage, bool supportsImageInt64Atomics, out string error)
    {
        var program = Program(Image(0, opcode, 0, dmask: dmask, vectorAddress: 1), EndProgram(8));
        var (plan, resources, layout) = Prepare(program, stage);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            SupportsImageInt64Atomics = supportsImageInt64Atomics,
            PixelOutputs = stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : [],
        };
        return Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error) ? shader.Spirv : null;
    }
}

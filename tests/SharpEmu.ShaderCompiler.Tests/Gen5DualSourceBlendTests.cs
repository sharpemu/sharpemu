// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Dual-source blending reads a second color from the MRT1 export. It is declared at target
// 0's location with index 1 and is not a render target of its own.
public sealed class Gen5DualSourceBlendTests
{
    private static Gen5ShaderInstruction Export(uint pc, uint target, bool done) =>
        new(pc, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)], [],
            new Gen5ExportControl(target, 15, false, done, false));

    private static ShaderCompileRequest Request(params Gen5PixelOutputBinding[] outputs)
    {
        var program = ResourceTestProgram.Program(Export(0, 0, false), Export(8, 1, true), ResourceTestProgram.EndProgram(16));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        return new ShaderCompileRequest(plan, resources, layout) { PixelOutputs = outputs };
    }

    private static IEnumerable<(uint Op, uint[] Args)> Instructions(byte[] bytes)
    {
        for (var offset = 20; offset < bytes.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            var count = (int)(header >> 16);
            var args = new uint[count - 1];
            for (var index = 0; index < args.Length; index++)
                args[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4 * (index + 1)));
            yield return (header & 0xFFFF, args);
            offset += count * 4;
        }
    }

    [Fact]
    public void SecondSource_IsLocationZeroIndexOne()
    {
        var request = Request(
            new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
            new Gen5PixelOutputBinding(1, 0, Gen5PixelOutputKind.Float) { ExportTarget = 1, Index = 1 });

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        const uint Decorate = 71, Location = 30, Index = 32;
        var decorations = Instructions(shader.Spirv).Where(i => i.Op == Decorate).ToArray();
        var second = Assert.Single(decorations, i => i.Args[1] == Index && i.Args[2] == 1).Args[0];
        Assert.Contains(decorations, i => i.Args[0] == second && i.Args[1] == Location && i.Args[2] == 0);
        Assert.Equal(2, decorations.Count(i => i.Args[1] == Location && i.Args[2] == 0));
    }

    [Fact]
    public void SecondSource_NeedsASingleTarget()
    {
        var request = Request(
            new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
            new Gen5PixelOutputBinding(2, 1, Gen5PixelOutputKind.Float),
            new Gen5PixelOutputBinding(1, 0, Gen5PixelOutputKind.Float) { ExportTarget = 1, Index = 1 });

        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("second source", error, StringComparison.Ordinal);
    }
}

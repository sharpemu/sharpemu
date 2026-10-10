// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// A graphics wave is one host subgroup of up to 32 guest lanes. V_READLANE in a pixel shader
// must read the selected lane of that group: Ghost of Yotei reduces a wave minimum with it,
// and a loop that waits for the minimum to reach 255 never ends on the reading lane's own value.
public sealed class Gen5PixelReadlaneTests
{
    private static Gen5ShaderInstruction Export(uint pc) =>
        new(pc, Gen5ShaderEncoding.Exp, "Exp", [0u, 0u],
            [Gen5Operand.Vector(4), Gen5Operand.Vector(4), Gen5Operand.Vector(4), Gen5Operand.Vector(4)], [],
            new Gen5ExportControl(0, 15, false, true, false));

    private static IEnumerable<uint> Opcodes(byte[] bytes)
    {
        for (var offset = 20; offset < bytes.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            yield return header & 0xFFFF;
            offset += (int)(header >> 16) * 4;
        }
    }

    [Theory]
    [InlineData(31u)]
    [InlineData(63u)]
    public void Readlane_ReadsTheSelectedLaneOfTheSubgroup(uint lane)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.ReadLane(0, 20, 3, lane),
            ResourceTestProgram.MoveVectorFromScalar(8, 4, 20),
            Export(12),
            ResourceTestProgram.EndProgram(20));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        const uint GroupNonUniformShuffle = 345;
        Assert.Contains(GroupNonUniformShuffle, Opcodes(shader.Spirv));
    }
}

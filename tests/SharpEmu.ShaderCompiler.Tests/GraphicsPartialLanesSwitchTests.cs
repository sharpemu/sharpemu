// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class GraphicsPartialLanesSwitchTests
{
    // A pixel V_READLANE keeps main's lowering unless the partial-subgroup lane reads are asked for
    // (SHARPEMU_GRAPHICS_PARTIAL_LANES=1), which look the source lane up in a ballot and the guest EXEC mask.
    [Fact]
    public void PixelReadLaneKeepsMainsLoweringUnlessEnabled()
    {
        Assert.Equal(Compile(enabled: false), Compile(enabled: null));
        Assert.NotEqual(Compile(enabled: false), Compile(enabled: true));
    }

    private static byte[] Compile(bool? enabled)
    {
        var program = Program(ReadLane(0, 32, 5, 31), MoveVectorFromScalar(8, 6, 32), EndProgram(16));
        var (plan, resources, layout) = Prepare(program, ShaderStage.Pixel);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        };
        if (enabled is { } value)
        {
            request = new ShaderCompileRequest(plan, resources, layout)
            {
                PixelOutputs = request.PixelOutputs,
                GraphicsPartialSubgroupLanes = value,
            };
        }

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }
}

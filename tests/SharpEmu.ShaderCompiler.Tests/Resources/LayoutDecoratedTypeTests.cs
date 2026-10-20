// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// A type with an explicit layout decoration is only valid under explicit-layout storage classes.
public sealed class LayoutDecoratedTypeTests
{
    // The push-data array is uint[32] with an ArrayStride, and a program with 32 buffers declares a private uint[32] of
    // per-buffer byte biases: the two must not be one type.
    [Fact]
    public void PushDataArrayAndTheBufferBiasArrayAreDistinctTypes()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.Add(MoveVectorFromScalar(pc, 1, 0));
        pc += 8;
        for (uint index = 0; index < ShaderResourceInfo.MaxBuffers; index++)
        {
            for (uint dword = 0; dword < 4; dword++)
            {
                instructions.Add(MoveScalar(pc, 8 + dword, index + dword + 0x100));
                pc += 8;
            }

            instructions.Add(BufferLoad(pc, 8));
            pc += 8;
        }

        instructions.Add(EndProgram(pc));
        var request = Request(Program([.. instructions]));

        Assert.Equal(ShaderResourceInfo.MaxBuffers, request.Resources.Info.Buffers.Count);
        Assert.True(request.Bindings.UsesPushData);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
    }
}

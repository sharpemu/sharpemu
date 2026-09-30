// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ComputePrefixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedSuffixUsesTheSelectedControlFlowPath(bool repeats)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, 20, 0),
            ResourceTestProgram.Branch(4, "SBranch", 0),
            ResourceTestProgram.MoveScalar(8, 21, 1),
            repeats ? ResourceTestProgram.Branch(12, "SBranch", -2)
                : ResourceTestProgram.EndProgram(12));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Compute);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
            out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var switches = 0;
        var loops = 0;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            if ((SpirvOp)(words[offset] & 0xffff) == SpirvOp.LoopMerge) loops++;
            if ((SpirvOp)(words[offset] & 0xffff) != SpirvOp.Switch) continue;
            switches++;
            Assert.Equal(5u, words[offset] >> 16);
            Assert.Equal(1u, words[offset + 3]);
        }
        var structured = Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_CF") != "0" ||
            Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_FORWARD_BLOCKS") != "0";
        Assert.Equal(repeats && !structured ? 1 : 0, switches);
        Assert.Equal(repeats ? 1 : 0, loops);
    }
}

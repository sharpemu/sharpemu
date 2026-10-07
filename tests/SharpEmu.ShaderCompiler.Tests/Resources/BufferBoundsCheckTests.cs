// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// A guest buffer word is bounds-checked in the shader unless the host measured that the device
// returns zero past the end of a descriptor range itself.
public sealed class BufferBoundsCheckTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferWordsAreCheckedUnlessTheDeviceZeroesOutOfRangeReads(bool deviceZeroes)
    {
        var program = Program(ScalarBufferLoad(0, 4, 20, count: 4), EndProgram(8));
        var (plan, resources, layout) = Prepare(program, SharpEmu.ShaderCompiler.Resources.ShaderStage.Compute, 0, 8);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            ZeroOutOfBoundsBufferReads = deviceZeroes,
        };

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        // One OpArrayLength is one range test. Without the device promise every word carries one.
        var checks = CountOpcode(shader.Spirv, SpirvOp.ArrayLength);
        if (deviceZeroes)
        {
            Assert.Equal(0, checks);
        }
        else
        {
            Assert.True(checks >= 4, $"expected a range test per word, found {checks}");
        }
    }

    private static int CountOpcode(byte[] spirv, SpirvOp opcode)
    {
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var count = 0;
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = (int)(words[offset] >> 16);
            if ((ushort)(words[offset] & 0xFFFF) == (ushort)opcode)
            {
                count++;
            }

            offset += wordCount;
        }

        return count;
    }
}

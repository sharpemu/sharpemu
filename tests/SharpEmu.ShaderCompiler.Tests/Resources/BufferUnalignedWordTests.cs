// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// The byte-wise decode of a formatted buffer load reads the four bytes of a component out of
// the two words they span, not out of four separately bounds-checked words.
public sealed class BufferUnalignedWordTests
{
    [Fact]
    public void ByteWiseComponentDecodeReadsTwoBoundsCheckedWords()
    {
        var request = Request(Program(BufferLoad(0, 4, dwords: 1, formatted: true), EndProgram(8)));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        // One bounds-checked buffer word load is one OpArrayLength. A formatted load decodes
        // four components whatever the opcode's width; the byte-wise branch now reads two words
        // per component instead of four, which is eight of these instead of sixteen.
        Assert.Equal(24, CountOpcode(shader.Spirv, SpirvOp.ArrayLength));
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

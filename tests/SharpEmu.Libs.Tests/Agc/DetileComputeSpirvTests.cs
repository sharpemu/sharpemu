// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Structural validation of the GPU detile compute kernel. This cannot run the
// shader without a Vulkan device, but it pins the SPIR-V is well-formed: the
// header is correct, every instruction's word count sums to exactly the module
// length (the classic hand-emit bug), and the compute-specific pieces are
// present (a GLCompute entry point, a LocalSize execution mode, and a runtime
// array for the storage buffers). Full pixel correctness is verified on a GPU.
public sealed class DetileComputeSpirvTests
{
    private const uint SpirvMagic = 0x07230203;
    private const uint SpirvVersion15 = 0x00010500;

    private const ushort OpEntryPoint = 15;
    private const ushort OpExecutionMode = 16;
    private const ushort OpTypeRuntimeArray = 29;
    private const ushort OpFunction = 54;
    private const ushort OpFunctionEnd = 56;

    private const uint ExecutionModelGLCompute = 5;
    private const uint ExecutionModeLocalSize = 17;

    [Fact]
    public void CreateDetileCompute_EmitsWellFormedComputeModule()
    {
        var spirv = SpirvFixedShaders.CreateDetileCompute();

        Assert.True(spirv.Length % sizeof(uint) == 0, "SPIR-V must be a whole number of words.");
        var words = new uint[spirv.Length / sizeof(uint)];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(i * sizeof(uint)));
        }

        Assert.True(words.Length > 5, "Module must have a header plus instructions.");
        Assert.Equal(SpirvMagic, words[0]);
        Assert.Equal(SpirvVersion15, words[1]);

        var bound = words[3];
        Assert.True(bound > 1, "Id bound must be set.");

        var sawComputeEntry = false;
        var sawLocalSize = false;
        var sawRuntimeArray = false;
        var functionCount = 0;
        var functionEndCount = 0;

        var offset = 5;
        while (offset < words.Length)
        {
            var word = words[offset];
            var wordCount = (int)(word >> 16);
            var opcode = (ushort)(word & 0xFFFF);

            Assert.True(wordCount >= 1, $"Instruction at {offset} has a zero word count.");
            Assert.True(
                offset + wordCount <= words.Length,
                $"Instruction at {offset} (op {opcode}, wc {wordCount}) overruns the module.");

            switch (opcode)
            {
                case OpEntryPoint when words[offset + 1] == ExecutionModelGLCompute:
                    sawComputeEntry = true;
                    break;
                case OpExecutionMode
                    when wordCount >= 6 &&
                         words[offset + 2] == ExecutionModeLocalSize &&
                         words[offset + 3] == 8 &&
                         words[offset + 4] == 8 &&
                         words[offset + 5] == 1:
                    sawLocalSize = true;
                    break;
                case OpTypeRuntimeArray:
                    sawRuntimeArray = true;
                    break;
                case OpFunction:
                    functionCount++;
                    break;
                case OpFunctionEnd:
                    functionEndCount++;
                    break;
            }

            offset += wordCount;
        }

        // Word counts must tile the module exactly — a wrong length lands here.
        Assert.Equal(words.Length, offset);
        Assert.True(sawComputeEntry, "Missing a GLCompute OpEntryPoint.");
        Assert.True(sawLocalSize, "Missing an 8x8x1 LocalSize execution mode.");
        Assert.True(sawRuntimeArray, "Missing a runtime array (storage buffers).");
        Assert.Equal(1, functionCount);
        Assert.Equal(1, functionEndCount);
    }
}

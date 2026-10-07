// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// VCC and EXEC carry a per-lane boolean that every write to their registers refreshes.
public sealed class Gen5WaveMaskLaneFlagTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void EmulatedWave64TestsTheLaneBitWithoutComposingTheMask()
    {
        var program = Decode(
        [
            0x7C020300, // v_cmp_lt_f32 vcc, v0, v1  (declares the lane input and writes a mask)
            0xBEFE0300, // s_mov_b32 exec_lo, s0
            0xBF810000, // s_endpgm
        ]);
        Assert.Equal("VCmpLtF32", program.Instructions[0].Opcode);

        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Compute, 0, 0, 0);
        // A 64-thread group is the wave64-on-32-lanes case the translator emulates.
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 64,
            LocalSizeX = 8,
            LocalSizeY = 8,
            LocalSizeZ = 1,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        // The lane's bit lives in one of the two mask registers, so the refresh selects that
        // register and shifts inside it. Composing the 64-bit mask and masking it with a 64-bit
        // lane bit left 27 of these for this two-instruction program; the remaining six build
        // the mask value itself, which stays 64-bit because the guest register pair is.
        Assert.Equal(6, Count64BitBitwiseAnd(shader.Spirv));
    }

    private static int Count64BitBitwiseAnd(byte[] spirv)
    {
        var words = new uint[spirv.Length / 4];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var wideTypes = new HashSet<uint>();
        var count = 0;
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = (int)(words[offset] >> 16);
            var opcode = (ushort)(words[offset] & 0xFFFF);
            if (opcode == (ushort)SpirvOp.TypeInt && words[offset + 2] == 64)
            {
                wideTypes.Add(words[offset + 1]);
            }
            else if (opcode == (ushort)SpirvOp.BitwiseAnd && wideTypes.Contains(words[offset + 1]))
            {
                count++;
            }

            offset += wordCount;
        }

        return count;
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        return program;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < baseAddress || address - baseAddress > int.MaxValue)
            {
                return false;
            }

            offset = (int)(address - baseAddress);
            return offset <= _storage.Length - length;
        }
    }
}

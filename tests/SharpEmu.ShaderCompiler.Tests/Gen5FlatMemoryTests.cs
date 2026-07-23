// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5FlatMemoryTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void FlatLoadUbyteInfersScalarBaseAndCompiles()
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x4000);
        uint[] words =
        [
            // v_add_co_u32 v1, vcc_lo, s12, v6
            0xD70F6A01,
            0x00020C0C,
            // v_add_co_ci_u32_sdwa v2, vcc_lo, 0, s13, vcc_lo
            0x50041AF9,
            0x86860680,
            // flat_load_ubyte v0, v[1:2]
            0xDC200000,
            0x007D0001,
            SEndpgm,
        ];
        var shader = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint)),
                words[index]);
        }
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "FlatLoadUbyte");
        var control = Assert.IsType<Gen5GlobalMemoryControl>(
            instruction.Control);
        Assert.True(control.UsesFlatAddress);
        Assert.Equal(1u, control.VectorAddress);
        Assert.Equal(0u, control.VectorData);
        Assert.Equal(12u, control.ScalarAddress);
        Assert.Equal(
            [
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Scalar(12),
            ],
            instruction.Sources);

        uint[] userData =
        [
            unchecked((uint)ShaderAddress),
            unchecked((uint)(ShaderAddress >> 32)),
        ];
        var state = new Gen5ShaderState(
            program,
            userData,
            null,
            UserDataScalarRegisterBase: 12);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);

        var binding = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.Equal(12u, binding.ScalarAddress);
        Assert.Contains(instruction.Pc, binding.InstructionPcs);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var compiled,
                out var compileError),
            compileError);
        Assert.Contains(
            (ushort)SpirvOp.ISub,
            ReadSpirvOpcodes(compiled.Spirv));
    }

    private static IReadOnlyList<ushort> ReadSpirvOpcodes(byte[] spirv)
    {
        Assert.Equal(0, spirv.Length % sizeof(uint));
        Assert.True(spirv.Length >= 5 * sizeof(uint));
        Assert.Equal(
            0x07230203u,
            BinaryPrimitives.ReadUInt32LittleEndian(spirv));

        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction =
                BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(
                wordCount,
                1,
                (spirv.Length - offset) / sizeof(uint));
            opcodes.Add((ushort)instruction);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
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

        public bool TryWrite(
            ulong virtualAddress,
            ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(
            ulong virtualAddress,
            int length,
            out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}

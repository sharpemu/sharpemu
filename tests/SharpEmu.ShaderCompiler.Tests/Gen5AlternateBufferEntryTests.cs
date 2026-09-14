// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection(PoolStateCollection.Name)]
public sealed class Gen5AlternateBufferEntryTests
{
    private const ulong ShaderAddress = 0x1000;
    private const ulong BufferAddress = 0x76F0D70000;
    private const uint BufferLength = 520;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AlternateVertexFetchKeepsItsEntryDescriptorAndAliases(bool captureBytes, bool hasPrimaryFetch)
    {
        var words = new List<uint>();
        if (hasPrimaryFetch) words.AddRange([0xE0002000, 0x8007071E]);
        words.AddRange([
            0xBF840002,
            0x811C8228,
            0xBF820006,
            0xE0002000, 0x8007071E,
            0xE0002000, 0x80071E1C,
            0xE0002000, 0x8007251D,
            0xBF810000,
        ]);

        Evaluate(words, captureBytes, evaluation =>
        {
            var binding = Assert.Single(evaluation.VertexInputs!);
            Assert.Equal(BufferAddress, binding.BaseAddress);
            Assert.Equal(4u, binding.Stride);
            Assert.Equal((int)BufferLength, binding.DataLength);
            Assert.Equal(0u, binding.Location);
            Assert.Equal(hasPrimaryFetch ? 0u : 12u, binding.Pc);
            Assert.Equal(hasPrimaryFetch ? [20u, 28u, 36u] : [20u, 28u], binding.AliasPcs);
            Assert.Equal(0x80000001u, evaluation.ScalarRegisters[28]);
            Assert.Empty(evaluation.GlobalMemoryBindings);
            if (captureBytes) Assert.All(binding.Data.Take(binding.DataLength), value => Assert.Equal(0x5A, value));
        });
    }

    [Fact]
    public void AlternateVertexFetchUsesRegistersUpdatedBeforeTheBranch()
    {
        Evaluate([
            0x811C901C,
            0xBF840002,
            0x811C8228,
            0xBF820002,
            0xE0002000, 0x8007071E,
            0xBF810000,
        ], captureBytes: false, evaluation =>
        {
            var binding = Assert.Single(evaluation.VertexInputs!);
            Assert.Equal(BufferAddress + 16, binding.BaseAddress);
            Assert.Equal(16u, binding.Pc);
        });
    }

    [Fact]
    public void SeparateBranchFetchesKeepTheirSeparateDescriptors()
    {
        Evaluate([
            0xBF840004,
            0x811C901C,
            0xE0002000, 0x8007071E,
            0xBF820002,
            0xE0002000, 0x80071E1C,
            0xBF810000,
        ], captureBytes: false, evaluation =>
        {
            Assert.Collection(evaluation.VertexInputs!,
                binding =>
                {
                    Assert.Equal(BufferAddress + 16, binding.BaseAddress + binding.OffsetBytes);
                    Assert.Equal(8u, binding.Pc);
                },
                binding =>
                {
                    Assert.Equal(BufferAddress, binding.BaseAddress);
                    Assert.Equal(20u, binding.Pc);
                });
        });
    }

    [Fact]
    public void NestedAlternateVertexFetchKeepsItsOwnEntryRegisters()
    {
        Evaluate([
            0xBF840002,
            0x811C8228,
            0xBF82000A,
            0xBF840005,
            0x811C901C,
            0xE0002000, 0x8007071E,
            0xBF820005,
            0xBF800000,
            0xE0002000, 0x80071E1C,
            0xE0002000, 0x8007251D,
            0xBF810000,
        ], captureBytes: false, evaluation =>
        {
            Assert.Collection(evaluation.VertexInputs!,
                binding =>
                {
                    Assert.Equal(BufferAddress + 16, binding.BaseAddress + binding.OffsetBytes);
                    Assert.Equal(20u, binding.Pc);
                },
                binding =>
                {
                    Assert.Equal(BufferAddress, binding.BaseAddress + binding.OffsetBytes);
                    Assert.Equal(36u, binding.Pc);
                    Assert.Equal([44u], binding.AliasPcs);
                });
        });
    }

    [Fact]
    public void AlternateScanDoesNotReplaceThePrimaryFetchAtTheJoin()
    {
        Evaluate([
            0xBF840002,
            0x811C901C,
            0xBF820001,
            0x811CA01C,
            0xE0002000, 0x8007071E,
            0xBF810000,
        ], captureBytes: false, evaluation =>
        {
            var binding = Assert.Single(evaluation.VertexInputs!);
            Assert.Equal(BufferAddress + 16, binding.BaseAddress + binding.OffsetBytes);
            Assert.Equal(16u, binding.Pc);
        });
    }

    [Fact]
    public void AlternateBufferLoadKeepsItsEntryDescriptor()
    {
        Evaluate([
            0xBF840002,
            0x811C8228,
            0xBF820002,
            0xE0002000, 0x8007071E,
            0xBF810000,
        ], captureBytes: false, evaluation =>
        {
            Assert.Empty(evaluation.VertexInputs!);
            var binding = Assert.Single(evaluation.GlobalMemoryBindings);
            Assert.Equal(BufferAddress, binding.BaseAddress);
            Assert.Equal((ulong)BufferLength, binding.Size);
            Assert.Equal([12u], binding.InstructionPcs);
        }, resolveVertexInputs: false);
    }

    [Fact]
    public void AlternateGlobalLoadKeepsItsEntryAddress()
    {
        Evaluate([
            0xBE9D03FF, (uint)(BufferAddress >> 32),
            0xBF840002,
            0x811C8228,
            0xBF820002,
            0xDC308000, 0x071C001E,
            0xBF810000,
        ], captureBytes: false, evaluation =>
        {
            var binding = Assert.Single(evaluation.GlobalMemoryBindings);
            Assert.Equal(BufferAddress, binding.BaseAddress);
            Assert.Equal([20u], binding.InstructionPcs);
        }, resolveVertexInputs: false);
    }

    private static void Evaluate(
        IReadOnlyList<uint> words,
        bool captureBytes,
        Action<Gen5ShaderEvaluation> checkEvaluation,
        bool resolveVertexInputs = true)
    {
        var memory = new ShaderMemory(words);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var decodeError), decodeError);
        var registers = new uint[41];
        registers[28] = unchecked((uint)BufferAddress);
        registers[29] = (uint)(BufferAddress >> 32) | (4u << 16);
        registers[30] = BufferLength / sizeof(uint);
        registers[31] = 0x00016204;
        registers[40] = 0x7FFFFFFF;
        var state = new Gen5ShaderState(program, registers, null);
        var previousCapture = Gen5ShaderScalarEvaluator.CaptureVertexInputData;
        Gen5ShaderEvaluation? evaluation = null;
        try
        {
            Gen5ShaderScalarEvaluator.CaptureVertexInputData = captureBytes;
            Assert.True(Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out evaluation, out var error,
                resolveVertexInputs: resolveVertexInputs), error);
            checkEvaluation(evaluation);
        }
        finally
        {
            Gen5ShaderScalarEvaluator.CaptureVertexInputData = previousCapture;
            if (evaluation is not null)
            {
                foreach (var binding in evaluation.GlobalMemoryBindings)
                {
                    if (binding.DataPooled) Gen5ShaderScalarEvaluator.GlobalMemoryPool.Return(binding.Data);
                }
                foreach (var binding in evaluation.VertexInputs ?? [])
                {
                    if (binding.DataPooled) Gen5ShaderScalarEvaluator.GlobalMemoryPool.Return(binding.Data);
                }
            }
        }
    }

    private sealed class ShaderMemory : ICpuMemory
    {
        private const ulong MappedBufferLength = 4096;
        private readonly byte[] _shaderBytes;

        public ShaderMemory(IReadOnlyList<uint> words)
        {
            _shaderBytes = new byte[words.Count * sizeof(uint)];
            for (var index = 0; index < words.Count; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_shaderBytes.AsSpan(index * sizeof(uint)), words[index]);
            }
        }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address >= ShaderAddress && address - ShaderAddress + (ulong)destination.Length <= (ulong)_shaderBytes.Length)
            {
                _shaderBytes.AsSpan((int)(address - ShaderAddress), destination.Length).CopyTo(destination);
                return true;
            }

            if (address >= BufferAddress && address - BufferAddress + (ulong)destination.Length <= MappedBufferLength)
            {
                destination.Fill(0x5A);
                return true;
            }
            return false;
        }

        public bool CanRead(ulong address, ulong size) =>
            (size <= (ulong)_shaderBytes.Length && address >= ShaderAddress &&
                address - ShaderAddress <= (ulong)_shaderBytes.Length - size) ||
            (size <= MappedBufferLength && address >= BufferAddress &&
                address - BufferAddress <= MappedBufferLength - size);

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

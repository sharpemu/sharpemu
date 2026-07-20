// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ScalarMemoryFallbackTests
{
    private const ulong ScalarTableAddress = 0x4_4665_4FD0;
    private static readonly object FallbackReaderGate = new();

    [Fact]
    public void ScalarLoadReadsTrackedFallbackMemory()
    {
        var expected = new uint[]
        {
            0x4665_4F70,
            0x0000_0004,
            0x4EA7_FCE0,
            0x0000_0004,
        };
        var table = new byte[expected.Length * sizeof(uint)];
        for (var index = 0; index < expected.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                table.AsSpan(index * sizeof(uint), sizeof(uint)),
                expected[index]);
        }

        var load = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Smem,
            "SLoadDwordx4",
            [],
            [Gen5Operand.Scalar(0)],
            [
                Gen5Operand.Scalar(16),
                Gen5Operand.Scalar(17),
                Gen5Operand.Scalar(18),
                Gen5Operand.Scalar(19),
            ],
            new Gen5ScalarMemoryControl(4, 0, null));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, [load, end]),
            [unchecked((uint)ScalarTableAddress), (uint)(ScalarTableAddress >> 32)],
            null);
        var ctx = new CpuContext(new FakeCpuMemory(0x1000, 0x100), Generation.Gen5);

        lock (FallbackReaderGate)
        {
            var previousReader = Gen5ShaderScalarEvaluator.FallbackMemoryReader;
            try
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = ReadFallback;
                Assert.True(
                    Gen5ShaderScalarEvaluator.TryEvaluate(
                        ctx,
                        state,
                        out var evaluation,
                        out var error),
                    error);
                Assert.Equal(expected, evaluation.ScalarRegisters.Skip(16).Take(4));
            }
            finally
            {
                Gen5ShaderScalarEvaluator.FallbackMemoryReader = previousReader;
            }
        }

        bool ReadFallback(ulong address, Span<byte> destination)
        {
            if (address < ScalarTableAddress)
            {
                return false;
            }

            var offset = address - ScalarTableAddress;
            if (offset + (ulong)destination.Length > (ulong)table.Length)
            {
                return false;
            }

            table.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }
    }
}

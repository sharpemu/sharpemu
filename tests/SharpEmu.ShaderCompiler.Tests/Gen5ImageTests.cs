// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void BvhIntersectRayUsesSplitMimgOpcodeHighBit()
    {
        uint[] words =
        [
            0xF1989F07,
            0x00040303,
            0x43440D3F,
            0x46424140,
            0x00004847,
            SEndpgm,
        ];
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader[(index * sizeof(uint))..],
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "ImageBvhIntersectRay");
        Assert.Equal(Gen5ShaderEncoding.Mimg, instruction.Encoding);
        Assert.Equal(5, instruction.Words.Count);
        Assert.Equal(
            new[]
            {
                Gen5Operand.Vector(3),
                Gen5Operand.Vector(4),
                Gen5Operand.Vector(5),
                Gen5Operand.Vector(6),
            },
            instruction.Destinations);
        var control = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(
            new uint[] { 3, 63, 13, 68, 67, 64, 65, 66, 70, 71, 72 },
            control.AddressRegisters);
        Assert.Equal(16U, control.ScalarResource);
        Assert.Equal(0U, control.ScalarSampler);
        Assert.Equal(0xFU, control.Dmask);
        Assert.Equal(12, instruction.Sources.Count);
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                destination.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            _storage.AsSpan((int)(address - baseAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                source.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            source.CopyTo(
                _storage.AsSpan((int)(address - baseAddress), source.Length));
            return true;
        }
    }
}

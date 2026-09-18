// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareSwizzleTests
{
    [Theory]
    [InlineData(0x801Bu)]
    [InlineData(0x041Fu)]
    [InlineData(0xC020u)]
    [InlineData(0xC420u)]
    [InlineData(0xC021u)]
    [InlineData(0x0050u)]
    [InlineData(0x8000u)]
    public void SupportedModesCompileOnBothBackends(uint selection)
    {
        uint[] words = [0xD8D40000u | selection, (4u << 24) | (4u << 8), 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new InstructionMemory(bytes), Generation.Gen5),
            0x1000, out var program, out var error), error);
        var instruction = program.Instructions[0];
        Assert.Equal("DsSwizzleB32", instruction.Opcode);
        Assert.Equal(new[] { Gen5Operand.Vector(4) }, instruction.Sources);
        Assert.Equal(new[] { Gen5Operand.Vector(4) }, instruction.Destinations);
        Assert.Equal(selection, Assert.IsType<Gen5DataShareControl>(instruction.Control).SingleOffsetBytes);
        var request = Request(CreateReadbackProgram(selection, true));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metal, out var metalError), metalError);
        Assert.Contains("sharpemu_ballot(exec)", metal.Source);
        Assert.Contains("simd_shuffle", metal.Source);
    }

    [Theory]
    [InlineData(0xE000u, false, "unsupported FFT")]
    [InlineData(0xFFFFu, false, "unsupported FFT")]
    [InlineData(0x801Bu, true, "not valid on the global data share")]
    public void UnsupportedFormsRemainRejected(uint selection, bool globalShare, string message)
    {
        var instruction = DataShare(0, "DsSwizzleB32", globalShare, [Gen5Operand.Vector(4)],
            [4], selection & 255, selection >> 8);
        var request = Request(Program(instruction, EndProgram(8)));
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError));
        Assert.Contains(message, spirvError);
        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError));
        Assert.Contains(message, metalError);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(uint selection, bool maskOddLanes)
    {
        return Program(
            Vop2(0, "VAddI32", 4, Operand(1000), Gen5Operand.Vector(0)),
            Vop2(4, "VAndB32", 6, Operand(maskOddLanes ? 1u : 0u), Gen5Operand.Vector(0)),
            Vopc(8, "VCmpxEqU32", Operand(0), 6),
            DataShare(12, "DsSwizzleB32", false, [Gen5Operand.Vector(4)], [4],
                selection & 255, selection >> 8),
            MoveScalar(20, 126, uint.MaxValue),
            MoveScalar(24, 127, uint.MaxValue),
            Vop2(28, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(32, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(40));
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

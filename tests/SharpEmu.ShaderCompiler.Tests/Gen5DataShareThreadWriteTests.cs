// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareThreadWriteTests
{
    public const uint InitialWord = 0xDEADBEEF;

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0xABCD0010u, 0x100u)]
    [InlineData(0xFFFF0100u, 0x204u)]
    public void DecodeUsesScalarBaseAndDataOperand(uint scalarBase, uint byteOffset)
    {
        var write = DecodeWrite(byteOffset);
        Assert.Equal("DsWriteAddtidB32", write.Opcode);
        Assert.Equal(new[] { Gen5Operand.Scalar(124), Gen5Operand.Vector(10) }, write.Sources);
        Assert.Empty(write.Destinations);
        var control = Assert.IsType<Gen5DataShareControl>(write.Control);
        Assert.Equal(byteOffset, control.SingleOffsetBytes);
        Assert.False(control.Gds);
        var request = Request(CreateReadbackProgram(scalarBase, byteOffset, false));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Fact]
    public void GlobalDataShareFormRemainsRejected()
    {
        var write = DecodeWrite(0) with { Control = new Gen5DataShareControl(0, 0, true) };
        var request = Request(Program(MoveScalar(0, 124, 0), MoveVector(4, 10, 7),
            write with { Pc = 8 }, EndProgram(16)));
        Assert.False(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError));
        Assert.Contains("not valid on the global data share", spirvError);
        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError));
        Assert.Contains("not valid on the global data share", metalError);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(uint scalarBase, uint byteOffset, bool maskOddLanes)
    {
        var expectedBase = scalarBase & 0xFFFF;
        return Program(
            MoveScalar(0, 124, scalarBase),
            Vop2(4, "VLshlrevB32", 3, Operand(2), Gen5Operand.Vector(0)),
            Vop2(8, "VAddI32", 3, Operand(expectedBase), Gen5Operand.Vector(3)),
            MoveVector(12, 10, InitialWord),
            DataShare(16, "DsWriteB32", false, [Gen5Operand.Vector(3), Gen5Operand.Vector(10)], [],
                byteOffset & 0xFF, byteOffset >> 8),
            Vop2(24, "VAddI32", 10, Operand(1000), Gen5Operand.Vector(0)),
            Vop2(28, "VAndB32", 6, Operand(maskOddLanes ? 1u : 0u), Gen5Operand.Vector(0)),
            Vopc(32, "VCmpxEqU32", Operand(0), 6),
            DecodeWrite(byteOffset) with { Pc = 36 },
            MoveScalar(44, 126, uint.MaxValue),
            MoveScalar(48, 127, uint.MaxValue),
            DataShare(52, "DsReadB32", false, [Gen5Operand.Vector(3)], [4], byteOffset & 0xFF, byteOffset >> 8),
            Vop2(60, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(64, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5),
            EndProgram(72));
    }

    private static Gen5ShaderInstruction DecodeWrite(uint byteOffset)
    {
        uint[] words = [0xDAC00000u | byteOffset, 10u << 8, 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new InstructionMemory(bytes), Generation.Gen5),
            0x1000, out var program, out var error), error);
        return program.Instructions[0];
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

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// ds_min_f32 and ds_max_f32 take one data register: MEM = min or max of MEM and DATA0. A
// compiler leaves the DATA1 field 0, so it names v0, which usually holds the LDS address.
public sealed class Gen5DataShareFloatMinMaxTests
{
    private const uint PositiveInfinity = 0x7F800000;
    private const uint NegativeInfinity = 0xFF800000;
    private const uint SignBit = 0x80000000;

    public static TheoryData<uint, uint, uint> ReductionCases => new()
    {
        { 64, 0, 0 },
        { 64, 0, SignBit },
        { 64, 1, 0 },
        { 32, 1, 0 },
    };

    [Theory]
    [MemberData(nameof(ReductionCases))]
    public void ReductionProgramCompiles(uint waveSize, uint negativeLaneMask, uint signOfEveryLane)
    {
        _ = waveSize;
        var request = Request(CreateReductionProgram(negativeLaneMask, signOfEveryLane));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    // Lane L holds the float with bits 0x3F800000 + (L << 20), negated where the lane mask or the
    // sign says so. Lane 0 seeds LDS[0] = +inf and LDS[4] = -inf, every lane reduces into them,
    // lane 0 stores both words to the buffer: the bounding-box reduction of a BVH builder.
    public static Gen5ShaderProgram CreateReductionProgram(uint negativeLaneMask, uint signOfEveryLane) => Program(
        Vop2(0, "VLshlrevB32", 8, Operand(20), Gen5Operand.Vector(0)),
        Vop2(4, "VAddI32", 8, Operand(0x3F800000), Gen5Operand.Vector(8)),
        Vop2(12, "VAndB32", 9, Operand(negativeLaneMask), Gen5Operand.Vector(0)),
        Vop2(20, "VLshlrevB32", 9, Operand(31), Gen5Operand.Vector(9)),
        Vop2(24, "VOrB32", 8, Gen5Operand.Vector(8), Gen5Operand.Vector(9)),
        Vop2(28, "VOrB32", 8, Operand(signOfEveryLane), Gen5Operand.Vector(8)),
        MoveVector(36, 0, 0),
        MoveVector(40, 10, PositiveInfinity),
        MoveVector(48, 11, NegativeInfinity),
        MoveScalar(56, 126, 1),
        MoveScalar(60, 127, 0),
        DataShare(64, "DsWriteB32", false, [Gen5Operand.Vector(0), Gen5Operand.Vector(10)], [], 0),
        DataShare(72, "DsWriteB32", false, [Gen5Operand.Vector(0), Gen5Operand.Vector(11)], [], 4),
        MoveScalar(80, 126, uint.MaxValue),
        MoveScalar(84, 127, uint.MaxValue),
        Decode(0xD8480000, 0x00000800) with { Pc = 88 },
        Decode(0xD84C0004, 0x00000800) with { Pc = 96 },
        MoveScalar(104, 126, 1),
        MoveScalar(108, 127, 0),
        DataShare(112, "DsReadB32", false, [Gen5Operand.Vector(0)], [4], 0),
        DataShare(120, "DsReadB32", false, [Gen5Operand.Vector(0)], [6], 4),
        BufferAccess(128, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 0),
        BufferAccess(136, "BufferStoreDword", 4, offset: 4, vectorData: 6, offsetEnabled: true, vectorAddress: 0),
        EndProgram(144));

    public static (uint Minimum, uint Maximum) ExpectedBits(uint waveSize, uint negativeLaneMask, uint signOfEveryLane)
    {
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        for (var lane = 0u; lane < waveSize; lane++)
        {
            var bits = (0x3F800000u + (lane << 20)) | ((lane & negativeLaneMask) << 31) | signOfEveryLane;
            var value = BitConverter.UInt32BitsToSingle(bits);
            minimum = MathF.Min(minimum, value);
            maximum = MathF.Max(maximum, value);
        }

        return (BitConverter.SingleToUInt32Bits(minimum), BitConverter.SingleToUInt32Bits(maximum));
    }

    private static Gen5ShaderInstruction Decode(uint first, uint second)
    {
        uint[] words = [first, second, 0xBF810000];
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

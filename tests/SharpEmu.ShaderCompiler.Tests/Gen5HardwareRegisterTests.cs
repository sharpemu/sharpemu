// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5HardwareRegisterTests
{
    // s_setreg_b32 hwreg(HW_REG_MODE, 0, 32), s2
    private const uint SetFullModeFromS2 = 0xB982F801u;

    [Fact]
    public void SopkOpcode13DecodesSetRegisterSourceWithoutDestination()
    {
        var instruction = Decode(SetFullModeFromS2);

        Assert.Equal(Gen5ShaderEncoding.Sopk, instruction.Encoding);
        Assert.Equal("SSetregB32", instruction.Opcode);
        Assert.Empty(instruction.Destinations);
        Assert.Equal(
            [
                Gen5Operand.Scalar(2),
                new Gen5Operand(Gen5OperandKind.EncodedConstant, 0xF801u),
            ],
            instruction.Sources);
    }

    [Fact]
    public void SetRegisterCompilesOnBothHostBackends()
    {
        var instruction = Decode(SetFullModeFromS2) with { Pc = 0 };
        var program = Program(instruction, EndProgram(sizeof(uint)));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError),
            spirvError);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError),
            metalError);
    }

    [Fact]
    public void CapturedFlatScratchBasePairCompilesOnBothHostBackends()
    {
        // Exact Little Nightmares prologue pair: initialize FLAT_SCRATCH_LO/HI
        // from s96:s97. Scratch-memory lowering itself has separate tests.
        var low = Decode(0xB9E0F814u) with { Pc = 0 };
        var high = Decode(0xB9E1F815u) with { Pc = sizeof(uint) };
        var program = Program(low, high, EndProgram(2 * sizeof(uint)));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
            ScratchDwords = 212,
        };

        Assert.Equal(Gen5Operand.Scalar(96), low.Sources[0]);
        Assert.Equal(new Gen5Operand(Gen5OperandKind.EncodedConstant, 0xF814u), low.Sources[1]);
        Assert.Equal(Gen5Operand.Scalar(97), high.Sources[0]);
        Assert.Equal(new Gen5Operand(Gen5OperandKind.EncodedConstant, 0xF815u), high.Sources[1]);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError),
            spirvError);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError),
            metalError);
    }

    [Fact]
    public void UnsupportedReadOnlyHardwareRegisterWriteFailsClosed()
    {
        // HW_REG_STATUS is read-only and cannot be abstracted as host state.
        var instruction = Decode(0xB982F802u) with { Pc = 0 };
        var program = Program(instruction, EndProgram(sizeof(uint)));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1,
            ThreadCountX = 1,
        };

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError));
        Assert.Contains("unsupported SSetregB32 selector=0xF802", spirvError, StringComparison.Ordinal);
        Assert.False(
            Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError));
        Assert.Contains("unsupported SSetregB32 selector=0xF802", metalError, StringComparison.Ordinal);
    }

    private static Gen5ShaderInstruction Decode(uint word)
    {
        var bytes = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sizeof(uint)), 0xBF810000u);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error),
            error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

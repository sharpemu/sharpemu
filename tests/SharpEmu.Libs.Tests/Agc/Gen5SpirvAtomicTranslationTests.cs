// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Checks atomic instructions after decoding and resource-plan compilation.
public sealed class Gen5SpirvAtomicTranslationTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x1_0000_1000;

    [Fact]
    public void BufferAtomics_EmitAtomicOpcodes()
    {
        // BUFFER_ATOMIC_UMAX v1, BUFFER_ATOMIC_CMPSWAP v[1:2], BUFFER_ATOMIC_INC v1,
        // all against the V# in s[0:3].
        var opcodes = CompileComputeOpcodes(
            [
                0xE0E04008, 0x80000100,
                0xE0C44000, 0x80000100,
                0xE0F00000, 0x80000100,
            ],
            BufferDescriptorRegisters());

        Assert.Contains((ushort)SpirvOp.AtomicUMax, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicIIncrement, opcodes);
    }

    [Fact]
    public void DataShareAtomics_EmitAtomicOpcodes()
    {
        // DS_ADD_RTN_U32 v3, v0, v1; DS_CMPST_RTN_B32 v3, v0, v1, v2; DS_MAX_U32 v0, v1.
        var opcodes = CompileComputeOpcodes(
            [
                0xD8800000, 0x03000100,
                0xD8C00000, 0x03020100,
                0xD8200000, 0x00000100,
            ],
            new Dictionary<uint, uint>());

        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicUMax, opcodes);
    }

    [Fact]
    public void DataShareSingleAddressOffset_UsesBothEncodedBytes()
    {
        // DS_WRITE_B32 v0, v1 offset:0x0808.
        var spirv = CompileComputeSpirv(
            [0xD8340808, 0x00000100],
            new Dictionary<uint, uint>());

        Assert.True(ContainsConstant(spirv, 0x0808u));
    }

    [Fact]
    public void DataShareWaveCounters_EmitOneWaveAtomicAndBroadcast()
    {
        var opcodes = CompileComputeOpcodes(
            [
                0xD8FA0014, 0x07000000,
                0xD8F60014, 0x08000000,
            ],
            new Dictionary<uint, uint>());

        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicISub, opcodes);
        Assert.Contains((ushort)SpirvOp.BitCount, opcodes);
        Assert.Contains((ushort)SpirvOp.GroupNonUniformShuffle, opcodes);
        Assert.Contains((ushort)SpirvOp.ShiftRightLogical, opcodes);
        Assert.Contains((ushort)SpirvOp.ULessThan, opcodes);
    }

    [Fact]
    public void DataShareWaveCounters_InVertexStageUseWaveCountAndBroadcast()
    {
        var opcodes = CompileVertexOpcodes(
            [
                0xD8FA0014, 0x07000000,
                0xD8F60014, 0x08000000,
            ]);

        // Private graphics storage applies the counter once per active wave.
        // Each active lane receives the value from before the update.
        Assert.Contains((ushort)SpirvOp.GroupNonUniformBallot, opcodes);
        Assert.Contains((ushort)SpirvOp.BitCount, opcodes);
        Assert.Contains((ushort)SpirvOp.GroupNonUniformShuffle, opcodes);
        Assert.Contains((ushort)SpirvOp.IAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.ISub, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.AtomicIAdd, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.AtomicISub, opcodes);
    }

    [Fact]
    public void ImageAtomicAdd_EmitsTexelPointerAndAtomicAdd()
    {
        // IMAGE_ATOMIC_ADD v2, v[0:1], s[4:11] dmask:0x1 dim:2D glc against an R32ui T#.
        var opcodes = CompileComputeOpcodes(
            [0xF0442100, 0x00010200],
            new Dictionary<uint, uint>
            {
                // A one-pixel unsigned image with identity component selection.
                [4] = (uint)(BufferAddress >> 8),
                [5] = 20u << 20,
                [7] = (9u << 28) | 0xFACu,
            });

        Assert.Contains((ushort)SpirvOp.ImageTexelPointer, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
    }

    private static Dictionary<uint, uint> BufferDescriptorRegisters() => new()
    {
        // V# in s[0:3]: base=BufferAddress, stride=0, numRecords=64 bytes, type=0.
        [0] = unchecked((uint)BufferAddress),
        [1] = (uint)(BufferAddress >> 32),
        [2] = 64,
        [3] = 0,
    };

    private static HashSet<ushort> CompileComputeOpcodes(
        uint[] programWords,
        Dictionary<uint, uint> userDataRegisters) =>
        CollectOpcodes(CompileComputeSpirv(programWords, userDataRegisters));

    private static byte[] CompileComputeSpirv(
        uint[] programWords,
        Dictionary<uint, uint> userDataRegisters)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1, 0, 16);
        var userData = new uint[16];
        foreach (var (registerIndex, value) in userDataRegisters)
        {
            userData[registerIndex] = value;
        }

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, ResourceTestProgram.Inputs(userData),
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 16),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program), 0);
        var request = new ShaderCompileRequest(plan, resources, layout);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        return shader.Spirv;
    }

    private static HashSet<ushort> CompileVertexOpcodes(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        var request = ResourceTestProgram.Request(program, ShaderStage.Vertex, userDataCount: 16);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error), error);
        return CollectOpcodes(shader.Spirv);
    }

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
    {
        var opcodes = new HashSet<ushort>();
        // 5-word SPIR-V header, then (wordCount << 16 | opcode) packed instructions.
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            opcodes.Add((ushort)word);
            offset += Math.Max((int)(word >> 16), 1) * sizeof(uint);
        }

        return opcodes;
    }

    private static bool ContainsConstant(byte[] spirv, uint expected)
    {
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(word >> 16), 1);
            if ((ushort)word == (ushort)SpirvOp.Constant &&
                wordCount >= 4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (3 * sizeof(uint)), sizeof(uint))) == expected)
            {
                return true;
            }

            offset += wordCount * sizeof(uint);
        }

        return false;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// S_CMPK compares an SGPR with SIMM16: zero-extended for the unsigned compares and
// sign-extended for the signed ones. NGG geometry programs test "no primitives" with
// s_cmpk_le_u32 s4, 0xffff on a packed vertex and primitive count.
public sealed class ScalarCompareImmediateDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint LessEqualSigned = 0x08;
    private const uint LessEqualUnsigned = 0x0E;

    [Theory]
    [InlineData(LessEqualUnsigned, 0x0002_0006u, 0xFFFFu, 0u)]
    [InlineData(LessEqualUnsigned, 0x0000_0010u, 0xFFFFu, 1u)]
    [InlineData(LessEqualUnsigned, 0x0000_FFFFu, 0xFFFFu, 1u)]
    [InlineData(LessEqualSigned, 0x0002_0006u, 0xFFFFu, 0u)]
    [InlineData(LessEqualSigned, 0xFFFF_FFFEu, 0xFFFFu, 1u)]
    public void CompareUsesTheImmediateExtensionOfItsSignedness(uint opcode, uint value, uint immediate, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            DecodeWords(0x8000_0000u | ((0x60u + opcode) << 23) | (8u << 16) | immediate) with { Pc = 0 },
            DecodeWords(0x8509_8081u) with { Pc = 4 },
            MoveVectorFromScalar(8, 2, 9),
            BufferAccess(12, "BufferStoreDword", 4, vectorData: 2),
            EndProgram(20));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = 64;
        registers[8] = value;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, sizeof(uint))));
        harness.AssertNoValidationMessages();
    }

    private static Gen5ShaderInstruction DecodeWords(uint word)
    {
        var bytes = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(sizeof(uint)), 0xBF810000);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var decodeError), decodeError);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || address - 0x1000 + (ulong)destination.Length > (ulong)bytes.Length)
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

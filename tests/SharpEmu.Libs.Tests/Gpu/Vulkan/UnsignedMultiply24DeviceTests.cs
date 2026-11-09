// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// The unsigned 24-bit multiplies use the low 24 bits of both sources: V_MUL_U32_U24 returns the low 32 bits of S0.u24 * S1.u24, V_MUL_HI_U32_U24 the bits above
// them and V_MAD_U32_U24 adds S2. V_MUL_U32_U24 multiplied the whole 32-bit sources instead, so a source with bits above bit 23 changed the product.
public sealed class UnsignedMultiply24DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ResultSize = 32;

    private static readonly (uint Left, uint Right, uint Addend)[] Cases =
    [
        (0x12345678, 0x9ABCDEF0, 0x11111111), (0xFF000001, 0x01000001, 7), (0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF), (0x01000000, 5, 9),
        (0x00FFFFFF, 2, 0), (0x80000005, 0x80000007, 1), (0, 0xFFFFFFFF, 3), (0x00800000, 0x200, 0x100),
    ];

    [Theory]
    [InlineData("VMulU32U24")]
    [InlineData("VMulHiU32U24")]
    [InlineData("VMadU32U24")]
    public void UsesTheLowTwentyFourBitsOfBothSources(string opcode)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = pc });
            pc += 8;
        }

        for (var index = 0u; index < Cases.Length; index++)
        {
            var left = Gen5Operand.Scalar(8 + 3 * index);
            var right = Gen5Operand.Scalar(9 + 3 * index);
            var addend = Gen5Operand.Scalar(10 + 3 * index);
            if (opcode == "VMulU32U24")
            {
                Add(MoveVectorFromScalar(0, 40, 9 + 3 * index));
                Add(Vop2(0, opcode, 16 + index, left, Gen5Operand.Vector(40)));
            }
            else if (opcode == "VMulHiU32U24")
            {
                Add(Vop3(0, opcode, 16 + index, left, right));
            }
            else
            {
                Add(Vop3(0, opcode, 16 + index, left, right, addend));
            }
        }

        Add(BufferAccess(0, "BufferStoreDwordx4", 4, dwords: 4, vectorData: 16));
        Add(BufferAccess(0, "BufferStoreDwordx4", 4, offset: 16, dwords: 4, vectorData: 20));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ResultSize);
        var registers = new uint[256];
        registers[6] = ResultSize;
        for (var index = 0; index < Cases.Length; index++)
        {
            registers[8 + 3 * index] = Cases[index].Left;
            registers[9 + 3 * index] = Cases[index].Right;
            registers[10 + 3 * index] = Cases[index].Addend;
        }

        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ResultSize);
        for (var index = 0; index < Cases.Length; index++)
        {
            var product = (ulong)(Cases[index].Left & 0xFFFFFF) * (Cases[index].Right & 0xFFFFFF);
            var expected = opcode switch
            {
                "VMulU32U24" => (uint)product,
                "VMulHiU32U24" => (uint)(product >> 32),
                _ => unchecked((uint)product + Cases[index].Addend),
            };
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }
}

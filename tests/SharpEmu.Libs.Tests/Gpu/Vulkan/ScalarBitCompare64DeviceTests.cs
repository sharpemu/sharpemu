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

// S_BITCMP0_B64 sets SCC when bit S1[5:0] of the 64-bit value S0 is clear, S_BITCMP1_B64 when it is set. Only the low six bits of the index count.
public sealed class ScalarBitCompare64DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly (ulong Value, uint Index)[] Cases =
    [
        (1, 0), (1, 1), (0x1_0000_0000, 32), (0x1_0000_0000, 0),
        (0x8000_0000_0000_0000, 63), (0x8000_0000_0000_0000, 62), (2, 0x41), (ulong.MaxValue, 0x7F),
    ];

    private const uint ResultSize = 64;

    [Fact]
    public void SetsTheConditionFromTheSelectedBitOfTheWideValue()
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
            // The pair of the value starts on an even register, as the hardware requires.
            var value = Gen5Operand.Scalar(8 + 4 * index);
            var bit = Gen5Operand.Scalar(10 + 4 * index);
            Add(Sopc(0, "SBitcmp0B64", value, bit));
            Add(Sop2(0, "SCselectB32", 40 + 2 * index, Operand(1), Operand(0)));
            Add(Sopc(0, "SBitcmp1B64", value, bit));
            Add(Sop2(0, "SCselectB32", 41 + 2 * index, Operand(1), Operand(0)));
            Add(MoveVectorFromScalar(0, 8 + 2 * index, 40 + 2 * index));
            Add(MoveVectorFromScalar(0, 9 + 2 * index, 41 + 2 * index));
        }

        for (var group = 0u; group < Cases.Length / 2; group++)
            Add(BufferAccess(0, "BufferStoreDwordx4", 4, offset: (int)group * 16, dwords: 4, vectorData: 8 + 4 * group));
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
            registers[8 + 4 * index] = (uint)Cases[index].Value;
            registers[9 + 4 * index] = (uint)(Cases[index].Value >> 32);
            registers[10 + 4 * index] = Cases[index].Index;
        }

        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ResultSize);
        for (var index = 0; index < Cases.Length; index++)
        {
            var isSet = ((Cases[index].Value >> (int)(Cases[index].Index & 63)) & 1) != 0;
            Assert.Equal(isSet ? 0u : 1u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * 8)));
            Assert.Equal(isSet ? 1u : 0u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * 8 + 4)));
        }

        harness.AssertNoValidationMessages();
    }
}

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

// S_ABSDIFF_I32 stores |S0 - S1| and sets SCC when that is not zero. The difference wraps, so the magnitude of the most negative value stays negative.
// The cases are the functional examples of the RDNA 2 instruction set reference plus equal and swapped operands.
public sealed class ScalarAbsoluteDifferenceDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly (uint Left, uint Right, uint Expected)[] Cases =
    [
        (2, 5, 3), (5, 2, 3), (7, 7, 0), (0xFFFFFFFF, 0, 1),
        (0x80000000, 0, 0x80000000), (0x80000000, 1, 0x7FFFFFFF), (0x80000000, 0xFFFFFFFF, 0x7FFFFFFF), (0x80000000, 0xFFFFFFFE, 0x7FFFFFFE),
    ];

    private const uint ResultSize = 64;

    [Fact]
    public void StoresTheMagnitudeAndSetsTheConditionForANonZeroResult()
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
            Add(Sop2(0, "SAbsdiffI32", 40 + 2 * index, Gen5Operand.Scalar(8 + 2 * index), Gen5Operand.Scalar(9 + 2 * index)));
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
            registers[8 + 2 * index] = Cases[index].Left;
            registers[9 + 2 * index] = Cases[index].Right;
        }

        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ResultSize);
        for (var index = 0; index < Cases.Length; index++)
        {
            Assert.Equal(Cases[index].Expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * 8)));
            Assert.Equal(Cases[index].Expected != 0 ? 1u : 0u, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * 8 + 4)));
        }

        harness.AssertNoValidationMessages();
    }
}

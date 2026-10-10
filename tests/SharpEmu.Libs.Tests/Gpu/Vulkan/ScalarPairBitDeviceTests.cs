// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class ScalarPairBitDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    public static IEnumerable<object[]> Inputs()
    {
        foreach (var opcode in new[] { "SBcnt1I32B64", "SFlbitI32B64" })
        foreach (uint destination in new uint[] { 20, 106 })
        {
            yield return [opcode, 0ul, destination];
            yield return [opcode, ulong.MaxValue, destination];
            yield return [opcode, 0x5555555555555555ul, destination];
            yield return [opcode, 0xAAAAAAAAAAAAAAAAul, destination];
            for (var bit = 0; bit < 64; bit++) yield return [opcode, 1ul << bit, destination];
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void PairBitOperationPreservesTheNextRegisterAndSetsTheExpectedCondition(
        string opcode, ulong input, uint destination)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var (plan, resources, layout) = Prepare(Program(
            MoveScalar(0, destination, (uint)input),
            MoveScalar(4, destination + 1, (uint)(input >> 32)),
            Sopc(8, "SCmpEqU32", Operand(1u), Operand(1u)),
            Sop1(12, opcode, destination, Gen5Operand.Scalar(destination)),
            MoveVectorFromScalar(16, 2, destination),
            MoveVectorFromScalar(20, 3, destination + 1),
            Sop2(24, "SCselectB32", 22, Operand(1u), Operand(0u)),
            MoveVectorFromScalar(28, 4, 22),
            BufferAccess(32, "BufferStoreDwordx2", 4, 0, 2, 2),
            BufferAccess(40, "BufferStoreDword", 4, 8, 1, 4),
            EndProgram(48)));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 1, ThreadCountX = 1, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var bytes = runner.ReadBack(output, 0, 12);
        var expected = opcode == "SBcnt1I32B64"
            ? (uint)BitOperations.PopCount(input)
            : input == 0 ? uint.MaxValue : (uint)BitOperations.LeadingZeroCount(input);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal((uint)(input >> 32), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(opcode == "SBcnt1I32B64" && input == 0 ? 0u : 1u,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        harness.AssertNoValidationMessages();
    }
}

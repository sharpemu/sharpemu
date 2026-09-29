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

public sealed class ScalarBitReplicationDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    public static IEnumerable<object[]> Inputs()
    {
        foreach (uint destination in new uint[] { 20, 106 })
        {
            yield return [0u, destination];
            yield return [uint.MaxValue, destination];
            yield return [0x55555555u, destination];
            yield return [0xAAAAAAAAu, destination];
            for (var bit = 0; bit < 32; bit++) yield return [1u << bit, destination];
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void ReplicatesEachBitIntoTheRegisterPair(uint input, uint destination)
        => CheckResult(input, destination, false);

    [Theory]
    [MemberData(nameof(Inputs))]
    public void LeadingZeroCountPreservesTheNextRegister(uint input, uint destination)
        => CheckResult(input, destination, true);

    private void CheckResult(uint input, uint destination, bool countLeadingZeros)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var (plan, resources, layout) = Prepare(Program([
            MoveScalar(0, destination + 1, 0x12345678u),
            MoveScalar(4, destination, input),
            Sopc(8, "SCmpEqU32", Operand(input & 1u), Operand(1u)),
            Sop1(12, countLeadingZeros ? "SFlbitI32B32" : "SBitreplicateB64B32", destination, Gen5Operand.Scalar(destination)),
            MoveVectorFromScalar(16, 2, destination),
            MoveVectorFromScalar(20, 3, destination + 1),
            Sop2(24, "SCselectB32", 22, Operand(1u), Operand(0u)),
            MoveVectorFromScalar(28, 4, 22),
            BufferAccess(32, "BufferStoreDwordx2", 4, 0, 2, 2),
            BufferAccess(40, "BufferStoreDword", 4, 8, 1, 4),
            EndProgram(48),
        ]));
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
        ulong expected = 0;
        for (var bit = 0; bit < 32; bit++) expected |= ((input >> bit) & 1u) * 3UL << (bit * 2);
        if (countLeadingZeros)
            expected = 0x1234567800000000UL | (input == 0 ? uint.MaxValue : (uint)System.Numerics.BitOperations.LeadingZeroCount(input));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt64LittleEndian(runner.ReadBack(output, 0, 8)));
        Assert.Equal(input & 1u, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(output, 8, 4)));
        harness.AssertNoValidationMessages();
    }
}

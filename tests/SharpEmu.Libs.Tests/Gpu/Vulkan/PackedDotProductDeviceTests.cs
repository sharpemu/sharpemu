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

// V_DOT2C_F32_F16 (VOP2 opcode 2) adds the two f16 products to the previous value of its destination and has no clamp of its own:
// D.f32 = S0.f16[0] * S1.f16[0] + S0.f16[1] * S1.f16[1] + D.f32. The translator left the old destination out and clamped the sum to [0, 1].
public sealed class PackedDotProductDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ThreadCount = 32;

    // Every product and sum is exact in f32, so the order of the additions cannot change a result.
    public static TheoryData<uint, uint, float, float> Cases => new()
    {
        { 0x00003C00, 0x00003C00, 2f, 3f },       // (1, 0) . (1, 0) + 2
        { 0xC0003E00, 0x42004000, 10.5f, 7.5f },  // (1.5, -2) . (2, 3) + 10.5
        { 0x3800C400, 0x3C003C00, 1f, -2.5f },    // (-4, 0.5) . (1, 1) + 1
        { 0x56405640, 0x56405640, 0f, 20000f },   // (100, 100) . (100, 100) + 0
        { 0x3C003C00, 0x3C003C00, -0.5f, 1.5f },  // (1, 1) . (1, 1) - 0.5
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void AddsThePreviousDestinationAndDoesNotClamp(uint left, uint right, float previous, float expected)
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

        Add(MoveScalar(0, 20, left));
        Add(MoveVector(0, 12, right));
        Add(MoveVector(0, 8, BitConverter.SingleToUInt32Bits(previous)));
        Add(Vop2(0, "VDot2cF32F16", 8, Gen5Operand.Scalar(20), Gen5Operand.Vector(12)));
        Add(Vop2(0, "VLshlrevB32", 7, Operand(2), Gen5Operand.Vector(0)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 8, offsetEnabled: true, vectorAddress: 7));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = ThreadCount, ThreadCountX = ThreadCount, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ThreadCount * sizeof(uint));
        var registers = new uint[256];
        registers[6] = ThreadCount * sizeof(uint);
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ThreadCount * sizeof(uint));
        for (var lane = 0; lane < ThreadCount; lane++)
        {
            Assert.Equal(BitConverter.SingleToUInt32Bits(expected),
                BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(lane * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }
}

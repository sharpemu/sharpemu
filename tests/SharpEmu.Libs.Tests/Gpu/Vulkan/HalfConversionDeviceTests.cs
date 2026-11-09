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

// V_CVT_U16_F16, V_CVT_I16_F16 and V_CVT_F16_F32 have a 16-bit result, and a 16-bit result is one half of the destination register: the plain (VOP1) and the VOP3 forms
// write the low half and leave the high half as it was, like the 16-bit arithmetic. Compiled programs rely on it. Saros packs the x and y of a lighting probe lookup as
// the two halves of one register, converts x in place and then reads y from the high half; with the whole register written the lookup used row 0 for every pixel. An
// SDWA word places the result itself.
public sealed class HalfConversionDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Previous = 0xDEADBEEF;
    private const uint Half3 = 0x4200;
    private const uint HalfMinus2 = 0xC000;
    private const uint Float2p5 = 0x40200000;

    private static readonly Gen5Vop3Control NoModifiers = new(0, 0, 0, false, 0, null);

    private static Gen5SdwaControl Sdwa(uint destination, uint unused, uint source0) =>
        new(destination, unused, source0, 6, false, false, 0, 0, 0, false, null);

    private static Gen5ShaderInstruction Store(uint register, int index) =>
        BufferAccess(0, "BufferStoreDword", 4, offset: index * 4, vectorData: register);

    [Theory]
    [InlineData("VCvtU16F16", Half3, 0xDEAD0003u)]
    [InlineData("VCvtI16F16", HalfMinus2, 0xDEADFFFEu)]
    [InlineData("VCvtF16F32", Float2p5, 0xDEAD4100u)]
    public void APlainConversionWritesTheLowHalf(string opcode, uint source, uint expected)
    {
        var actual = Run(2,
            MoveVector(0, 40, source),
            MoveVector(0, 43, Previous),
            Vop1(0, opcode, 43, Gen5Operand.Vector(40)),
            Store(43, 0),
            MoveVector(0, 44, Previous),
            Vop3(0, opcode, 44, Gen5Operand.Vector(40)) with { Control = NoModifiers },
            Store(44, 1));
        if (actual is null) return;
        Assert.Equal($"vop1 {expected:X8}, vop3 {expected:X8}", $"vop1 {actual[0]:X8}, vop3 {actual[1]:X8}");
    }

    [Fact]
    public void AnSdwaWordPlacesTheResult()
    {
        var actual = Run(2,
            MoveVector(0, 40, Half3),
            MoveVector(0, 43, Previous),
            Vop1(0, "VCvtU16F16", 43, Gen5Operand.Vector(40)) with { Control = Sdwa(6, 0, 4) },
            Store(43, 0),
            MoveVector(0, 44, Previous),
            Vop1(0, "VCvtU16F16", 44, Gen5Operand.Vector(40)) with { Control = Sdwa(5, 2, 4) },
            Store(44, 1));
        if (actual is null) return;
        Assert.Equal("dword 00000003, high word 0003BEEF", $"dword {actual[0]:X8}, high word {actual[1]:X8}");
    }

    // The sequence of the probe lookup: v44 = (f16 5.75, f16 9.25), x converted in place, then y converted from the high half of v44 into the high half of v45.
    [Fact]
    public void AConversionInPlaceLeavesTheHighHalfForTheNextRead()
    {
        var actual = Run(2,
            MoveVector(0, 40, BitConverter.SingleToUInt32Bits(5.75f)),
            MoveVector(0, 41, BitConverter.SingleToUInt32Bits(9.25f)),
            MoveVector(0, 45, 0x1234),
            Vop2(0, "VCvtPkrtzF16F32", 44, Gen5Operand.Vector(40), Gen5Operand.Vector(41)),
            Vop1(0, "VCvtU16F16", 44, Gen5Operand.Vector(44)),
            Vop1(0, "VCvtU16F16", 45, Gen5Operand.Vector(44)) with { Control = Sdwa(5, 2, 5) },
            Store(44, 0),
            Store(45, 1));
        if (actual is null) return;
        Assert.Equal("x 48A00005, y 00091234", $"x {actual[0]:X8}, y {actual[1]:X8}");
    }

    private uint[]? Run(int results, params Gen5ShaderInstruction[] body)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return null;
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        foreach (var instruction in body.Append(EndProgram(0)))
        {
            instructions.Add(instruction with { Pc = pc });
            pc += 8;
        }

        var size = (uint)(results * sizeof(uint));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(size);
        var registers = new uint[256];
        registers[6] = size;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var bytes = runner.ReadBack(result, 0, size);
        harness.AssertNoValidationMessages();
        return Enumerable.Range(0, results).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)))).ToArray();
    }
}

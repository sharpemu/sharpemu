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

// The output modifiers of a float instruction (clamp and omod) belong to its result whatever the encoding or the width: VOP3 and SDWA carry them, and a 16-bit result
// gets them like a 32-bit one. A 16-bit result is one half of the destination register. The VOP3 forms that have op_sel leave the other half as it was, and the
// instruction set manual says so. An SDWA word says what becomes of the rest of the register: a DWORD select writes all of it, the half and zeros. The SDWA modifiers were
// dropped from the 16-bit results, the 16-bit results kept the old high half under a DWORD select, and the conversions to and from float ignored the modifiers in every
// encoding.
public sealed class FloatOutputModifierDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Previous = 0xDEADBEEF;
    private const uint Half0p5 = 0x3800;
    private const uint Half0p75 = 0x3A00;
    private const uint Half1 = 0x3C00;
    private const uint Half1p5 = 0x3E00;
    private const uint Half2 = 0x4000;
    private const uint Half3 = 0x4200;
    private const uint Half4 = 0x4400;
    private const uint Half8 = 0x4800;
    private const uint HalfMinus1p5 = 0xBE00;
    private const uint HalfMinus2 = 0xC000;

    private sealed record Case(string Label, string Opcode, Gen5InstructionControl? Control, uint Left, uint Right, uint Expected, uint Third = 0, bool Vop3 = false);

    private static Gen5SdwaControl Sdwa(uint destination, uint unused, uint outputModifier = 0, bool clamp = false, uint source0 = 4, uint source1 = 4) =>
        new(destination, unused, source0, source1, false, false, 0, 0, outputModifier, clamp, null);

    private static Gen5Vop3Control Modifiers(uint outputModifier, bool clamp, uint operandSelect = 0) => new(0, 0, outputModifier, clamp, operandSelect, null);

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void SdwaClampSaturatesTheHalfResult()
    {
        Check(
            new Case("mul above one, low field", "VMulF16", Sdwa(4, 2, clamp: true), Half2, Half1p5, 0xDEAD0000 | Half1),
            new Case("mul below zero, low field", "VMulF16", Sdwa(4, 2, clamp: true), HalfMinus2, Half1, 0xDEAD0000),
            new Case("mul inside the range, high field", "VMulF16", Sdwa(5, 2, clamp: true), Half0p5, Half0p5, 0x3400_BEEF),
            new Case("max above one", "VMaxF16", Sdwa(4, 2, clamp: true), Half1p5, Half0p5, 0xDEAD0000 | Half1),
            new Case("min above one", "VMinF16", Sdwa(4, 2, clamp: true), Half2, Half1p5, 0xDEAD0000 | Half1),
            new Case("sub below zero, dword", "VSubF16", Sdwa(6, 0, clamp: true, source0: 6, source1: 6), Half0p5, Half2, 0));
    }

    [Fact]
    public void SdwaOutputModifierScalesTheHalfResult()
    {
        Check(
            new Case("times two", "VAddF16", Sdwa(4, 2, outputModifier: 1), Half1p5, Half0p5, 0xDEAD0000 | Half4),
            new Case("times four", "VAddF16", Sdwa(4, 2, outputModifier: 2), Half1, Half1, 0xDEAD0000 | Half8),
            new Case("divided by two", "VAddF16", Sdwa(4, 2, outputModifier: 3), Half1p5, Half0p5, 0xDEAD0000 | Half1),
            new Case("times two, then clamp", "VAddF16", Sdwa(4, 2, outputModifier: 1, clamp: true), Half0p75, Half0p5, 0xDEAD0000 | Half1));
    }

    [Fact]
    public void ASdwaDwordSelectWritesTheHalfAndZeros()
    {
        Check(
            new Case("dword select", "VAddF16", Sdwa(6, 0), Half1, Half2, Half3),
            new Case("low field padded", "VAddF16", Sdwa(4, 0), Half1, Half2, Half3),
            new Case("unary, dword select", "VRcpF16", Sdwa(6, 0), Half2, 0, Half0p5),
            new Case("min, dword select", "VMinF16", Sdwa(6, 0), Half1, Half2, Half1));
    }

    [Fact]
    public void PreservingFormsKeepTheOtherHalf()
    {
        Check(
            new Case("sdwa low field preserved", "VAddF16", Sdwa(4, 2), Half1, Half2, 0xDEAD0000 | Half3),
            new Case("sdwa high field preserved", "VAddF16", Sdwa(5, 2), Half1, Half2, (Half3 << 16) | 0xBEEF),
            new Case("vop3 op_sel low", "VFmaF16", Modifiers(0, false), Half1, Half2, 0xDEAD0000 | Half3, Half1),
            new Case("vop3 op_sel high", "VFmaF16", Modifiers(0, false, 8), Half1, Half2, (Half3 << 16) | 0xBEEF, Half1),
            new Case("plain vop2", "VAddF16", null, Half1, Half2, 0xDEAD0000 | Half3),
            new Case("plain vop1", "VRcpF16", null, Half2, 0, 0xDEAD0000 | Half0p5));
    }

    [Fact]
    public void ConversionsApplyTheOutputModifiers()
    {
        Check(
            new Case("f16 to f32, sdwa, times two and clamp", "VCvtF32F16", Sdwa(6, 0, 1, true), Half0p75, 0, Bits(1f)),
            new Case("f16 to f32, sdwa, clamp below zero", "VCvtF32F16", Sdwa(6, 0, 0, true), HalfMinus1p5, 0, Bits(0f)),
            new Case("f16 to f32, sdwa, times two", "VCvtF32F16", Sdwa(6, 0, 1, false), Half1p5, 0, Bits(3f)),
            new Case("f16 to f32, vop3, divided by two", "VCvtF32F16", Modifiers(3, false), Half3, 0, Bits(1.5f), Vop3: true),
            new Case("i32 to f32, sdwa, divided by two", "VCvtF32I32", Sdwa(6, 0, 3, false, 6, 6), 5, 0, Bits(2.5f)),
            new Case("i32 to f32, vop3, clamp", "VCvtF32I32", Modifiers(0, true), 3, 0, Bits(1f), Vop3: true),
            new Case("u32 to f32, sdwa, times two", "VCvtF32U32", Sdwa(6, 0, 1, false, 6, 6), 4, 0, Bits(8f)),
            new Case("ubyte1 to f32, vop3, times four", "VCvtF32Ubyte1", Modifiers(2, false), 0x300, 0, Bits(12f), Vop3: true),
            new Case("f32 to f16, sdwa, times two", "VCvtF16F32", Sdwa(6, 0, 1, false, 6, 6), Bits(0.75f), 0, Half1p5),
            new Case("f32 to f16, vop3, clamp", "VCvtF16F32", Modifiers(0, true), Bits(2.5f), 0, 0xDEAD0000 | Half1, Vop3: true),
            new Case("f32 to f16, plain", "VCvtF16F32", null, Bits(2.5f), 0, 0xDEAD4100));
    }

    private void Check(params Case[] cases)
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

        for (var index = 0; index < cases.Length; index++)
        {
            var current = cases[index];
            Add(MoveVector(0, 40, current.Left));
            Add(MoveVector(0, 41, current.Right));
            Add(MoveVector(0, 42, current.Third));
            Add(MoveVector(0, 43, Previous));
            var unary = current.Opcode is "VRcpF16" or "VCvtF32F16" or "VCvtF32I32" or "VCvtF32U32" or "VCvtF32Ubyte1" or "VCvtF16F32";
            var instruction = current.Opcode switch
            {
                "VFmaF16" => Vop3(0, current.Opcode, 43, Gen5Operand.Vector(40), Gen5Operand.Vector(41), Gen5Operand.Vector(42)),
                _ when unary && current.Vop3 => Vop3(0, current.Opcode, 43, Gen5Operand.Vector(40)),
                _ when unary => Vop1(0, current.Opcode, 43, Gen5Operand.Vector(40)),
                _ => Vop2(0, current.Opcode, 43, Gen5Operand.Vector(40), Gen5Operand.Vector(41)),
            };
            Add(current.Control is null ? instruction : instruction with { Control = current.Control });
            Add(BufferAccess(0, "BufferStoreDword", 4, offset: index * 4, vectorData: 43));
        }

        Add(EndProgram(0));
        var size = (uint)(cases.Length * sizeof(uint));
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
        var actual = runner.ReadBack(result, 0, size);
        var failures = new List<string>();
        for (var index = 0; index < cases.Length; index++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(index * sizeof(uint)));
            if (value != cases[index].Expected)
                failures.Add($"{cases[index].Label}: expected 0x{cases[index].Expected:X8}, got 0x{value:X8}");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
        harness.AssertNoValidationMessages();
    }
}

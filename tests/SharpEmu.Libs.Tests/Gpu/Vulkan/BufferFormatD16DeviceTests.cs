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

// Formatted D16 accesses keep the format's component order but move 16-bit values: two components
// per register, half floats for float formats and 16-bit integers for integer ones. The _HI_X forms
// use only the high half of their register.
public sealed class BufferFormatD16DeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint DataResource = 4;
    private const uint OutputResource = 8;
    private const int DataBytes = 64;
    private const byte Sentinel = 0xEE;
    private const uint Format32x4Float = 77;
    private const uint Format8x4Uint = 60;
    private const uint IdentitySwizzle = 0xFAC;

    [Fact]
    public void D16LoadsAndStoresConvertBetweenHalvesAndTheFormat()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var program = Program(BuildInstructions());
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 64);
        Assert.Equal(2, plan.Info.Buffers.Count);
        var specialization = ResourceSpecialization.Default(plan.Info);
        specialization.Buffers[0] = new BufferSpecialization(0, Format32x4Float, IdentitySwizzle);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 64),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        var request = new ShaderCompileRequest(plan, resources, layout) { ExactFloat16ConversionSupported = vulkan.ExactFloat16Conversion, LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var memory = new byte[DataBytes];
        Array.Fill(memory, Sentinel);
        WriteFloats(memory, 0, 1.0f, 2.0f, 0.5f, -3.0f);
        memory[16] = 1;
        memory[17] = 2;
        memory[18] = 3;
        memory[19] = 200;

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var data = runner.CreateBuffer(memory);
        var output = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = DataBytes;
        registers[10] = 64;
        harness.Run(() => runner.Dispatch(
            registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [data, output] },
            1,
            memoryOffsets: [0, 0]));

        var loaded = runner.ReadBack(output, 0, 5 * sizeof(uint));
        Assert.Equal(
            new uint[] { 0x4000_3C00, 0xC200_3800, 0x3C00_1234, 0x0002_0001, 0x00C8_0003 },
            Enumerable.Range(0, 5).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(loaded.AsSpan(index * sizeof(uint)))).ToArray());

        var stored = runner.ReadBack(data, 0, DataBytes);
        Assert.Equal(new[] { 1.0f, 2.0f, 0.5f, -3.0f }, ReadFloats(stored, 32, 4));
        Assert.Equal(2.0f, ReadFloats(stored, 48, 1)[0]);
        Assert.Equal(new byte[] { 1, 2, 3, 200 }, stored[52..56]);
        Assert.All(stored[56..], value => Assert.Equal(Sentinel, value));
        harness.AssertNoValidationMessages();
    }

    private static void WriteFloats(byte[] memory, int offset, params float[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(memory.AsSpan(offset + index * sizeof(float)), values[index]);
        }
    }

    private static float[] ReadFloats(byte[] memory, int offset, int count) =>
        Enumerable.Range(0, count).Select(index => BinaryPrimitives.ReadSingleLittleEndian(memory.AsSpan(offset + index * sizeof(float)))).ToArray();

    private static Gen5ShaderInstruction[] BuildInstructions()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction);
            pc += 8;
        }

        // buffer_load_format_d16_xyzw v[6:7]: four float components become four halves.
        Add(D16(pc, "BufferLoadFormatD16Xyzw", DataResource, 0, registers: 2, components: 4, vectorData: 6));

        // buffer_load_format_d16_hi_x v8: component x lands in the high half, the low half stays.
        Add(MoveVector(pc, 8, 0x1234));
        Add(D16(pc, "BufferLoadFormatD16HiX", DataResource, 0, registers: 1, components: 0, vectorData: 8));

        // tbuffer_load_format_d16_xyzw v[16:17], format 8_8_8_8 uint: 16-bit integers.
        Add(D16(pc, "TBufferLoadFormatD16Xyzw", DataResource, 16, registers: 2, components: 4, vectorData: 16, typedFormat: Format8x4Uint));

        // buffer_store_format_d16_xyzw v[10:11] to bytes 32..47 as 32-bit floats.
        Add(MoveVector(pc, 10, 0x4000_3C00));
        Add(MoveVector(pc, 11, 0xC200_3800));
        Add(D16(pc, "BufferStoreFormatD16Xyzw", DataResource, 32, registers: 2, components: 4, vectorData: 10));

        // buffer_store_format_d16_hi_x v12: the high half (2.0) is stored as component x.
        Add(MoveVector(pc, 12, 0x4000_1234));
        Add(D16(pc, "BufferStoreFormatD16HiX", DataResource, 48, registers: 1, components: 0, vectorData: 12));

        // tbuffer_store_format_d16_xyzw v[14:15], format 8_8_8_8 uint, at bytes 52..55.
        Add(MoveVector(pc, 14, 0x0002_0001));
        Add(MoveVector(pc, 15, 0x00C8_0003));
        Add(D16(pc, "TBufferStoreFormatD16Xyzw", DataResource, 52, registers: 2, components: 4, vectorData: 14, typedFormat: Format8x4Uint));

        uint[] results = [6, 7, 8, 16, 17];
        for (var index = 0; index < results.Length; index++)
        {
            Add(BufferAccess(pc, "BufferStoreDword", OutputResource, index * sizeof(uint), vectorData: results[index]));
        }

        Add(EndProgram(pc));
        return [.. instructions];
    }

    private static Gen5ShaderInstruction D16(
        uint pc, string opcode, uint resource, int offset, uint registers, uint components, uint vectorData, uint typedFormat = 0)
    {
        var plain = BufferAccess(pc, opcode, resource, offset, registers, vectorData);
        var control = Assert.IsType<Gen5BufferMemoryControl>(plain.Control);
        var packed = components != 0;
        return plain with
        {
            Control = control with
            {
                Typed = typedFormat != 0,
                TypedFormat = typedFormat,
                PackedD16 = packed,
                FormatComponentCount = components,
            },
        };
    }
}

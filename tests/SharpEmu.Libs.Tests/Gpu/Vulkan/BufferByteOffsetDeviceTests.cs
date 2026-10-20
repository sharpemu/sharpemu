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

// A guest V# base that is not dword aligned reaches the shader as an aligned view plus a byte
// offset. Byte, short and formatted accesses must land on the exact guest bytes at any offset.
public sealed class BufferByteOffsetDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ByteResource = 4;
    private const uint OutputResource = 8;
    private const int LogicalBytes = 64;
    private const int PhysicalBytes = 80;
    private const byte Sentinel = 0xEE;
    private const uint Format32x4Float = 77;
    private const uint Format8x4Uint = 60;
    private const uint IdentitySwizzle = 0xFAC;

    // Loads write v6 upward: one dword per byte or short load, then four per formatted load.
    private const uint FirstLoadRegister = 6;
    private const int LoadedDwords = 5 + 4 + 4;

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void ByteShortAndFormattedAccessesHonourTheMemoryOffset(uint bias)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var program = Program(BuildInstructions());
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, Hash, 0, 64);
        Assert.Equal(2, plan.Info.Buffers.Count);
        Assert.False(plan.Info.Buffers[0].DwordAddressed);
        Assert.True(plan.Info.Buffers[1].DwordAddressed);

        var specialization = ResourceSpecialization.Default(plan.Info);
        specialization.Buffers[0] = new BufferSpecialization(0, Format32x4Float, IdentitySwizzle);
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 64),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var logical = LogicalPattern();
        var physical = new byte[PhysicalBytes];
        Array.Fill(physical, Sentinel);
        logical.CopyTo(physical, (int)bias);

        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var bytes = runner.CreateBuffer(physical);
        var output = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[6] = LogicalBytes;
        registers[10] = 64;
        harness.Run(() => runner.Dispatch(
            registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [bytes, output] },
            1,
            memoryOffsets: [bias, 0]));

        var expectedLogical = (byte[])logical.Clone();
        var expectedLoads = Simulate(expectedLogical);
        var expectedPhysical = new byte[PhysicalBytes];
        Array.Fill(expectedPhysical, Sentinel);
        expectedLogical.CopyTo(expectedPhysical, (int)bias);

        Assert.Equal(expectedPhysical, runner.ReadBack(bytes, 0, PhysicalBytes));
        var loaded = runner.ReadBack(output, 0, LoadedDwords * sizeof(uint));
        Assert.Equal(
            expectedLoads,
            Enumerable.Range(0, LoadedDwords).Select(index => BinaryPrimitives.ReadUInt32LittleEndian(loaded.AsSpan(index * sizeof(uint)))).ToArray());
        harness.AssertNoValidationMessages();
    }

    private static byte[] LogicalPattern()
    {
        var pattern = new byte[LogicalBytes];
        for (var index = 0; index < pattern.Length; index++)
        {
            pattern[index] = (byte)(index * 37 + 11);
        }

        return pattern;
    }

    // The guest-visible effect of the program on the logical bytes, and the loaded dwords.
    private static uint[] Simulate(byte[] memory)
    {
        memory[0] = 0x11;
        memory[1] = 0x22;
        memory[2] = 0x33;
        memory[3] = 0x44;
        BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan(4), 0xBEEF);
        BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan(9), 0xCAFE);
        var loads = new List<uint>
        {
            memory[19],
            unchecked((uint)(sbyte)memory[17]),
            BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(18)),
            unchecked((uint)(short)BinaryPrimitives.ReadUInt16LittleEndian(memory.AsSpan(16))),
            memory[0],
        };
        for (var component = 0; component < 4; component++)
        {
            loads.Add(memory[24 + component]);
        }

        for (var component = 0; component < 4; component++)
        {
            loads.Add(BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(32 + component * 4)));
        }

        // The formatted store copies the registers v19..v22 over bytes 48..63.
        for (var component = 0; component < 4; component++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(48 + component * 4), FormattedStoreValue(component));
        }

        return [.. loads];
    }

    private static uint FormattedStoreValue(int component) => 0x0102_0304u * (uint)(component + 1);

    private static Gen5ShaderInstruction[] BuildInstructions()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        var pc = 0u;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction);
            pc += 8;
        }

        void StoreByteOrShort(string opcode, uint value, int offset)
        {
            Add(MoveVector(pc, 2, value));
            Add(BufferAccess(pc, opcode, ByteResource, offset, vectorData: 2));
        }

        StoreByteOrShort("BufferStoreByte", 0x11, 0);
        StoreByteOrShort("BufferStoreByte", 0x22, 1);
        StoreByteOrShort("BufferStoreByte", 0x33, 2);
        StoreByteOrShort("BufferStoreByte", 0x44, 3);
        StoreByteOrShort("BufferStoreShort", 0xBEEF, 4);
        StoreByteOrShort("BufferStoreShort", 0xCAFE, 9);

        var register = FirstLoadRegister;
        foreach (var (opcode, offset) in new[]
                 {
                     ("BufferLoadUbyte", 19), ("BufferLoadSbyte", 17), ("BufferLoadUshort", 18), ("BufferLoadSshort", 16),
                     ("BufferLoadUbyte", 0),
                 })
        {
            Add(BufferAccess(pc, opcode, ByteResource, offset, vectorData: register));
            register++;
        }

        Add(Typed(pc, "TBufferLoadFormatXyzw", ByteResource, 24, 4, register, Format8x4Uint));
        register += 4;
        Add(Typed(pc, "TBufferLoadFormatXyzw", ByteResource, 32, 4, register, Format32x4Float));
        register += 4;
        Assert.Equal(FirstLoadRegister + LoadedDwords, register);

        for (var component = 0; component < 4; component++)
        {
            Add(MoveVector(pc, 19 + (uint)component, FormattedStoreValue(component)));
        }

        // An untyped formatted store takes the descriptor format, known only at specialization.
        Add(BufferAccess(pc, "BufferStoreFormatXyzw", ByteResource, 48, 4, 19));

        for (var index = 0; index < LoadedDwords; index++)
        {
            Add(BufferAccess(pc, "BufferStoreDword", OutputResource, index * sizeof(uint), vectorData: FirstLoadRegister + (uint)index));
        }

        Add(EndProgram(pc));
        return [.. instructions];
    }

    private static Gen5ShaderInstruction Typed(uint pc, string opcode, uint resource, int offset, uint dwords, uint vectorData, uint format)
    {
        var plain = BufferAccess(pc, opcode, resource, offset, dwords, vectorData);
        var control = Assert.IsType<Gen5BufferMemoryControl>(plain.Control);
        return plain with { Control = control with { Typed = true, TypedFormat = format } };
    }
}

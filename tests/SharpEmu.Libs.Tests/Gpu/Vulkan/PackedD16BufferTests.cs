// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class PackedD16BufferTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(false, false, 75u)]
    [InlineData(false, true, 75u)]
    [InlineData(true, false, 75u)]
    [InlineData(false, false, 76u)]
    [InlineData(false, true, 76u)]
    [InlineData(true, false, 76u)]
    [InlineData(false, false, 77u)]
    [InlineData(false, true, 77u)]
    [InlineData(true, false, 77u)]
    public void BoundD16_LoadAndStoreUsePackedRegisters(bool typed, bool generic, uint format)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        Gen5ShaderInstruction Access(uint pc, bool store, int offset)
        {
            var instruction = BufferAccess(pc, (typed ? "TBuffer" : "Buffer") +
                (store ? "Store" : "Load") + "FormatD16Xyzw", 0, offset, 2, vectorData: 4);
            return instruction with
            {
                Encoding = typed ? Gen5ShaderEncoding.Mtbuf : Gen5ShaderEncoding.Mubuf,
                Control = ((Gen5BufferMemoryControl)instruction.Control!) with
                { Typed = typed, TypedFormat = typed ? format : 0, PackedD16 = true, FormatComponentCount = 4 },
            };
        }
        var program = Program(MoveVector(0, 6, 0xAABBCCDD), MoveVector(8, 7, 0x11223344),
            Access(16, false, 0), BufferAccess(24, "BufferStoreDwordx4", 0, 0x800, 4, vectorData: 4),
            Access(32, true, 0x100), EndProgram(40));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, 1, 0, 16);
        var registers = new uint[16];
        registers[1] = 2; registers[2] = 0x1000;
        registers[3] = DescriptorConstants.IdentityDestinationSelect | (format << 12);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        static bool NoMemory(ulong address, out uint word) { word = 0; return false; }
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(registers, NoMemory, NoMemory),
            ref snapshot, ref specialization, out var failure), failure.ToString());
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 16),
            false, ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        { ThreadCountX = 1, ThreadCountY = 1, ThreadCountZ = 1, ForceGenericBufferFormats = generic };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var bytes = new byte[0x1000];
        uint[] input = format == 77 ? [0x3F800000u, 0xBF000000u, 0x40400000u, 0x40800000u]
            : [0x00011234u, 0xFFFF8000u, 0x0002ABCDu, 0xFFFF7FFFu];
        for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), input[i]);
        var buffer = runner.CreateBuffer(bytes);
        var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
        { [DescriptorBindingKind.Buffers] = Enumerable.Repeat(buffer, resources.Info.Buffers.Count).ToArray() };
        harness.Run(() => runner.Dispatch(registers, bindings, 1));
        var result = harness.ReadBack(buffer.Handle, 0, (ulong)bytes.Length);
        for (var i = 0; i < 4; i++)
        {
            var half = format == 77 ? BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle(input[i]))
                : input[i] & 0xFFFF;
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0x800 + i / 2 * 4));
            Assert.Equal(half, (packed >> ((i & 1) * 16)) & 0xFFFF);
            var stored = format == 77 ? BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf((ushort)half))
                : format == 76 ? unchecked((uint)(int)(short)half) : half;
            Assert.Equal(stored, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0x100 + i * 4)));
        }
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0x808)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0x80C)));
        harness.AssertNoValidationMessages();
        vulkan.AssertNoValidationMessages();
        output.WriteLine($"Verified D16 load/store typed={typed} generic={generic} format={format} on {vulkan.DeviceName}");
    }
}

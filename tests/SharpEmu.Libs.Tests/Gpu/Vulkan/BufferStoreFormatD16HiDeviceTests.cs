// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// BUFFER_STORE_FORMAT_D16_HI_X stores the 16-bit component held in bits 16..31 of VDATA.
public sealed class BufferStoreFormatD16HiDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Threads = 64;
    private const uint Stride = 16;
    private const uint IdentitySwizzle = 4u | (5u << 3) | (6u << 6) | (7u << 9);

    // 16-bit UINT, SINT and FLOAT formats.
    public static IEnumerable<object[]> Formats()
    {
        for (uint format = 1; format < 128; format++)
        {
            if (Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out var numberFormat) &&
                dataFormat == 2 && numberFormat is 4 or 5 or 7)
            {
                yield return [format];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void StoresTheHighHalfOfTheRegister(uint format)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        // The high half is 1.0 as a half float, and a plain 16-bit integer otherwise; the low half must not be stored.
        const uint value = 0x3C00_1234u;
        var output = Run(vulkan, format, value);
        for (var thread = 0; thread < Threads; thread++)
        {
            var stored = BitConverter.ToUInt32(output, thread * (int)Stride);
            Assert.True(stored == value >> 16, $"format={format} thread={thread} stored=0x{stored:X8}");
        }
    }

    // v1 = v0 * 16; v4 = value; store_format_d16_hi_x v4 to s[8:11] at v1.
    private static byte[] Run(HeadlessVulkan vulkan, uint format, uint value)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            Vop2(0, "VLshlrevB32", 1, Operand(4), Gen5Operand.Vector(0)),
            MoveVector(4, 4, value),
            BufferAccess(12, "BufferStoreFormatD16HiX", 8, 0, 1, vectorData: 4, offsetEnabled: true, vectorAddress: 1),
            EndProgram(20),
        };
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var storeResource = MemoryAccessInfo.NoResource;
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            if (plan.Memory[index].Opcode == "BufferStoreFormatD16HiX") storeResource = plan.Memory[index].Resource;
        }

        resources.Info.Buffers[(int)storeResource].DescriptorFormat = format;
        resources.Info.Buffers[(int)storeResource].DescriptorSwizzle = IdentitySwizzle;
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = Threads };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(Threads * Stride);
        var buffers = new GpuBuffer[Math.Max(resources.Info.Buffers.Count, 1)];
        Array.Fill(buffers, output);
        var registers = new uint[256];
        registers[10] = Threads * Stride;
        registers[11] = (format << 12) | IdentitySwizzle;
        harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers }, 1));
        harness.AssertNoValidationMessages();
        return runner.ReadBack(output, 0, Threads * Stride);
    }
}

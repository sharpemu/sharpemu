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

// BUFFER_LOAD_FORMAT_D16_HI_X converts the formatted X component to 16 bits and writes it
// into bits 16..31 of VDATA, keeping bits 0..15.
public sealed class BufferLoadFormatD16HiDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Threads = 64;
    private const uint Stride = 16;
    private const uint Previous = 0xDEADBEEFu;
    private const uint IdentitySwizzle = 4u | (5u << 3) | (6u << 6) | (7u << 9);

    public static IEnumerable<object[]> Formats()
    {
        for (uint format = 1; format < 128; format++)
        {
            if (Gfx10UnifiedFormat.TryDecode(format, out var dataFormat, out var numberFormat) &&
                ((numberFormat is 4 or 5) || (dataFormat == 4 && numberFormat == 7)))
            {
                yield return [format];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void HiHalfTakesTheConvertedComponentAndKeepsTheLowHalf(uint format)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        Gfx10UnifiedFormat.TryDecode(format, out _, out var numberFormat);
        var isFloat = numberFormat == 7;
        var data = new byte[Threads * Stride];
        if (isFloat)
        {
            // Exactly representable in half, so the conversion has a single answer.
            for (var index = 0; index < data.Length / 4; index++)
            {
                BitConverter.GetBytes(index * 0.5f - 7.0f).CopyTo(data, index * 4);
            }
        }
        else
        {
            data = ImageTestHarness.Pattern((int)(Threads * Stride), seed: format);
        }

        var component = Run(vulkan, data, format, "BufferLoadFormatX", preload: false);
        var actual = Run(vulkan, data, format, "BufferLoadFormatD16HiX", preload: true);
        for (var thread = 0; thread < Threads; thread++)
        {
            var value = BitConverter.ToUInt32(component, thread * (int)Stride);
            var half = isFloat
                ? BitConverter.HalfToUInt16Bits((Half)BitConverter.UInt32BitsToSingle(value))
                : value & 0xFFFFu;
            var expected = (Previous & 0xFFFFu) | (half << 16);
            Assert.True(expected == BitConverter.ToUInt32(actual, thread * (int)Stride),
                $"format={format} thread={thread} expected=0x{expected:X8} actual=0x{BitConverter.ToUInt32(actual, thread * (int)Stride):X8}");
        }
    }

    // v1 = v0 * 16; [v4 = previous;] v4 = load(s[8:11], v1); store v4 to s[4:7] at v1.
    private static byte[] Run(HeadlessVulkan vulkan, byte[] data, uint format, string loadOpcode, bool preload)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            Vop2(0, "VLshlrevB32", 1, Operand(4), Gen5Operand.Vector(0)),
        };
        var pc = 4u;
        if (preload)
        {
            instructions.Add(MoveVector(pc, 4, Previous));
            pc += 8;
        }

        instructions.Add(BufferAccess(pc, loadOpcode, 8, 0, 1, vectorData: 4, offsetEnabled: true, vectorAddress: 1));
        pc += 8;
        instructions.Add(BufferAccess(pc, "BufferStoreDword", 4, 0, 1, vectorData: 4, offsetEnabled: true, vectorAddress: 1));
        pc += 8;
        instructions.Add(EndProgram(pc));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var loadResource = MemoryAccessInfo.NoResource;
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            if (plan.Memory[index].Opcode == loadOpcode) loadResource = plan.Memory[index].Resource;
        }

        resources.Info.Buffers[(int)loadResource].DescriptorFormat = format;
        resources.Info.Buffers[(int)loadResource].DescriptorSwizzle = IdentitySwizzle;
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = Threads };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var input = runner.CreateBuffer(data);
        var output = runner.CreateBuffer(Threads * Stride);
        var buffers = new GpuBuffer[Math.Max(resources.Info.Buffers.Count, 2)];
        for (var index = 0; index < buffers.Length; index++)
        {
            buffers[index] = index == (int)loadResource ? input : output;
        }

        var registers = new uint[256];
        registers[6] = Threads * Stride;
        registers[10] = Threads * Stride;
        registers[11] = (format << 12) | IdentitySwizzle;
        harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers }, 1));
        harness.AssertNoValidationMessages();
        return runner.ReadBack(output, 0, Threads * Stride);
    }
}

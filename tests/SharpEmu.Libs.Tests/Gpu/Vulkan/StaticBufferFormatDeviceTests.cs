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

// A formatted buffer load whose descriptor format is known when the shader is translated
// is emitted for that format only. On the device it must give the run-time decoder's bits
// for every unified format, with a swizzle, and past the end of the buffer.
public sealed class StaticBufferFormatDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Threads = 256;
    private const uint Stride = 16;

    // Identity, and W, X, one, zero.
    private static readonly uint[] Swizzles =
    [
        4u | (5u << 3) | (6u << 6) | (7u << 9),
        7u | (4u << 3) | (1u << 6) | (0u << 9),
    ];

    public static IEnumerable<object[]> Formats()
    {
        for (uint format = 1; format < 128; format++)
        {
            if (Gfx10UnifiedFormat.TryDecode(format, out _, out _))
            {
                yield return [format];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void StaticFormatLoadMatchesTheRuntimeDecoder(uint format)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var data = ImageTestHarness.Pattern((int)(Threads * Stride), seed: format);
        foreach (var swizzle in Swizzles)
        {
            var generic = Run(vulkan, data, format, swizzle, forceGeneric: true);
            var specialized = Run(vulkan, data, format, swizzle, forceGeneric: false);
            for (var offset = 0; offset < generic.Length; offset += 4)
            {
                var expected = BitConverter.ToUInt32(generic, offset);
                var actual = BitConverter.ToUInt32(specialized, offset);
                Assert.True(expected == actual,
                    $"format={format} swizzle=0x{swizzle:X3} thread={offset / 16} component={offset % 16 / 4} " +
                    $"runtime=0x{expected:X8} static=0x{actual:X8}");
            }
        }
    }

    // v1 = v0 * 16; v4..v7 = format_load(s[8:11], v1); store v4..v7 to s[4:7] at v1.
    private static byte[] Run(HeadlessVulkan vulkan, byte[] data, uint format, uint swizzle, bool forceGeneric)
    {
        var program = Program(
            Vop2(0, "VLshlrevB32", 1, Operand(4), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadFormatXyzw", 8, 0, 4, vectorData: 4, offsetEnabled: true, vectorAddress: 1),
            BufferAccess(12, "BufferStoreDwordx4", 4, 0, 4, vectorData: 4, offsetEnabled: true, vectorAddress: 1),
            EndProgram(20));
        var (plan, resources, layout) = Prepare(program);
        var loadResource = MemoryAccessInfo.NoResource;
        for (var index = 0; index < plan.Memory.Count; index++)
        {
            if (plan.Memory[index].Opcode == "BufferLoadFormatXyzw") loadResource = plan.Memory[index].Resource;
        }

        resources.Info.Buffers[(int)loadResource].DescriptorFormat = format;
        resources.Info.Buffers[(int)loadResource].DescriptorSwizzle = swizzle;
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = Threads,
            ForceGenericBufferFormats = forceGeneric,
        };
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
        // The last few elements are past num_records and read the out-of-range value.
        registers[10] = Threads * Stride - 40;
        registers[11] = (format << 12) | swizzle;
        harness.Run(() => runner.Dispatch(registers, new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = buffers }, 1));
        harness.AssertNoValidationMessages();
        return runner.ReadBack(output, 0, Threads * Stride);
    }
}

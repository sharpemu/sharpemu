// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// The wave-wide maximum UE's motion blur takes of its per-lane sample counts: row_shr 1, 2,
// 4 and 8 through DPP, v_permlanex16_b32 across the row pairs, then v_readlane_b32 of lanes
// 31 and 63. The result bounds a sample loop, so a wrong maximum can hang the GPU.
public sealed class WaveMaxReductionDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private static readonly uint[] Reduction =
    [
        0x280404FA, 0xFF011102, // v_max_u32_dpp v2, v2, v2 row_shr:1
        0x280404FA, 0xFF011202, // row_shr:2
        0x280404FA, 0xFF011402, // row_shr:4
        0x280404FA, 0xFF011802, // row_shr:8
        0xD7781004, 0x03058302, // v_permlanex16_b32 v4, v2, -1, -1
        0x28040902,             // v_max_u32 v2, v2, v4
        0xD760000A, 0x00013F02, // v_readlane_b32 s10, v2, 31
        0xD760000B, 0x00017F02, // v_readlane_b32 s11, v2, 63
        0x84940B0A,             // s_max_u32 s20, s10, s11
    ];

    [Theory]
    [InlineData(0x7E040300u, 63u)] // v_mov_b32 v2, v0: the maximum is in the last lane
    [InlineData(0x4C0400BFu, 63u)] // v_sub_nc_u32 v2, 63, v0: the maximum is in lane 0
    public void ReductionYieldsTheWaveMaximum(uint prepareValue, uint expected)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        uint[] words =
        [
            prepareValue,
            .. Reduction,
            0x7E060214,             // v_mov_b32 v3, s20
            0x340A0082,             // v_lshlrev_b32 v5, 2, v0
            0xE0701000, 0x80010305, // buffer_store_dword v3, v5, s[4:7], 0 offen
            0xBF810000,             // s_endpgm
        ];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var decodeError), decodeError);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 64, ThreadCountX = 64, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var output = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [output] }, 1));
        var result = runner.ReadBack(output, 0, 256);
        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(lane * sizeof(uint))));
        }

        harness.AssertNoValidationMessages();
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || address - 0x1000 + (ulong)destination.Length > (ulong)bytes.Length)
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

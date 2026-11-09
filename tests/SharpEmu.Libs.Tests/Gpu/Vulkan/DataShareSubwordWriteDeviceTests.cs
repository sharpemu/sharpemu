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

// DS_WRITE_B8, DS_WRITE_B16 and DS_MSKOR_B32 on the LDS a workgroup shares.
public sealed class DataShareSubwordWriteDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ResultRegister = 4;
    private const uint ResultBytes = 256;

    private static Gen5ShaderInstruction Barrier(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null);

    // Two lanes of one wave store different values into the same LDS byte (or half-word). The hardware keeps one of the
    // two values; it never merges them.
    [Theory]
    [InlineData("DsWriteB8", 0x01u, 0x02u, 0xFFu)]
    [InlineData("DsWriteB16", 0x0101u, 0x0202u, 0xFFFFu)]
    public void TwoLanesStoringOneSubdwordField_LeaveOneLanesValue(string opcode, uint lane0Value, uint lane1Value, uint fieldMask)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var program = Program(
            MoveVector(0, 1, 0),                                   // v1 = LDS address 0 (and the store offset)
            MoveVector(8, 3, 0),                                   // v3 = 0
            MoveVector(16, 2, lane0Value),                         // v2 = lane 0's value ...
            MoveScalar(24, 126, 2), MoveScalar(28, 127, 0),        // ... and lane 1's value under EXEC = lane 1
            MoveVector(32, 2, lane1Value),
            MoveScalar(40, 126, 1),                                // EXEC = lane 0: LDS[0] = 0
            DataShare(48, "DsWriteB32", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(3)], []),
            Barrier(56),
            MoveScalar(60, 126, 3),                                // EXEC = lanes 0 and 1: both store the field
            DataShare(64, opcode, false, [Gen5Operand.Vector(1), Gen5Operand.Vector(2)], []),
            Barrier(72),
            MoveScalar(76, 126, 1),                                // EXEC = lane 0: read the word back and store it
            DataShare(80, "DsReadB32", false, [Gen5Operand.Vector(1)], [4]),
            BufferAccess(88, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 4, offsetEnabled: true, vectorAddress: 1),
            EndProgram(96));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ResultBytes);
        var registers = new uint[256];
        registers[ResultRegister + 2] = ResultBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var word = BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, ResultBytes));
        output.WriteLine($"{opcode}: lanes stored 0x{lane0Value:X} and 0x{lane1Value:X}; LDS word = 0x{word:X8}");
        Assert.Contains(word & fieldMask, new[] { lane0Value, lane1Value });
    }

    // Every lane of a wave masks its own bit into one LDS word: each update lands, as the hardware applies them one
    // after another.
    [Fact]
    public void LanesMaskingOneWord_EachKeepTheirBit()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        var program = Program(
            MoveVector(0, 1, 0),                                   // v1 = LDS address 0 (and the store offset)
            MoveVector(8, 3, 1),
            Vop2(16, "VLshlrevB32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(3)), // v2 = 1 << lane
            MoveVector(24, 4, 0),
            DataShare(32, "DsWriteB32", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(4)], []),
            Barrier(40),
            DataShare(44, "DsMskorB32", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(2)], []),
            Barrier(52),
            DataShare(56, "DsReadB32", false, [Gen5Operand.Vector(1)], [4]),
            BufferAccess(64, "BufferStoreDword", ResultRegister, 0, 1, vectorData: 4, offsetEnabled: true, vectorAddress: 1),
            EndProgram(72));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ResultBytes);
        var registers = new uint[256];
        registers[ResultRegister + 2] = ResultBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(runner.ReadBack(result, 0, ResultBytes)));
    }

    // The sub-dword reads extract and extend the field the address selects, the D16 forms fill one half and keep the
    // other, and the D16_HI writes store the upper half of the data register.
    [Fact]
    public void SubwordReadsAndD16Forms_MatchTheIsa()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;

        Gen5ShaderInstruction Read(uint pc, string opcode, uint destination, uint offset) =>
            DataShare(pc, opcode, false, [Gen5Operand.Vector(1)], [destination], offset);
        var program = Program(
            MoveVector(0, 1, 0),
            MoveScalar(8, 126, 1), MoveScalar(12, 127, 0),
            MoveVector(16, 3, 0x80FF7F01),
            DataShare(24, "DsWriteB32", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(3)], []),
            Read(32, "DsReadU8", 10, 1),            // 0x7F
            Read(40, "DsReadI8", 11, 2),            // 0xFF -> 0xFFFFFFFF
            Read(48, "DsReadU8", 12, 3),            // 0x80
            Read(56, "DsReadI16", 13, 2),           // 0x80FF -> 0xFFFF80FF
            Read(64, "DsReadU16", 14, 0),           // 0x7F01
            MoveVector(72, 15, 0xAAAA5555),
            Read(80, "DsReadI8D16Hi", 15, 3),       // high half 0xFF80, low half kept: 0xFF805555
            MoveVector(88, 16, 0xAAAA5555),
            Read(96, "DsReadU8D16", 16, 1),         // low half 0x007F, high half kept: 0xAAAA007F
            MoveVector(104, 17, 0xAAAA5555),
            Read(112, "DsReadU16D16Hi", 17, 0),     // high half 0x7F01: 0x7F015555
            MoveVector(120, 18, 0x00CD0000),
            DataShare(128, "DsWriteB8D16Hi", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(18)], [], 1),
            MoveVector(136, 19, 0xBEEF0000),
            DataShare(144, "DsWriteB16D16Hi", false, [Gen5Operand.Vector(1), Gen5Operand.Vector(19)], [], 2),
            Read(152, "DsReadB32", 20, 0),          // 0xBEEFCD01
            MoveVector(160, 2, 4),
            BufferAccess(168, "BufferStoreDwordx4", ResultRegister, 0, 4, vectorData: 10, offsetEnabled: true, vectorAddress: 1),
            BufferAccess(176, "BufferStoreDwordx4", ResultRegister, 16, 4, vectorData: 14, offsetEnabled: true, vectorAddress: 1),
            BufferAccess(184, "BufferStoreDwordx3", ResultRegister, 32, 3, vectorData: 18, offsetEnabled: true, vectorAddress: 1),
            EndProgram(192));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ResultBytes);
        var registers = new uint[256];
        registers[ResultRegister + 2] = ResultBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var bytes = runner.ReadBack(result, 0, ResultBytes);
        uint Word(int index) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4));
        uint[] expected = [0x7Fu, 0xFFFFFFFFu, 0x80u, 0xFFFF80FFu, 0x7F01u, 0xFF805555u, 0xAAAA007Fu, 0x7F015555u, 0x00CD0000u, 0xBEEF0000u, 0xBEEFCD01u];
        Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(Word).ToArray());
    }
}

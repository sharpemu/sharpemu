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

// With robustBufferAccess2 on the device a guest buffer load is a plain load (ShaderCompileRequest.RobustBufferAccess2). It
// must read what the bounds-checked load reads: the view's dwords, zero past the view though the buffer goes on, and zero
// at the top of the 32-bit byte space, where a dword index passes 2^30 (a byte offset past 4 GiB).
public sealed class RobustBufferLoadDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint ViewBytes = 64;
    private const int BufferDwords = 64;
    private const uint Lanes = 32;
    private const uint RowBytes = 32;

    // Lane i loads dword i (the view ends at dword 16) and the four dwords from byte 0xFFFFFF80 + 4i (dword indices
    // 0x3FFFFFE0 + i to 0x3FFFFFE3 + i, past 2^30 for the last lanes).
    [Fact]
    public void VectorLoadsReadWhatBoundsCheckedLoadsRead()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, robustBufferAccess2: true)) return;
        var program = Program(
            Vop2(0, "VLshlrevB32", 1, Operand(2), Gen5Operand.Vector(0)),
            BufferAccess(4, "BufferLoadDword", 4, vectorData: 2, offsetEnabled: true, vectorAddress: 1),
            Vop2(12, "VAddU32", 3, new Gen5Operand(Gen5OperandKind.LiteralConstant, 0xFFFF_FF80), Gen5Operand.Vector(1)),
            BufferAccess(20, "BufferLoadDwordx4", 4, dwords: 4, vectorData: 4, offsetEnabled: true, vectorAddress: 3),
            Vop2(28, "VLshlrevB32", 8, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(32, "BufferStoreDword", 8, vectorData: 2, offsetEnabled: true, vectorAddress: 8),
            BufferAccess(40, "BufferStoreDwordx4", 8, offset: 4, dwords: 4, vectorData: 4, offsetEnabled: true, vectorAddress: 8),
            EndProgram(48));

        var robust = Run(vulkan, program, robust: true, Lanes, new uint[256]);
        var bounded = Run(vulkan, program, robust: false, Lanes, new uint[256]);

        Assert.Equal(bounded, robust);
        for (var lane = 0u; lane < Lanes; lane++)
        {
            Assert.Equal(lane < ViewBytes / 4 ? Word(lane) : 0u, Read(robust, lane, 0));
            for (var index = 1u; index <= 4; index++)
            {
                Assert.Equal(0u, Read(robust, lane, index));
            }
        }
    }

    // A scalar load with a dynamic offset: two dwords from byte 60 (the view's last dword, then the first past it) and
    // sixteen from byte 0xFFFFFFFC (dword indices 0x3FFFFFFF to 0x4000000E).
    [Fact]
    public void ScalarLoadsReadWhatBoundsCheckedLoadsRead()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, robustBufferAccess2: true)) return;
        var program = Program(
            ScalarBufferLoad(0, 4, 20, count: 2, dynamicOffsetRegister: 16),
            ScalarBufferLoad(8, 4, 24, count: 16, dynamicOffsetRegister: 17),
            MoveVectorFromScalar(16, 2, 20),
            MoveVectorFromScalar(20, 3, 21),
            MoveVectorFromScalar(24, 4, 24),
            MoveVectorFromScalar(28, 5, 25),
            MoveVectorFromScalar(32, 6, 39),
            BufferAccess(36, "BufferStoreDwordx4", 8, dwords: 4, vectorData: 2),
            BufferAccess(44, "BufferStoreDword", 8, offset: 16, vectorData: 6),
            EndProgram(52));
        var registers = new uint[256];
        registers[16] = 60;
        registers[17] = 0xFFFF_FFFC;

        var robust = Run(vulkan, program, robust: true, 1, registers);
        var bounded = Run(vulkan, program, robust: false, 1, registers);

        Assert.Equal(bounded, robust);
        Assert.Equal([Word(15), 0u, 0u, 0u, 0u], Enumerable.Range(0, 5).Select(index => Read(robust, 0, (uint)index)).ToArray());
    }

    private static uint Word(uint index) => 0xA000_0000 | index;

    private static uint Read(byte[] rows, uint lane, uint word) =>
        BinaryPrimitives.ReadUInt32LittleEndian(rows.AsSpan((int)(lane * RowBytes + word * sizeof(uint))));

    // The source buffer holds 64 dwords behind a 16-dword view; the result buffer has a row of 8 dwords per lane.
    private static byte[] Run(HeadlessVulkan vulkan, Gen5ShaderProgram program, bool robust, uint lanes, uint[] registers)
    {
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = lanes, ThreadCountX = lanes, WaveSize = 32, RobustBufferAccess2 = robust,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var words = new byte[BufferDwords * sizeof(uint)];
        for (var index = 0; index < BufferDwords; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(words.AsSpan(index * sizeof(uint)), Word((uint)index));
        }

        var source = runner.CreateBuffer(words);
        var result = runner.CreateBuffer(Lanes * RowBytes);
        registers[4] = 0x1000;
        registers[6] = ViewBytes;
        registers[8] = 0x2000;
        registers[10] = Lanes * RowBytes;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [source, result] }, 1,
            bufferRanges: [ViewBytes, Lanes * RowBytes]));
        var rows = runner.ReadBack(result, 0, Lanes * RowBytes);
        harness.AssertNoValidationMessages();
        return rows;
    }
}

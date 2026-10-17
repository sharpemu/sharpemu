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

public sealed class MultiplyHighDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint Factor = 0x9E37_79B9;
    private const uint SignedFactor = 0x8000_0005;
    private const ulong Addend = 0x1_FFFF_FFF0;
    private const uint LaneBase = 0x7FFF_FFF0;
    private const uint ThreadCount = 32;

    [Fact]
    public void WideProductsMatchTheHost()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var program = Program(
            Vop2(0, "VAddI32", 10, Operand(LaneBase), Gen5Operand.Vector(0)),
            MoveScalar(8, 8, Factor),
            MoveScalar(16, 9, SignedFactor),
            MoveScalar(24, 10, unchecked((uint)Addend)),
            MoveScalar(32, 11, (uint)(Addend >> 32)),
            Vop3(40, "VMadU64U32", 2, Gen5Operand.Scalar(8), Gen5Operand.Vector(10), Gen5Operand.Scalar(10)),
            Vop3(48, "VMulHiU32", 4, Gen5Operand.Vector(10), Gen5Operand.Scalar(9)),
            Vop3(56, "VMulHiI32", 5, Gen5Operand.Vector(10), Gen5Operand.Scalar(9)),
            Sop2(64, "SMulHiU32", 12, Gen5Operand.Scalar(8), Gen5Operand.Scalar(9)),
            MoveVectorFromScalar(68, 6, 12),
            Vop2(72, "VLshlrevB32", 7, Operand(5), Gen5Operand.Vector(0)),
            BufferAccess(76, "BufferStoreDwordx4", 4, vectorData: 2, dwords: 4, offsetEnabled: true, vectorAddress: 7),
            BufferAccess(84, "BufferStoreDword", 4, offset: 16, vectorData: 6, offsetEnabled: true, vectorAddress: 7),
            EndProgram(92));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = ThreadCount, ThreadCountX = ThreadCount, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(ThreadCount * 32);
        var registers = new uint[256];
        registers[6] = ThreadCount * 32;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, ThreadCount * 32);
        for (var lane = 0u; lane < ThreadCount; lane++)
        {
            var value = LaneBase + lane;
            var mad = (ulong)Factor * value + Addend;
            var row = actual.AsSpan((int)(lane * 32));
            Assert.Equal((uint)mad, BinaryPrimitives.ReadUInt32LittleEndian(row));
            Assert.Equal((uint)(mad >> 32), BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
            Assert.Equal((uint)(((ulong)value * SignedFactor) >> 32), BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            Assert.Equal((uint)(((long)(int)value * unchecked((int)SignedFactor)) >> 32), BinaryPrimitives.ReadUInt32LittleEndian(row[12..]));
            Assert.Equal((uint)(((ulong)Factor * SignedFactor) >> 32), BinaryPrimitives.ReadUInt32LittleEndian(row[16..]));
        }
    }
}

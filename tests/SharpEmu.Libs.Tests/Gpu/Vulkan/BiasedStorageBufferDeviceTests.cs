// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A storage buffer whose guest base is two bytes past a dword: the view starts at the dword before it, and the shader
// adds the two bytes to its byte addresses (the buffer's byte bias).
public sealed class BiasedStorageBufferDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(6ul, 64ul, 8ul)]
    [InlineData(8ul, 64ul, 8ul)]
    [InlineData(6ul, 7ul, 6ul)]
    [InlineData(5ul, 64ul, 8ul)]
    public void TheViewEndsOnADwordInsideTheBuffer(ulong bytes, ulong available, ulong expected) =>
        Assert.Equal(expected, VulkanVideoPresenter.StorageBufferViewRange(bytes, available, uint.MaxValue));

    // Two 16-bit elements at guest offsets 0 and 2: both are in range, the second in the view's last partial dword.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SixteenBitLoadsReadTheLastElementOfABiasedBinding(bool tableBlock)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(
            MoveVector(0, 0, 0),
            BufferAccess(8, "BufferLoadUshort", 4, offset: 0, vectorData: 4),
            BufferAccess(16, "BufferLoadUshort", 4, offset: 2, vectorData: 5),
            BufferAccess(24, "BufferStoreDword", 8, offset: 0, vectorData: 4),
            BufferAccess(32, "BufferStoreDword", 8, offset: 4, vectorData: 5),
            EndProgram(40));
        var input = new byte[16];
        input.AsSpan().Fill(0xEE);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(2), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(4), 0x5678);
        var range = VulkanVideoPresenter.StorageBufferViewRange(4 + 2, (ulong)input.Length, uint.MaxValue);

        var words = Run(program, input, range, recordBytes: 4, tableBlock);

        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(words));
        Assert.Equal(0x5678u, BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(4)));
    }

    // A scalar load ignores the two low bits of the base and of the offset (RDNA2 ISA 7), so with the base two bytes past
    // a dword, offsets 0 and 2 both read the dword that starts at the view, and offset 4 the next one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScalarLoadsIgnoreTheLowBitsOfTheBaseAndTheOffset(bool tableBlock)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(
            MoveVector(0, 0, 0),
            ScalarBufferLoad(8, 4, 20, immediateOffset: 0),
            ScalarBufferLoad(16, 4, 21, immediateOffset: 2),
            ScalarBufferLoad(24, 4, 22, immediateOffset: 4),
            MoveVectorFromScalar(32, 4, 20),
            MoveVectorFromScalar(40, 5, 21),
            MoveVectorFromScalar(48, 6, 22),
            BufferAccess(56, "BufferStoreDword", 8, offset: 0, vectorData: 4),
            BufferAccess(64, "BufferStoreDword", 8, offset: 4, vectorData: 5),
            BufferAccess(72, "BufferStoreDword", 8, offset: 8, vectorData: 6),
            EndProgram(80));
        var input = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(input, 0x11111111);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), 0x22222222);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0x33333333);

        var words = Run(program, input, range: 16, recordBytes: 12, tableBlock);

        Assert.Equal(0x11111111u, BinaryPrimitives.ReadUInt32LittleEndian(words));
        Assert.Equal(0x11111111u, BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(4)));
        Assert.Equal(0x22222222u, BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(8)));
    }

    private byte[] Run(Gen5ShaderProgram program, byte[] input, ulong range, uint recordBytes, bool tableBlock)
    {
        var (plan, resources, layout) = Prepare(program, tableBlock: tableBlock);
        Assert.Equal(tableBlock, layout.UsesTableBlock);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(fixture.Vulkan!);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var source = runner.CreateBuffer(input);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[4] = 0x1000; registers[6] = recordBytes;
        registers[8] = 0x2000; registers[10] = 64;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [source, result] }, 1,
            bufferRanges: [range, 64], memoryOffsets: [2, 0]));
        return runner.ReadBack(result, 0, 12);
    }
}

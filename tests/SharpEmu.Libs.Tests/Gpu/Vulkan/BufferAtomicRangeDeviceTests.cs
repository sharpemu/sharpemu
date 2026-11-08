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

public sealed class BufferAtomicRangeDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    // BUFFER_ATOMIC_ADD with glc returns the value before the add. Out of range the add is dropped and the
    // returned value is zero, as an out-of-range read gives; the destination kept the operand before.
    [Fact]
    public void ReturningAtomicOutOfRangeReturnsZero()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var program = Program(
            MoveScalar(0, 126, 1),
            MoveScalar(4, 127, 0),
            MoveVector(8, 0, 0),
            MoveVector(16, 4, 5),
            MoveVector(24, 5, 9),
            // buffer_atomic_add v4, v0, s[4:7], 0 offen glc: in range.
            Decode(0xE0C85000, 0x80010400) with { Pc = 32 },
            // buffer_atomic_add v5, v0, s[4:7], 0 offen offset:2048 glc: past the 64-byte buffer.
            Decode(0xE0C85800, 0x80010500) with { Pc = 40 },
            BufferAccess(48, "BufferStoreDword", 4, offset: 8, vectorData: 4, offsetEnabled: true, vectorAddress: 0),
            BufferAccess(56, "BufferStoreDword", 4, offset: 12, vectorData: 5, offsetEnabled: true, vectorAddress: 0),
            EndProgram(64));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var initial = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(initial, 7);
        var buffer = runner.CreateBuffer(initial);
        var registers = new uint[256];
        registers[6] = 64;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [buffer] }, 1));
        var words = runner.ReadBack(buffer, 0, 16);
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(words));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(12)));
        harness.AssertNoValidationMessages();
    }

    private static Gen5ShaderInstruction Decode(uint first, uint second)
    {
        uint[] words = [first, second, 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new InstructionMemory(bytes), Generation.Gen5),
            0x1000, out var program, out var error), error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            var offset = address - 0x1000;
            if (address < 0x1000 || offset + (ulong)destination.Length > (ulong)bytes.Length) return false;
            bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

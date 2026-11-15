// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ShaderClockTests
{
    // S_MEMTIME (0x24) and S_MEMREALTIME (0x25) write a 64-bit counter to an SGPR pair and read nothing.
    [Theory]
    [InlineData(0xF4900180u, "SMemtime", 6u)]
    [InlineData(0xF4940100u, "SMemrealtime", 4u)]
    public void AClockReadDecodesToItsRegisterPair(uint word, string opcode, uint destination)
    {
        var memory = new ShaderMemory();
        uint[] words = [word, 0, 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(memory.TryWrite(0x1000, bytes));

        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), 0x1000,
            out var program, out var error), error);

        var clock = program.Instructions[0];
        Assert.Equal(opcode, clock.Opcode);
        Assert.Equal([Gen5Operand.Scalar(destination), Gen5Operand.Scalar(destination + 1)], clock.Destinations);
        Assert.Empty(clock.Sources);
    }

    // S_SLEEP (SOPP 0x0E) pauses the wave, often inside a loop that waits on the clock; it has no effect on results.
    [Fact]
    public void ASleepDecodesAndCompilesToNothing()
    {
        var memory = new ShaderMemory();
        uint[] words = [0xBF8E0001, 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(memory.TryWrite(0x1000, bytes));

        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), 0x1000,
            out var program, out var error), error);
        Assert.Equal("SSleep", program.Instructions[0].Opcode);
        var (plan, resources, layout) = Prepare(program);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out _, out error), error);
    }

    [Fact]
    public void AClockReadUsesTheDeviceClockWhenTheDeviceHasOne()
    {
        var shader = Compile(supportsShaderDeviceClock: true, out var error);

        Assert.True(shader is not null, error);
        var module = new SpirvModuleInspector(shader!);
        Assert.Contains((uint)SpirvCapability.ShaderClockKhr, module.Capabilities);
        Assert.Contains((ushort)SpirvOp.ReadClockKhr, module.Opcodes);
        Assert.Contains("SPV_KHR_shader_clock", Encoding.ASCII.GetString(shader!));
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader!);
    }

    // A program that waits for the counter to pass a value must still finish without a device clock.
    [Fact]
    public void AClockReadWithoutADeviceClockStepsACounter()
    {
        var shader = Compile(supportsShaderDeviceClock: false, out var error);

        Assert.True(shader is not null, error);
        var module = new SpirvModuleInspector(shader!);
        Assert.DoesNotContain((uint)SpirvCapability.ShaderClockKhr, module.Capabilities);
        Assert.DoesNotContain((ushort)SpirvOp.ReadClockKhr, module.Opcodes);
        Assert.Contains(module.Names, name => name.Value == "steppedClock");
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader!);
    }

    private static byte[]? Compile(bool supportsShaderDeviceClock, out string error)
    {
        var program = Program(
            new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Smem, "SMemrealtime", [0u, 0u], [],
                [Gen5Operand.Scalar(4), Gen5Operand.Scalar(5)], null),
            MoveVectorFromScalar(8, 4, 4),
            MoveVectorFromScalar(12, 5, 5),
            BufferAccess(16, "BufferStoreDwordx2", 0, dwords: 2, vectorData: 4),
            EndProgram(24));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { SupportsShaderDeviceClock = supportsShaderDeviceClock };
        return Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out error) ? shader.Spirv : null;
    }

    private sealed class ShaderMemory : ICpuMemory
    {
        private const ulong BaseAddress = 0x1000;
        private readonly byte[] _bytes = new byte[256];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (destination.Length > _bytes.Length || address < BaseAddress || address - BaseAddress > (ulong)(_bytes.Length - destination.Length)) return false;
            _bytes.AsSpan((int)(address - BaseAddress), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (source.Length > _bytes.Length || address < BaseAddress || address - BaseAddress > (ulong)(_bytes.Length - source.Length)) return false;
            source.CopyTo(_bytes.AsSpan((int)(address - BaseAddress), source.Length));
            return true;
        }
    }
}

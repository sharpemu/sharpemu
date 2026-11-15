// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5BufferAtomic64Tests
{
    // BUFFER_ATOMIC_SWAP_X2 and OR_X2 are one 64-bit atomic on the GPU. As two dword atomics, a thread group polling another's
    // {value, status} pair can read the value of an older update with the status of a newer one.
    [Theory]
    [InlineData("BufferAtomicSwapX2", SpirvOp.AtomicExchange, false)]
    [InlineData("BufferAtomicOrX2", SpirvOp.AtomicOr, false)]
    [InlineData("BufferAtomicAndX2", SpirvOp.AtomicAnd, false)]
    [InlineData("BufferAtomicSwapX2", SpirvOp.AtomicExchange, true)]
    [InlineData("BufferAtomicOrX2", SpirvOp.AtomicOr, true)]
    [InlineData("BufferAtomicAndX2", SpirvOp.AtomicAnd, true)]
    public void AnX2AtomicIsOne64BitAtomic(string opcode, SpirvOp atomic, bool runtimeDescriptor)
    {
        var spirv = Compile(X2Program(opcode, runtimeDescriptor), bufferInt64Atomics: true);

        Assert.Contains((uint)SpirvCapability.Int64Atomics, new SpirvModuleInspector(spirv).Capabilities);
        Assert.Equal(1, CountAtomics(spirv, atomic, width: 64));
        Gen5MipStatisticsTests.ValidateWhenAvailable(spirv);
    }

    // A device without shaderBufferInt64Atomics takes no 64-bit buffer atomic, so the pair stays two dword atomics.
    [Theory]
    [InlineData("BufferAtomicSwapX2", false)]
    [InlineData("BufferAtomicOrX2", true)]
    [InlineData("BufferAtomicAndX2", false)]
    public void WithoutTheDeviceFeatureAnX2AtomicStaysTwoDwordAtomics(string opcode, bool runtimeDescriptor)
    {
        var spirv = Compile(X2Program(opcode, runtimeDescriptor), bufferInt64Atomics: false);

        Assert.DoesNotContain((uint)SpirvCapability.Int64Atomics, new SpirvModuleInspector(spirv).Capabilities);
        Assert.Equal(0, CountAtomics(spirv, SpirvOp.AtomicExchange, width: 64) + CountAtomics(spirv, SpirvOp.AtomicOr, width: 64) +
            CountAtomics(spirv, SpirvOp.AtomicAnd, width: 64));
    }

    // Only a program with an X2 atomic changes with the device feature; every other module stays the same, so no other
    // shader compiles again.
    [Theory]
    [InlineData("BufferAtomicAdd", 1u)]
    [InlineData("BufferStoreDwordx2", 2u)]
    public void AProgramWithoutAnX2AtomicIsTheSameModuleEitherWay(string opcode, uint dwords)
    {
        var program = Program(Glc(BufferAccess(0, opcode, 4, dwords: dwords, vectorData: 2)), EndProgram(8));

        Assert.Equal(Compile(program, bufferInt64Atomics: false), Compile(program, bufferInt64Atomics: true));
    }

    // buffer_atomic_<op>_x2 v[2:3], off, s[resource:resource+3], 0 glc. A V# read from a lane-chosen address is a runtime
    // descriptor, reached through device addresses.
    private static Gen5ShaderProgram X2Program(string opcode, bool runtimeDescriptor)
    {
        var program = runtimeDescriptor
            ? Program(ReadFirstLane(0, 12, 0), MoveScalar(4, 13, 0), ScalarLoad(8, 12, 16, count: 4),
                Glc(BufferAccess(16, opcode, 16, dwords: 2, vectorData: 2)), EndProgram(24))
            : Program(Glc(BufferAccess(0, opcode, 4, dwords: 2, vectorData: 2)), EndProgram(8));
        Assert.Equal(runtimeDescriptor, Extract(program).Memory.Find(runtimeDescriptor ? 16u : 0u)!.DeviceDescriptor);
        return program;
    }

    private static Gen5ShaderInstruction Glc(Gen5ShaderInstruction access) =>
        access with { Control = ((Gen5BufferMemoryControl)access.Control!) with { Glc = true } };

    private static byte[] Compile(Gen5ShaderProgram program, bool bufferInt64Atomics)
    {
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { SupportsBufferInt64Atomics = bufferInt64Atomics };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    // The atomics of one opcode whose result is an integer of the given width.
    private static int CountAtomics(byte[] spirv, SpirvOp atomic, uint width)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
        var types = new HashSet<uint>();
        var count = 0;
        for (var offset = 5; offset < words.Length; offset += Math.Max((int)(words[offset] >> 16), 1))
        {
            var opcode = (SpirvOp)(words[offset] & 0xFFFF);
            if (opcode == SpirvOp.TypeInt && words[offset + 2] == width)
            {
                types.Add(words[offset + 1]);
            }
            else if (opcode == atomic && types.Contains(words[offset + 1]))
            {
                count++;
            }
        }

        return count;
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SharedMemoryBarrierTests
{
    [Theory]
    [InlineData(64u, 64u, false, 2)]
    [InlineData(64u, 64u, true, 2)]
    [InlineData(32u, 64u, false, 0)]
    [InlineData(64u, 128u, false, 0)]
    public void SharedMemoryPhases_SynchronizeOnlySingleWaveGroups(
        uint waveSize, uint threadCount, bool explicitBarrier, int expectedBarriers)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Ds, "DsWriteB32", [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], new Gen5DataShareControl(0, 0, false)),
        };
        if (explicitBarrier)
            instructions.Add(new(8, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
        instructions.Add(new(12, Gen5ShaderEncoding.Ds, "DsReadB32", [0u, 0u],
            [Gen5Operand.Vector(0)], [Gen5Operand.Vector(2)], new Gen5DataShareControl(0, 0, false)));
        instructions.Add(new(20, Gen5ShaderEncoding.Sopp, "SEndpgm", [0u], [], [], null));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(new Gen5ShaderProgram(0, instructions), userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            LocalSizeX = threadCount,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var barriers = 0;
        var sharedPointerTypes = new HashSet<uint>();
        var sharedPointers = new HashSet<uint>();
        var sharedReads = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            uint Operand(int index) => BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset + index * 4));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.TypePointer && Operand(2) == 4)
                sharedPointerTypes.Add(Operand(1));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.AccessChain && sharedPointerTypes.Contains(Operand(1)))
                sharedPointers.Add(Operand(2));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.Load && sharedPointers.Contains(Operand(3)))
                sharedReads++;
            if ((instruction & 0xFFFF) == (uint)SpirvOp.ControlBarrier) barriers++;
            offset += checked((int)(instruction >> 16) * 4);
        }
        Assert.Equal(expectedBarriers, barriers);
        Assert.Equal(1, sharedReads);
    }
}

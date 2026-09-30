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
    public void SharedMemoryPhases_SynchronizeAcrossForwardBlocks(
        uint waveSize, uint threadCount, bool splitPhases, int expectedBarriers)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            ResourceTestProgram.MoveScalar(0, 20, 1),
            ResourceTestProgram.Sopc(4, "SCmpEqU32", Gen5Operand.Scalar(20), ResourceTestProgram.Operand(1)),
            ResourceTestProgram.Branch(8, "SCbranchScc0", 0),
            ResourceTestProgram.DataShare(12, "DsWriteB32", false,
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], []),
        };
        if (splitPhases) instructions.Add(ResourceTestProgram.Branch(20, "SBranch", 0));
        instructions.Add(ResourceTestProgram.DataShare(24, "DsReadB32", false, [Gen5Operand.Vector(0)], [2]));
        instructions.Add(ResourceTestProgram.EndProgram(32));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(new Gen5ShaderProgram(0, instructions), userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            LocalSizeX = threadCount,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var barriers = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            if ((instruction & 0xFFFF) == (uint)SpirvOp.ControlBarrier) barriers++;
            offset += checked((int)(instruction >> 16) * 4);
        }
        Assert.Equal(expectedBarriers, barriers);
    }

    [Theory]
    [InlineData(64u, 64u, false, false, 2)]
    [InlineData(64u, 64u, true, false, 2)]
    [InlineData(32u, 64u, false, false, 0)]
    [InlineData(64u, 128u, false, false, 0)]
    [InlineData(64u, 64u, false, true, 2)]
    [InlineData(32u, 64u, false, true, 0)]
    public void SharedMemoryPhases_SynchronizeOnlySingleWaveGroups(
        uint waveSize, uint threadCount, bool explicitBarrier, bool splitBlocks, int expectedBarriers)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Ds, "DsWriteB32", [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], new Gen5DataShareControl(0, 0, false)),
        };
        if (explicitBarrier)
            instructions.Add(new(8, Gen5ShaderEncoding.Sopp, "SBarrier", [0u], [], [], null));
        if (splitBlocks)
            instructions.Add(new(8, Gen5ShaderEncoding.Sopp, "SBranch", [0u], [], [], null));
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

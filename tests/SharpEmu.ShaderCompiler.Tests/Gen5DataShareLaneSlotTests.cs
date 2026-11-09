// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareLaneSlotTests
{
    private const uint SetM0ToZero = 0xB07C0000;
    private const uint SetM0FromS0 = 0xBEFC0300;

    [Theory]
    [InlineData(ShaderStage.Pixel)]
    [InlineData(ShaderStage.Vertex)]
    public void ThreadAddressedSpillsWithAConstantBaseUseOneVariablePerSlot(ShaderStage stage)
    {
        var names = CompileNames(SpillProgram(SetM0ToZero), stage);

        Assert.Contains("ldsLane64", names);
        Assert.DoesNotContain("lds", names);
    }

    [Fact]
    public void ThreadAddressedSpillsWithARegisterBaseKeepTheLdsArray()
    {
        var names = CompileNames(SpillProgram(SetM0FromS0), ShaderStage.Pixel);

        Assert.Contains("lds", names);
        Assert.DoesNotContain(names, name => name.StartsWith("ldsLane", StringComparison.Ordinal));
    }

    [Fact]
    public void ABranchTargetBetweenTheBaseAndTheSpillKeepsTheLdsArray()
    {
        uint[] words =
        [
            SetM0ToZero,
            0xBF850000,
            0xDAC00100, 0x00000A00,
            0xDAC40100, 0x04000000,
            0xBF810000,
        ];
        var names = CompileNames(Decode(words), ShaderStage.Pixel);

        Assert.Contains("lds", names);
    }

    [Fact]
    public void ComputeKeepsSharedLds()
    {
        var names = CompileNames(SpillProgram(SetM0ToZero), ShaderStage.Compute);

        Assert.Contains("lds", names);
    }

    private static Gen5ShaderProgram SpillProgram(uint setM0) => Decode(
    [
        setM0,
        0x7E1402FF, 0x3F800000,
        0xDAC00100, 0x00000A00,
        0xDAC40100, 0x04000000,
        0xBF810000,
    ]);

    private static List<string> CompileNames(Gen5ShaderProgram program, ShaderStage stage)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, stage), out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(index * sizeof(uint)));
        var names = new List<string>();
        for (var index = 5; index < words.Length;)
        {
            var count = (int)(words[index] >> 16);
            Assert.InRange(count, 1, words.Length - index);
            if ((SpirvOp)(words[index] & 0xFFFF) == SpirvOp.Name)
            {
                var bytes = shader.Spirv.AsSpan((index + 2) * sizeof(uint), (count - 2) * sizeof(uint));
                names.Add(Encoding.UTF8.GetString(bytes[..bytes.IndexOf((byte)0)]));
            }
            index += count;
        }
        return names;
    }

    private static Gen5ShaderProgram Decode(uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new InstructionMemory(bytes), Generation.Gen5),
            0x1000, out var program, out var error), error);
        return program;
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

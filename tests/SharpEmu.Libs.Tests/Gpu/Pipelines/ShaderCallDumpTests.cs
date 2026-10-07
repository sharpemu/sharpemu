// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderCallDumpTests
{
    [Fact]
    public void FailedFunction_IsCapturedWithoutReadingItAgain()
    {
        var directory = Directory.CreateTempSubdirectory("sharpemu-call-dump-");
        try
        {
            var path = Path.Combine(directory.FullName, "shader");
            var program = new Gen5ShaderProgram(0x1000, [new(0, Gen5ShaderEncoding.Sop1,
                "SSwappcB64", [0], [Gen5Operand.Scalar(14)], [Gen5Operand.Scalar(14)], null)]);
            var bytes = new byte[0x4008];
            bytes[^1] = 0x7F;
            var failure = new ShaderFunctionReadException(0x2000, new(program, bytes, "read-failed"));
            ShaderCallDump.Write(path, program, new ResourceSnapshot { Buffers = [] }, new MemoryAccessTable(),
                (ulong address, out uint word) => throw new InvalidOperationException("Unexpected guest read."), failure);
            Assert.Equal(bytes, File.ReadAllBytes(path + ".call-failed.bin"));
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".calls.json"));
            var target = Assert.Single(manifest.RootElement.GetProperty("CandidateTargets").EnumerateArray());
            Assert.Equal("0x0000000000002000", target.GetProperty("Address").GetString());
            Assert.False(target.GetProperty("Complete").GetBoolean());
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindCallBuffers_FollowsCopiesButNotOverwrittenAddresses(bool overwrite)
    {
        static Gen5Operand Scalar(uint register) => new(Gen5OperandKind.ScalarRegister, register);
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Smem, "SBufferLoadDwordx4", [], [Scalar(96)],
                [Scalar(16), Scalar(17), Scalar(18), Scalar(19)], new Gen5ScalarMemoryControl(4, 0, 106)),
            new(8, Gen5ShaderEncoding.Sop1, "SMovB32", [], [Scalar(16)], [Scalar(14)], null),
            new(12, Gen5ShaderEncoding.Sop1, "SMovB32", [], [Scalar(17)], [Scalar(15)], null),
        };
        if (overwrite)
            instructions.Add(new(16, Gen5ShaderEncoding.Sop1, "SMovB64", [],
                [new(Gen5OperandKind.LiteralConstant, 0)], [Scalar(14)], null));
        instructions.Add(new(20, Gen5ShaderEncoding.Sop1, "SSwappcB64", [], [Scalar(14)], [Scalar(14)], null));
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 0, ComponentIndex = 0, Kind = MemoryResourceKind.ScalarBuffer, Resource = 2 });
        memory.Add(new() { Pc = 0, ComponentIndex = 1, Kind = MemoryResourceKind.ScalarBuffer, Resource = 2 });
        var buffers = ShaderCallDump.FindCallBuffers(new(0, instructions), memory);
        if (overwrite) Assert.Empty(buffers);
        else Assert.Equal(2u, Assert.Single(buffers));
    }

    [Fact]
    public void ReadPrefix_StopsAtUnreadableWord()
    {
        var reads = 0;
        var bytes = ShaderCallDump.ReadPrefix(0x1000, 16, (ulong address, out uint word) =>
        {
            reads++;
            word = 0x12345678;
            return address == 0x1000;
        });
        Assert.Equal(new byte[] { 0x78, 0x56, 0x34, 0x12 }, bytes);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void ReadPrefix_DoesNotWrapTheAddress()
    {
        var reads = 0;
        var bytes = ShaderCallDump.ReadPrefix(ulong.MaxValue - 3, 16, (ulong address, out uint word) =>
        {
            reads++;
            word = 1;
            return true;
        });
        Assert.Equal(4, bytes.Length);
        Assert.Equal(1, reads);
    }
}

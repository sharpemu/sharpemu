// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderCallLibraryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionRead_CrossesTheOldByteLimitAndStopsAtReturn(bool missingLiteral)
    {
        var reads = new Dictionary<ulong, int>();
        var result = ShaderFunctionReader.Read(0x1000, (ulong address, out uint word) =>
        {
            reads[address] = reads.GetValueOrDefault(address) + 1;
            var offset = address - 0x1000;
            word = offset switch
            {
                < 0x3FFC => 0xBF800000,
                0x3FFC => 0xBE8003FF,
                0x4000 => 0x12345678,
                0x4004 => 0xBE80200E,
                _ => 0,
            };
            return offset <= 0x4004 && (!missingLiteral || offset != 0x4000);
        });
        Assert.Equal(!missingLiteral, result.Complete);
        Assert.Equal(missingLiteral ? 0x4000 : 0x4008, result.Bytes.Length);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.DoesNotContain(0x5008ul, reads.Keys);
        if (!missingLiteral) Assert.Equal("SSetpcB64", result.Program.Instructions[^1].Opcode);
        else Assert.Contains("read-failed", result.Error);
    }

    [Fact]
    public void FunctionRead_DoesNotCrossTheGuestAddressSpace()
    {
        var reads = 0;
        var result = ShaderFunctionReader.Read((1ul << 48) - 4, (ulong address, out uint word) =>
        {
            reads++;
            word = 0xBE8003FF;
            return true;
        });
        Assert.False(result.Complete);
        Assert.Equal(1, reads);
        Assert.Equal(4, result.Bytes.Length);
    }

    [Fact]
    public void FunctionRead_RetainsTheDecoderInstructionLimit()
    {
        var reads = 0;
        var result = ShaderFunctionReader.Read(0x1000, (ulong address, out uint word) =>
        {
            reads++;
            word = 0xBF800000;
            return true;
        });
        Assert.False(result.Complete);
        Assert.Contains("unterminated", result.Error);
        Assert.Equal(262144, reads);
    }

    [Theory]
    [InlineData(65, false, 0)]
    [InlineData(4096, false, 0)]
    [InlineData(65, true, 16)]
    [InlineData(65, true, 1040)]
    public void TableRecords_PreserveArgumentsAndCheckFixedOffsets(int recordCount, bool fixedOffset, int offset)
    {
        var caller = Program(
            Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
            ScalarBufferLoad(4, 0, 16, 4, immediateOffset: offset, dynamicOffsetRegister: fixedOffset ? null : 20),
            Vop1(12, "VMovB32", 111, Operand(0)),
            new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            MoveScalarRegister(20, 14, 16), MoveScalarRegister(24, 15, 17),
            MoveScalarRegister(28, 16, 18), MoveScalarRegister(32, 17, 19),
            Sop1(36, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(40));
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        var snapshot = new ResourceSnapshot { Buffers = [[0x1000, 16u << 16, (uint)recordCount, 0]] };
        var words = new Dictionary<ulong, uint> { [0x200000] = 0xBF800000, [0x200004] = 0xBE80200E };
        for (var record = 0; record < recordCount; record++)
        {
            var address = 0x1000ul + (ulong)record * 16;
            words[address] = 0x200000;
            words[address + 4] = 0;
            words[address + 8] = (uint)record;
            words[address + 12] = 0;
        }
        ShaderCallLibrary Read() => ShaderCallLibrary.Read(caller, memory, snapshot,
            (ulong address, out uint word) => words.TryGetValue(address, out word));
        if (offset >= recordCount * 16)
        {
            Assert.Throws<InvalidOperationException>(Read);
            return;
        }
        var targets = Assert.Single(Read().Calls).Targets;
        Assert.Equal(fixedOffset ? 1 : recordCount, targets.Count);
        Assert.Equal((ulong)(fixedOffset ? offset / 16 : recordCount - 1), targets[^1].Argument);
        Assert.All(targets, target => Assert.Same(targets[0].Program, target.Program));
    }

    [Fact]
    public void Identity_TracksTableCodeAndCallerAddress()
    {
        var caller = Program(
            Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
            ScalarBufferLoad(4, 0, 16, 4, dynamicOffsetRegister: 20),
            new(12, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            MoveScalarRegister(16, 14, 16), MoveScalarRegister(20, 15, 17),
            MoveScalarRegister(24, 16, 18), MoveScalarRegister(28, 17, 19),
            Sop1(32, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(36));
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        var snapshot = new ResourceSnapshot { Buffers = [[0x1000, 16u << 16, 1, 0]] };
        var words = new Dictionary<ulong, uint>
        {
            [0x1000] = 0x2000, [0x1004] = 0, [0x1008] = 0x3000, [0x100C] = 0,
            [0x2000] = 0xBF800000, [0x2004] = 0xBE80200E,
        };
        ShaderCallLibrary Read(Gen5ShaderProgram program) => ShaderCallLibrary.Read(program, memory, snapshot,
            (ulong address, out uint word) => words.TryGetValue(address, out word));
        var first = Read(caller);
        Assert.Equal(first.Identity, Read(caller).Identity);
        var site = Assert.Single(first.Calls);
        Assert.Equal(36ul, site.ReturnAddress);
        Assert.Equal(0x3000ul, Assert.Single(site.Targets).Argument);
        Assert.DoesNotContain(Gen5ShaderCallLinker.Link(caller, first.Calls).Instructions,
            instruction => instruction.Opcode == "SSwappcB64");
        words[0x2000] = 0xBF800001;
        var changedCode = Read(caller);
        Assert.NotEqual(first.Identity, changedCode.Identity);
        words[0x1008] = 0x4000;
        var changedArgument = Read(caller);
        Assert.NotEqual(changedCode.Identity, changedArgument.Identity);
        Assert.NotEqual(changedArgument.Identity, Read(caller with { Address = 0x8000 }).Identity);
        words.Remove(0x100C);
        Assert.Throws<InvalidOperationException>(() => Read(caller));
    }
}

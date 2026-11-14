// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderCallSiteCacheRegressionTests
{
    private static Gen5ShaderProgram UserDataCaller() => Program(
        MoveScalarRegister(0, 14, 8), MoveScalarRegister(4, 15, 9),
        MoveScalarRegister(8, 16, 10), MoveScalarRegister(12, 17, 11),
        Sop1(16, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(20));

    [Fact]
    public void CachedSites_ReadCurrentArgumentsAndCalleeCode()
    {
        var caller = UserDataCaller();
        var words = new Dictionary<ulong, uint>
        {
            [0x2000] = 0xBF800000, [0x2004] = 0xBE80200E,
            [0x4000] = 0xBF800000, [0x4004] = 0xBE80200E,
        };
        var functions = new ShaderFunctionCache(null);
        ShaderCallLibrary Read(uint target, uint argument) => ShaderCallLibrary.Read(
            caller, new MemoryAccessTable(), new ResourceSnapshot { UserData = [target, 0, argument, 0] },
            (ulong address, out uint word) => words.TryGetValue(address, out word), functions,
            runtimeArguments: true, userDataBase: 8);

        var first = Read(0x2000, 0x3000);
        var changedArgument = Read(0x2000, 0x5000);
        Assert.Equal(first.LayoutIdentity, changedArgument.LayoutIdentity);
        Assert.Equal((0x2000ul, 0x5000ul), Assert.Single(changedArgument.Records));
        words[0x2000] = 0xBF800001;
        var changedCode = Read(0x2000, 0x5000);
        Assert.False(first.CurrentCodeHashes[0x2000].SequenceEqual(changedCode.CurrentCodeHashes[0x2000]));
        Assert.NotEqual(first.LayoutIdentity, changedCode.LayoutIdentity);
        Assert.Equal((0x4000ul, 0x6000ul), Assert.Single(Read(0x4000, 0x6000).Records));
    }

    [Fact]
    public void CachedSites_RecheckUserDataBaseWithTheSameCount()
    {
        var caller = UserDataCaller();
        ShaderCallLibrary Read(uint first, uint[] data) => ShaderCallLibrary.Read(caller, new MemoryAccessTable(),
            new ResourceSnapshot { UserData = data }, (ulong address, out uint word) => { word = 0; return false; },
            userDataBase: first);

        for (var repetition = 0; repetition < 2; repetition++)
        {
            Assert.Empty(Assert.Single(Read(8, [0, 0, 123, 0]).Calls).Targets);
            Assert.Throws<InvalidOperationException>(() => Read(9, [0, 0, 123, 0]));
            Assert.Empty(Assert.Single(Read(8, [0, 0, 456, 0]).Calls).Targets);
        }
    }

    [Fact]
    public void CachedTableSites_ReadCurrentTableAndResourceMapping()
    {
        var caller = Program(
            ScalarBufferLoad(0, 4, 14, 2, immediateOffset: 0),
            ScalarBufferLoad(8, 4, 16, 2, immediateOffset: 8),
            new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            Sop1(20, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(24));
        var words = new Dictionary<ulong, uint>
        {
            [0x1000] = 0x2000, [0x1004] = 0, [0x1008] = 0x3000, [0x100C] = 0,
            [0x3000] = 0x2000, [0x3004] = 0, [0x3008] = 0x5000, [0x300C] = 0,
            [0x2000] = 0xBF800000, [0x2004] = 0xBE80200E,
        };
        var snapshot = new ResourceSnapshot { Buffers = [[0x1000, 16u << 16, 1, 0], [0x3000, 16u << 16, 1, 0]] };
        ShaderCallLibrary Read(uint resource)
        {
            var memory = new MemoryAccessTable();
            memory.Add(new() { Pc = 0, Kind = MemoryResourceKind.ScalarBuffer, Resource = resource });
            return ShaderCallLibrary.Read(caller, memory, snapshot,
                (ulong address, out uint word) => words.TryGetValue(address, out word));
        }

        Assert.Equal(0x3000ul, Assert.Single(Assert.Single(Read(0).Calls).Targets).Argument);
        Assert.Equal(0x5000ul, Assert.Single(Assert.Single(Read(1).Calls).Targets).Argument);
        words[0x3008] = 0x6000;
        Assert.Equal(0x6000ul, Assert.Single(Assert.Single(Read(1).Calls).Targets).Argument);
        words.Remove(0x300C);
        Assert.Throws<InvalidOperationException>(() => Read(1));
    }

    [Fact]
    public void CachedSites_DoNotFollowAnOldInstructionList()
    {
        var caller = UserDataCaller();
        var changed = caller with
        {
            Instructions = caller.Instructions.Select(instruction => instruction.Pc == 16
                ? instruction with { AddressOffset = 0x1_0000_0010ul } : instruction).ToArray(),
        };
        var snapshot = new ResourceSnapshot { UserData = [0, 0, 0, 0] };
        ShaderCallLibrary Read(Gen5ShaderProgram selected) => ShaderCallLibrary.Read(selected, new MemoryAccessTable(), snapshot,
            (ulong address, out uint word) => { word = 0; return false; }, userDataBase: 8);

        Assert.Equal(20ul, Assert.Single(Read(caller).Calls).ReturnAddress);
        Assert.Equal(0x1_0000_0014ul, Assert.Single(Read(changed).Calls).ReturnAddress);
        var changedSource = caller with
        {
            Instructions = caller.Instructions.Select(instruction => instruction.Pc == 0
                ? MoveScalarRegister(0, 14, 9) : instruction).ToArray(),
        };
        Assert.Throws<InvalidOperationException>(() => Read(changedSource));
        Assert.Equal(20ul, Assert.Single(Read(caller).Calls).ReturnAddress);
    }

    [Fact]
    public void CachedRefusal_DoesNotReorderEarlierGuestReads()
    {
        var caller = Program(
            MoveScalarRegister(0, 14, 8), MoveScalarRegister(4, 15, 9),
            MoveScalarRegister(8, 16, 10), MoveScalarRegister(12, 17, 11),
            Sop1(16, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
            new(20, Gen5ShaderEncoding.Sop1, "SSwappcB64", [0], [], [], null), EndProgram(24));
        for (var repetition = 0; repetition < 2; repetition++)
        {
            var reads = new List<ulong>();
            var error = Assert.Throws<InvalidOperationException>(() => ShaderCallLibrary.Read(caller, new MemoryAccessTable(),
                new ResourceSnapshot { UserData = [0x2000, 0, 0x3000, 0] },
                (ulong address, out uint word) =>
                {
                    reads.Add(address);
                    word = address == 0x2000 ? 0xBF800000u : 0xBE80200Eu;
                    return address is 0x2000 or 0x2004;
                }, userDataBase: 8));

            Assert.Contains(0x2000ul, reads);
            Assert.Equal("The shader call does not use a supported table record.", error.Message);
        }
    }

    [Fact]
    public void ConcurrentReaders_KeepTheirOwnRangeAndRefusal()
    {
        var caller = UserDataCaller();
        Parallel.For(0, 64, index =>
        {
            var valid = (index & 1) == 0;
            ShaderCallLibrary Read() => ShaderCallLibrary.Read(caller, new MemoryAccessTable(),
                new ResourceSnapshot { UserData = [0, 0, 0, 0] },
                (ulong address, out uint word) => { word = 0; return false; }, userDataBase: valid ? 8u : 9u);
            if (valid) Assert.Empty(Assert.Single(Read().Calls).Targets);
            else Assert.Throws<InvalidOperationException>(Read);
        });
    }
}

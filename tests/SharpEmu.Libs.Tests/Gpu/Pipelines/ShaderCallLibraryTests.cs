// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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

    // A ray tracer's hit table reserves far more records than one frame fills: 360,000 records of 16 bytes, copied at once.
    // Records past the filled ones can name functions the game has since unloaded. With run-time arguments such a record is
    // left out and the tables' other callees still link; a table past 16 MiB is refused.
    [Fact]
    public void RuntimeArguments_ReadALargeHitTableAndLeaveOutUnreadableFunctions()
    {
        const int Records = 360000;
        const ulong TableAddress = 0x10_0000_0000;
        var caller = Program(
            Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
            ScalarBufferLoad(4, 0, 16, 4, dynamicOffsetRegister: 20),
            Vop1(12, "VMovB32", 111, Operand(0)),
            new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            MoveScalarRegister(20, 14, 16), MoveScalarRegister(24, 15, 17),
            MoveScalarRegister(28, 16, 18), MoveScalarRegister(32, 17, 19),
            Sop1(36, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(40));
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        ResourceSnapshot Snapshot(uint records) =>
            new() { Buffers = [[unchecked((uint)TableAddress), (uint)(TableAddress >> 32) | (16u << 16), records, 0]] };
        var table = new byte[Records * 16];
        void Record(int index, ulong target, ulong argument)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(index * 16), target);
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(index * 16 + 8), argument);
        }

        Record(0, 0x200000, 0x5000);
        Record(1, 0x300000, 0x5100);
        Record(4095, 0x200000, 0x5200);
        Record(Records - 1, 0x900000, 0x6000);
        var code = new Dictionary<ulong, uint>
        {
            [0x200000] = 0xBF800000, [0x200004] = 0xBE80200E,
            [0x300000] = 0xBF800000, [0x300004] = 0xBF800000, [0x300008] = 0xBE80200E,
        };
        var tableWordReads = 0;
        bool ReadWord(ulong address, out uint word)
        {
            if (address >= TableAddress && address - TableAddress < (ulong)table.Length)
            {
                tableWordReads++;
                word = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan((int)(address - TableAddress)));
                return true;
            }

            return code.TryGetValue(address, out word);
        }

        bool ReadBytes(ulong address, Span<byte> destination, bool clean)
        {
            if (address < TableAddress || address - TableAddress + (ulong)destination.Length > (ulong)table.Length) return false;
            table.AsSpan((int)(address - TableAddress), destination.Length).CopyTo(destination);
            return true;
        }

        var library = ShaderCallLibrary.Read(caller, memory, Snapshot(Records), ReadWord, runtimeArguments: true, readBytes: ReadBytes);
        Assert.Equal(0, tableWordReads);
        var site = Assert.Single(library.Calls);
        Assert.Equal(new ulong[] { 0x200000, 0x300000 }, site.Targets.Select(static target => target.Address).Order());
        Assert.Equal(new (ulong, ulong)[] { (0x200000, 0x5000), (0x200000, 0x5200), (0x300000, 0x5100) },
            library.Records.Select(static record => (record.Target, record.Argument)).Order());

        // A copy per record reads tables of up to 64 KiB; past 16 MiB a table is refused in either mode.
        Assert.Throws<InvalidOperationException>(() =>
            ShaderCallLibrary.Read(caller, memory, Snapshot(Records), ReadWord, readBytes: ReadBytes));
        Assert.Throws<InvalidOperationException>(() =>
            ShaderCallLibrary.Read(caller, memory, Snapshot((16u << 20) / 16 + 1), ReadWord, runtimeArguments: true, readBytes: ReadBytes));
    }

    // A caller may pass its function pointer and argument in user data: the call registers are copied from user-data
    // registers nothing wrote before, and the target and argument come from the dispatch's user data. Another argument
    // is another library, but the same layout, so it links to the same program.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserDataFunctionPointersLink(bool userDataOverwritten)
    {
        var caller = Program(
            userDataOverwritten ? MoveScalarRegister(0, 2, 40) : Vop1(0, "VMovB32", 1, Operand(0)),
            MoveScalarRegister(4, 14, 0), MoveScalarRegister(8, 15, 1),
            MoveScalarRegister(12, 16, 2), MoveScalarRegister(16, 17, 3),
            Vop1(20, "VMovB32", 5, Operand(0)),
            Sop1(24, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(28));
        var snapshot = new ResourceSnapshot { UserData = [0x200000, 0x11, 0x5000, 0x12] };
        var words = new Dictionary<ulong, uint> { [0x11_0020_0000] = 0xBF800000, [0x11_0020_0004] = 0xBE80200E };
        ShaderCallLibrary Read() => ShaderCallLibrary.Read(caller, new MemoryAccessTable(), snapshot,
            (ulong address, out uint word) => words.TryGetValue(address, out word));
        if (userDataOverwritten)
        {
            Assert.Throws<InvalidOperationException>(Read);
            return;
        }

        var call = Assert.Single(Read().Calls);
        Assert.Equal((14u, 16u), (call.AddressRegister, call.ArgumentRegister));
        var target = Assert.Single(call.Targets);
        Assert.Equal((0x11_0020_0000ul, 0x12_0000_5000ul), (target.Address, target.Argument));
        Assert.Equal("SSetpcB64", target.Program.Instructions[^1].Opcode);
        var other = ShaderCallLibrary.Read(caller, new MemoryAccessTable(), new ResourceSnapshot { UserData = [0x200000, 0x11, 0x6000, 0x12] },
            (ulong address, out uint word) => words.TryGetValue(address, out word));
        Assert.NotEqual(Read().Identity, other.Identity);
        Assert.Equal(Read().LayoutIdentity, other.LayoutIdentity);
    }

    // The call registers may reach user data through copies: the caller saves the argument pair at entry, overwrites
    // the call registers with another call, and copies the second target and the saved pair back for this call.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserDataFunctionPointersLinkThroughCopies(bool savedPairOverwritten)
    {
        var caller = Program(
            Sop1(0, "SMovB64", 84, Gen5Operand.Scalar(14)),
            savedPairOverwritten ? Sop2(4, "SAddU32", 85, Gen5Operand.Scalar(85), Gen5Operand.Scalar(0)) : Vop1(4, "VMovB32", 1, Operand(0)),
            MoveScalarRegister(8, 14, 8), MoveScalarRegister(12, 15, 9), MoveScalarRegister(16, 16, 10), MoveScalarRegister(20, 17, 11),
            Sop1(24, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
            MoveScalarRegister(28, 14, 12), MoveScalarRegister(32, 15, 13), MoveScalarRegister(36, 16, 84), MoveScalarRegister(40, 17, 85),
            Sop1(44, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(48));
        var snapshot = new ResourceSnapshot
        {
            UserData = [0, 0, 0, 0, 0, 0, 0, 0, 0x200000, 0x11, 0x5000, 0x12, 0x200100, 0x11, 0x6000, 0x12],
        };
        var words = new Dictionary<ulong, uint>
        {
            [0x11_0020_0000] = 0xBF800000, [0x11_0020_0004] = 0xBE80200E,
            [0x11_0020_0100] = 0xBF800000, [0x11_0020_0104] = 0xBE80200E,
        };
        ShaderCallLibrary Read() => ShaderCallLibrary.Read(caller, new MemoryAccessTable(), snapshot,
            (ulong address, out uint word) => words.TryGetValue(address, out word));
        if (savedPairOverwritten)
        {
            Assert.Throws<InvalidOperationException>(Read);
            return;
        }

        var calls = Read().Calls;
        Assert.Equal(2, calls.Count);
        var first = Assert.Single(calls[0].Targets);
        Assert.Equal((0x11_0020_0000ul, 0x12_0000_5000ul), (first.Address, first.Argument));
        var second = Assert.Single(calls[1].Targets);
        Assert.Equal((0x11_0020_0100ul, 0x12_0000_6000ul), (second.Address, second.Argument));
    }

    // A site linked with run-time records holds each callee once, so its table may hold more records than a site linked
    // per record.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunTimeRecordTables_ReadPastThePerRecordLimit(bool runtimeArguments)
    {
        const int recordCount = 11284;
        var caller = Program(
            Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
            ScalarBufferLoad(4, 0, 16, 4, dynamicOffsetRegister: 20),
            new(12, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            MoveScalarRegister(16, 14, 16), MoveScalarRegister(20, 15, 17),
            MoveScalarRegister(24, 16, 18), MoveScalarRegister(28, 17, 19),
            Sop1(32, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(36));
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        var snapshot = new ResourceSnapshot { Buffers = [[0x1000, 16u << 16, recordCount, 0]] };
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
            (ulong address, out uint word) => words.TryGetValue(address, out word), runtimeArguments: runtimeArguments);
        if (!runtimeArguments)
        {
            Assert.Throws<InvalidOperationException>(Read);
            return;
        }

        var library = Read();
        Assert.Single(Assert.Single(library.Calls).Targets);
        Assert.Equal(recordCount, library.Records.Length);
    }

    // A game rebuilds its tables with the argument data at new addresses. Arguments anywhere keep the layout identity
    // (and so the linked program) while Arguments follow them; records that stop or start sharing an argument, or a null
    // argument that becomes one, are another layout.
    [Fact]
    public void LayoutIdentity_FollowsMovedArguments()
    {
        var caller = Program(
            Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
            ScalarBufferLoad(4, 0, 16, 4, dynamicOffsetRegister: 20),
            Vop1(12, "VMovB32", 111, Operand(0)),
            new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            MoveScalarRegister(20, 14, 16), MoveScalarRegister(24, 15, 17),
            MoveScalarRegister(28, 16, 18), MoveScalarRegister(32, 17, 19),
            Sop1(36, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(40));
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        var snapshot = new ResourceSnapshot { Buffers = [[0x1000, 16u << 16, 4, 0]] };
        ShaderCallLibrary Read(params ulong[] arguments)
        {
            var words = new Dictionary<ulong, uint> { [0x200000] = 0xBF800000, [0x200004] = 0xBE80200E };
            for (var record = 0; record < arguments.Length; record++)
            {
                var address = 0x1000ul + (ulong)record * 16;
                words[address] = 0x200000;
                words[address + 4] = 0;
                words[address + 8] = (uint)arguments[record];
                words[address + 12] = (uint)(arguments[record] >> 32);
            }

            return ShaderCallLibrary.Read(caller, memory, snapshot, (ulong address, out uint word) => words.TryGetValue(address, out word));
        }

        var original = Read(0x5_0000_0000, 0x5_0000_0040, 0x9_0000_0000, 0);
        Assert.Equal([0x5_0000_0000ul, 0x5_0000_0040, 0x9_0000_0000], original.Arguments);
        var moved = Read(0x5_0010_0000, 0x5_0010_0040, 0x9_0000_0000, 0);
        Assert.Equal(original.LayoutIdentity, moved.LayoutIdentity);
        Assert.NotEqual(original.Identity, moved.Identity);
        Assert.Equal([0x5_0010_0000ul, 0x5_0010_0040, 0x9_0000_0000], moved.Arguments);
        Assert.Equal(original.LayoutIdentity, Read(0x7_0000_0000, 0x5_0000_0080, 0x1000, 0).LayoutIdentity);
        Assert.NotEqual(original.LayoutIdentity, Read(0x5_0000_0000, 0x5_0000_0000, 0x9_0000_0000, 0).LayoutIdentity);
        Assert.NotEqual(original.LayoutIdentity, Read(0x5_0000_0000, 0x5_0000_0040, 0x9_0000_0000, 0x9_0000_0040).LayoutIdentity);
    }

    // The direct form needs only its two loads before the call: at the start of a program fewer than six instructions
    // stand before it.
    [Fact]
    public void DirectRecordLoadsLinkAtTheStartOfAProgram()
    {
        var caller = Program(
            ScalarBufferLoad(0, 4, 14, 2, immediateOffset: 32),
            ScalarBufferLoad(8, 4, 16, 2, immediateOffset: 40),
            new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            Sopc(20, "SCmpLgU64", Operand(0), Gen5Operand.Scalar(14)),
            Branch(24, "SCbranchScc0", 1),
            Sop1(28, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(32));
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 0, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        var snapshot = new ResourceSnapshot { Buffers = [[0, 0, 0, 0]] };
        var library = ShaderCallLibrary.Read(caller, memory, snapshot, (ulong address, out uint word) => { word = 0; return false; });
        var site = Assert.Single(library.Calls);
        Assert.Equal(28u, site.Pc);
        Assert.Empty(site.Targets);
    }

    // A caller may load the record straight into the call registers, the argument pair 8 bytes after the target
    // pair, and test the target before it calls. A null target leaves the site without targets.
    [Theory]
    [InlineData(0x200000u, 40)]
    [InlineData(0u, 40)]
    [InlineData(0x200000u, 48)]
    public void DirectRecordLoads_ReadTheTargetAndItsArgumentBehindTheNullCheck(uint target, int argumentOffset)
    {
        var caller = DirectRecordCaller(argumentOffset);
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 0, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        memory.Add(new() { Pc = 20, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        var snapshot = new ResourceSnapshot { Buffers = [[0x1000, 16u << 16, 4, 0]] };
        var words = new Dictionary<ulong, uint>
        {
            [0x1020] = target, [0x1028] = 0x3000,
            [0x200000] = 0xBF800000, [0x200004] = 0xBE80200E,
        };
        for (var address = 0x1000ul; address < 0x1040; address += 4) words.TryAdd(address, 0);
        ShaderCallLibrary Read() => ShaderCallLibrary.Read(caller, memory, snapshot,
            (ulong address, out uint word) => words.TryGetValue(address, out word));
        if (argumentOffset != 40)
        {
            Assert.Throws<InvalidOperationException>(Read);
            return;
        }
        var site = Assert.Single(Read().Calls);
        Assert.Equal((14u, 16u, 14u), (site.AddressRegister, site.ArgumentRegister, site.ReturnRegister));
        Assert.Equal(target == 0 ? 0 : 1, site.Targets.Count);
        if (target != 0) Assert.Equal(0x3000ul, site.Targets[0].Argument);
        var linked = Gen5ShaderCallLinker.Link(caller, [site]);
        Assert.DoesNotContain(linked.Instructions, instruction => instruction.Opcode == "SSwappcB64");
        Assert.Single(linked.Instructions, instruction => instruction.Control is ShaderCallFaultControl);
    }

    // A buffer without records reads zero for every load of the record: a null target, so the call has no target.
    [Fact]
    public void ADescriptorWithoutRecordsLeavesTheCallWithoutTargets()
    {
        var memory = new MemoryAccessTable();
        memory.Add(new() { Pc = 0, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        var snapshot = new ResourceSnapshot { Buffers = [[0, 0, 0, 0]] };
        var site = Assert.Single(ShaderCallLibrary.Read(DirectRecordCaller(40), memory, snapshot,
            (ulong address, out uint word) => { word = 0; return false; }).Calls);
        Assert.Empty(site.Targets);
        Assert.Equal((14u, 16u, 36ul), (site.AddressRegister, site.ArgumentRegister, site.ReturnAddress));
    }

    private static Gen5ShaderProgram DirectRecordCaller(int argumentOffset) => Program(
        ScalarBufferLoad(0, 4, 14, 2, immediateOffset: 32),
        new(8, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
        Sopc(12, "SCmpLgU64", Operand(0), Gen5Operand.Scalar(14)),
        Branch(16, "SCbranchScc0", 4),
        ScalarBufferLoad(20, 4, 16, 2, immediateOffset: argumentOffset),
        new(28, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
        Sop1(32, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(36));

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
        var relocated = caller with
        {
            Address = 0x8000,
            Instructions = caller.Instructions.Select(instruction => instruction.Pc == 32
                ? instruction with { AddressOffset = 0x1_0000_0020UL } : instruction).ToArray(),
        };
        Assert.Equal(0x1_0000_8024UL, Assert.Single(Read(relocated).Calls).ReturnAddress);
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

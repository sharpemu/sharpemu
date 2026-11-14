// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Call-library reuse (ShaderCallLibraryMemo): every reused result equals what the original read derives from the same
// memory with the same function cache, and the counters show which reuse served it.
public sealed class ShaderCallLibraryMemoTests
{
    private const ulong Table = 0x1000;
    private const int MaxCallees = 192;

    // A run-time-record caller: one site whose 16-byte records it selects by a dynamic offset.
    private static Gen5ShaderProgram Caller() => Program(
        Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
        ScalarBufferLoad(4, 0, 16, 4, dynamicOffsetRegister: 20),
        Vop1(12, "VMovB32", 111, Operand(0)),
        new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
        MoveScalarRegister(20, 14, 16), MoveScalarRegister(24, 15, 17),
        MoveScalarRegister(28, 16, 18), MoveScalarRegister(32, 17, 19),
        Sop1(36, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(40));

    private static MemoryAccessTable Access()
    {
        var access = new MemoryAccessTable();
        access.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        return access;
    }

    private static ResourceSnapshot Snapshot(int records) => new() { Buffers = [[(uint)Table, 16u << 16, (uint)records, 0]] };

    private sealed class Guest
    {
        // 0x200000: s_nop 0; s_setpc_b64 s[14:15]. 0x300000: another body. 0x400000: the code of 0x200000 at another address.
        public readonly Dictionary<ulong, uint> Words = new()
        {
            [0x200000] = 0xBF800000, [0x200004] = 0xBE80200E,
            [0x300000] = 0xBF800000, [0x300004] = 0xBF800000, [0x300008] = 0xBE80200E,
            [0x400000] = 0xBF800000, [0x400004] = 0xBE80200E,
        };

        public void Records(ulong table, params (ulong Target, ulong Argument)[] records)
        {
            for (var index = 0; index < records.Length; index++)
            {
                var address = table + (ulong)index * 16;
                Words[address] = (uint)records[index].Target;
                Words[address + 4] = (uint)(records[index].Target >> 32);
                Words[address + 8] = (uint)records[index].Argument;
                Words[address + 12] = (uint)(records[index].Argument >> 32);
            }
        }

        public void Records(params (ulong Target, ulong Argument)[] records) => Records(Table, records);

        public bool Read(ulong address, out uint word) => Words.TryGetValue(address, out word);
    }

    private sealed class Reader(int records, bool active = true)
    {
        public readonly Gen5ShaderProgram Caller = ShaderCallLibraryMemoTests.Caller();
        public readonly MemoryAccessTable Access = ShaderCallLibraryMemoTests.Access();
        public readonly ShaderFunctionCache Functions = new(null);
        public readonly ShaderCallLibraryMemo Memo = new(active: active);
        public readonly Guest Guest = new();
        public int Records = records;

        public ShaderCallLibrary Reuse() => Memo.Read(Caller, Access, Snapshot(Records), Guest.Read, Functions, runtimeArguments: true,
            userDataBase: 0, readBytes: null);

        // The original read of the same memory with the same function cache.
        public ShaderCallLibrary Original() => ShaderCallLibrary.Read(Caller, Access, Snapshot(Records), Guest.Read, Functions,
            runtimeArguments: true);
    }

    private static void SameAsOriginal(Reader reader, ShaderCallLibrary reused) =>
        Assert.Null(ShaderCallLibraryMemo.Difference(reused, reader.Original()));

    // The same tables again: the tables are read again and the topology of the first read serves.
    [Fact]
    public void RepeatedTablesReuseTheTopology()
    {
        var reader = new Reader(2);
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        var first = reader.Reuse();
        var second = reader.Reuse();
        Assert.Equal(1, reader.Memo.Counted("TopologyMiss"));
        Assert.Equal(1, reader.Memo.Counted("TopologyHit"));
        Assert.Same(first.Topology, second.Topology);
        Assert.Equal(first.Records, second.Records);
        SameAsOriginal(reader, second);
    }

    // Changed arguments in another order: the records are fresh, while the sites, slots and identity come from the topology
    // of the first read.
    [Fact]
    public void ReorderedRecordsWithChangedArgumentsReuseTheTopologyWithFreshRecords()
    {
        var reader = new Reader(3);
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100), (0x200000, 0x5200));
        var first = reader.Reuse();
        reader.Guest.Records((0x300000, 0x6100), (0x200000, 0x6000), (0x200000, 0x6200));
        var second = reader.Reuse();
        Assert.Equal(1, reader.Memo.Counted("TopologyHit"));
        Assert.Equal(first.LayoutIdentity, second.LayoutIdentity);
        Assert.Equal(new (ulong, ulong)[] { (0x300000, 0x6100), (0x200000, 0x6000), (0x200000, 0x6200) }, second.Records);
        SameAsOriginal(reader, second);
    }

    // A duplicated record counts once, in its first position; a record whose target is null is not collected (the same
    // callees: the topology serves); a table with fewer records naming fewer callees is another topology.
    [Fact]
    public void DuplicatedAndRemovedRecordsKeepTheOriginalCollection()
    {
        var reader = new Reader(3);
        reader.Guest.Records((0x200000, 0x5000), (0x200000, 0x5000), (0x300000, 0x5100));
        var duplicated = reader.Reuse();
        Assert.Equal(new (ulong, ulong)[] { (0x200000, 0x5000), (0x300000, 0x5100) }, duplicated.Records);
        SameAsOriginal(reader, duplicated);

        reader.Guest.Records((0x200000, 0x5000), (0, 0), (0x300000, 0x5100));
        var removed = reader.Reuse();
        Assert.Equal(new (ulong, ulong)[] { (0x200000, 0x5000), (0x300000, 0x5100) }, removed.Records);
        SameAsOriginal(reader, removed);

        reader.Records = 2;
        var shorter = reader.Reuse();
        Assert.Equal(new (ulong, ulong)[] { (0x200000, 0x5000) }, shorter.Records);
        SameAsOriginal(reader, shorter);
        Assert.Equal(1, reader.Memo.Counted("TopologyHit"));
        Assert.Equal(2, reader.Memo.Counted("TopologyMiss"));
    }

    // New code at an address the library names is a new function-cache result: the topology is not reused, and the site
    // links the new program.
    [Fact]
    public void ChangedCodeAtAReusedAddressIsNotReused()
    {
        var reader = new Reader(2);
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        var first = reader.Reuse();
        reader.Guest.Words[0x200000] = 0xBF800001;
        var second = reader.Reuse();
        Assert.Equal(0, reader.Memo.Counted("TopologyHit"));
        Assert.Equal(2, reader.Memo.Counted("TopologyMiss"));
        Assert.NotEqual(first.LayoutIdentity, second.LayoutIdentity);
        SameAsOriginal(reader, second);
    }

    // The same code at another address is another topology (the slots name addresses).
    [Fact]
    public void ACalleeAtAnotherAddressIsAnotherTopology()
    {
        var reader = new Reader(2);
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        reader.Reuse();
        reader.Guest.Records((0x400000, 0x5000), (0x300000, 0x5100));
        var relocated = reader.Reuse();
        Assert.Equal(0, reader.Memo.Counted("TopologyHit"));
        Assert.Contains(relocated.Calls[0].Targets, target => target.Address == 0x400000);
        SameAsOriginal(reader, relocated);
    }

    // A record whose function cannot be read is left out; the same table again reuses the topology.
    [Fact]
    public void AnUnreadableTargetIsLeftOutAndTheTopologyIsReused()
    {
        var reader = new Reader(2);
        reader.Guest.Records((0x200000, 0x5000), (0x900000, 0x5100));
        var first = reader.Reuse();
        Assert.Equal(new (ulong, ulong)[] { (0x200000, 0x5000) }, first.Records);
        SameAsOriginal(reader, first);
        var again = reader.Reuse();
        Assert.Equal(1, reader.Memo.Counted("TopologyHit"));
        SameAsOriginal(reader, again);
    }

    // A table the host cannot read in full is refused as the original read refuses it, with no topology step; once
    // readable again, the topology serves.
    [Theory]
    [InlineData(0ul)]
    [InlineData(4ul)]
    [InlineData(16ul)]
    [InlineData(28ul)]
    public void ATableThatBecomesUnreadableKeepsTheOriginalRefusal(ulong missingOffset)
    {
        var reader = new Reader(2);
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        reader.Reuse();
        reader.Guest.Words.Remove(Table + missingOffset);

        var original = Assert.Throws<InvalidOperationException>(() => reader.Original());
        var reused = Assert.Throws<InvalidOperationException>(() => reader.Reuse());
        Assert.Equal(original.Message, reused.Message);
        Assert.Equal(0, reader.Memo.Counted("TopologyHit"));
        Assert.Equal(1, reader.Memo.Counted("TopologyMiss"));

        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        var restored = reader.Reuse();
        Assert.Equal(1, reader.Memo.Counted("TopologyHit"));
        SameAsOriginal(reader, restored);
    }

    // Two sites, each selecting its records from its own table: a changed first table and a hole in the second is refused
    // as the original read refuses it; with the hole restored both tables are read in their order and the topology serves.
    [Fact]
    public void ASecondTableWithAHoleKeepsTheOriginalRefusal()
    {
        const ulong second = 0x2000;
        var caller = Program(
            Sop2(0, "SLshlB32", 20, Gen5Operand.Scalar(20), Operand(4)),
            ScalarBufferLoad(4, 0, 16, 4, dynamicOffsetRegister: 20),
            Vop1(12, "VMovB32", 111, Operand(0)),
            new(16, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            MoveScalarRegister(20, 14, 16), MoveScalarRegister(24, 15, 17),
            MoveScalarRegister(28, 16, 18), MoveScalarRegister(32, 17, 19),
            Sop1(36, "SSwappcB64", 14, Gen5Operand.Scalar(14)),
            Sop2(40, "SLshlB32", 21, Gen5Operand.Scalar(21), Operand(4)),
            ScalarBufferLoad(44, 4, 16, 4, dynamicOffsetRegister: 21),
            new(52, Gen5ShaderEncoding.Sopp, "SWaitcnt", [0xBF8C0000], [], [], null),
            MoveScalarRegister(56, 14, 16), MoveScalarRegister(60, 15, 17),
            MoveScalarRegister(64, 16, 18), MoveScalarRegister(68, 17, 19),
            Sop1(72, "SSwappcB64", 14, Gen5Operand.Scalar(14)), EndProgram(76));
        var access = new MemoryAccessTable();
        access.Add(new() { Pc = 4, Kind = MemoryResourceKind.ScalarBuffer, Resource = 0 });
        access.Add(new() { Pc = 44, Kind = MemoryResourceKind.ScalarBuffer, Resource = 1 });
        var snapshot = new ResourceSnapshot { Buffers = [[(uint)Table, 16u << 16, 2, 0], [(uint)second, 16u << 16, 2, 0]] };
        var guest = new Guest();
        var functions = new ShaderFunctionCache(null);
        var memo = new ShaderCallLibraryMemo(active: true);
        ShaderCallLibrary Reuse() => memo.Read(caller, access, snapshot, guest.Read, functions, runtimeArguments: true, userDataBase: 0,
            readBytes: null);
        ShaderCallLibrary Original() => ShaderCallLibrary.Read(caller, access, snapshot, guest.Read, functions, runtimeArguments: true);

        guest.Records(Table, (0x200000, 0x5000), (0x300000, 0x5100));
        guest.Records(second, (0x300000, 0x7000), (0x200000, 0x7100));
        var first = Reuse();
        Assert.Equal(2, first.Calls.Count);
        Assert.Null(ShaderCallLibraryMemo.Difference(first, Original()));

        guest.Records(Table, (0x200000, 0x6000), (0x300000, 0x6100));
        guest.Words.Remove(second + 20);
        var original = Assert.Throws<InvalidOperationException>(Original);
        var reused = Assert.Throws<InvalidOperationException>(Reuse);
        Assert.Equal(original.Message, reused.Message);
        Assert.Equal(0, memo.Counted("TopologyHit"));

        guest.Records(second, (0x300000, 0x7000), (0x200000, 0x7200));
        var restored = Reuse();
        Assert.Equal(1, memo.Counted("TopologyHit"));
        Assert.Null(ShaderCallLibraryMemo.Difference(restored, Original()));
        Assert.Contains((0x200000ul, 0x7200ul), restored.Records);
    }

    // Unite keeps a call that changed nothing by the input's topology and the union's version: a later read with other
    // records reuses it with its own records, and the union stays as the original call leaves it.
    [Fact]
    public void UniteAndSiteCalleesAreReusedWithTheCurrentRecords()
    {
        var reader = new Reader(2);
        var union = new Dictionary<uint, Dictionary<ulong, UnitedCallee>>();
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        var first = reader.Reuse();
        reader.Memo.Unite(first, reader.Caller.Address, union, MaxCallees);
        Assert.Equal(1, reader.Memo.Counted("UniteChanged"));
        var united = reader.Memo.Unite(first, reader.Caller.Address, union, MaxCallees);
        Assert.Equal(0, reader.Memo.Counted("UniteHit"));

        reader.Guest.Records((0x300000, 0x6100), (0x200000, 0x6000));
        var second = reader.Reuse();
        var reused = reader.Memo.Unite(second, reader.Caller.Address, union, MaxCallees);
        Assert.Equal(1, reader.Memo.Counted("UniteHit"));
        var copy = union.ToDictionary(static site => site.Key, static site => new Dictionary<ulong, UnitedCallee>(site.Value));
        var direct = second.Unite(reader.Caller.Address, copy, MaxCallees, out var changed);
        Assert.False(changed);
        Assert.Null(ShaderCallLibraryMemo.Difference(reused, direct));
        Assert.Equal(second.Records, reused.Records);

        var chosen = union.ToDictionary(static site => site.Key, static site => site.Value);
        var firstChoice = reader.Memo.WithSiteCallees(united, reader.Caller.Address, chosen);
        Assert.Null(ShaderCallLibraryMemo.Difference(firstChoice, united.WithSiteCallees(reader.Caller.Address, chosen)));
        var reusedChoice = reader.Memo.WithSiteCallees(reused, reader.Caller.Address, chosen);
        Assert.Equal(1, reader.Memo.Counted("SitesHit"));
        Assert.Null(ShaderCallLibraryMemo.Difference(reusedChoice, reused.WithSiteCallees(reader.Caller.Address, chosen)));
    }

    // With reuse off, a read is the original read: counted as Off and without a topology token.
    [Fact]
    public void AnInactiveMemoReadsAsTheOriginal()
    {
        var reader = new Reader(2, active: false);
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        var read = reader.Reuse();
        Assert.Null(read.Topology);
        Assert.Equal(1, reader.Memo.Counted("Off"));
        Assert.Equal(0, reader.Memo.Counted("TopologyMiss"));
        SameAsOriginal(reader, read);
    }

    // An un-tokened library (the original read's) must invalidate a previously kept Unite result.
    [Fact]
    public void AnUntokenedUnionChangeInvalidatesAnEarlierKeptResult()
    {
        var reader = new Reader(2);
        var union = new Dictionary<uint, Dictionary<ulong, UnitedCallee>>();
        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        var first = reader.Reuse();
        reader.Memo.Unite(first, reader.Caller.Address, union, MaxCallees);
        reader.Memo.Unite(first, reader.Caller.Address, union, MaxCallees);

        reader.Guest.Records((0x400000, 0x6000), (0x300000, 0x6100));
        var untokened = reader.Original();
        Assert.Null(untokened.Topology);
        reader.Memo.Unite(untokened, reader.Caller.Address, union, MaxCallees);
        Assert.Equal(1, reader.Memo.Counted("UniteUntokened"));
        Assert.Contains(0x400000ul, Assert.Single(union).Value.Keys);
        Assert.DoesNotContain(0x200000ul, Assert.Single(union).Value.Keys);

        reader.Guest.Records((0x200000, 0x5000), (0x300000, 0x5100));
        var current = reader.Reuse();
        var expectedUnion = union.ToDictionary(static site => site.Key,
            static site => new Dictionary<ulong, UnitedCallee>(site.Value));
        var expected = current.Unite(reader.Caller.Address, expectedUnion, MaxCallees);
        var actual = reader.Memo.Unite(current, reader.Caller.Address, union, MaxCallees);
        Assert.Equal(0, reader.Memo.Counted("UniteHit"));
        Assert.Null(ShaderCallLibraryMemo.Difference(actual, expected));
        Assert.Equal(Assert.Single(expectedUnion).Value.Keys.Order(), Assert.Single(union).Value.Keys.Order());
    }
}

// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceMaterializationCacheTests
{
    private const ulong HeapBase = 0x1000;

    // A mask word, a two-entry index table and six 32-byte image records.
    private sealed class Heap
    {
        public readonly Dictionary<ulong, uint> Words = new();
        public int Reads;
        public bool GpuOwned;

        public Heap()
        {
            Words[HeapBase + 0x80] = (1u << 1) | (1u << 4);
            Words[HeapBase + 0x40 + 0x90] = 2;
            Words[HeapBase + 0x40 + 4 * 0x90] = 5;
            foreach (var record in new uint[] { 2, 5 })
            {
                var entry = HeapBase + 0x100 + record * 32;
                for (uint dword = 0; dword < 8; dword++)
                    Words[entry + dword * 4] = dword switch
                    {
                        0 => (record & 1) == 0 ? 0x2000u : 0x1000u,
                        1 => 20u << 20,
                        3 => 0xFACu | (9u << 28),
                        _ => 0,
                    };
            }
        }

        public bool Read(ulong address, out uint word)
        {
            Reads++;
            return Words.TryGetValue(address, out word);
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            if (GpuOwned)
                return false;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!Words.TryGetValue(address + (ulong)offset, out var word))
                    return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }
    }

    private static ShaderResourcePlan Plan() =>
        ShaderResourcePlan.Extract(DirectImageTableTests.CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 2);

    private static bool Run(ResourceMaterializationCache cache, ShaderResourcePlan plan, Heap heap, uint[] userData,
        out ResourceSnapshot snapshot, out ResourceSpecialization specialization, ulong shaderBase = 0)
    {
        snapshot = new ResourceSnapshot();
        specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(userData, readCleanMemory: heap.Read, shaderBase: shaderBase), heap.ReadResident,
            ref snapshot, ref specialization, out _);
    }

    [Fact]
    public void UnchangedMemoryReusesTheMaterialization()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out var firstSpecialization));
        var readsAfterFirst = heap.Reads;
        Assert.True(readsAfterFirst > 0);

        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out var secondSpecialization));
        Assert.Equal(readsAfterFirst, heap.Reads);
        Assert.Same(first, second);
        Assert.Same(firstSpecialization, secondSpecialization);
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AChangedDescriptorWordMaterializesAgain()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));

        heap.Words[HeapBase + 0x100 + 5 * 32] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.NotSame(first, second);
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));

        // An uncached full walk gives the same result as the re-materialization.
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: heap.Read),
            ref reference, ref referenceSpecialization));
        Assert.Equal(reference.Images.Select(image => image.ToArray()), second.Images.Select(image => image.ToArray()));
    }

    [Fact]
    public void AlternatingDescriptorsReuseEveryRecentVariant()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        var word = HeapBase + 0x100 + 5 * 32;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        heap.Words[word] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));

        heap.Words[word] = 0x1000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var firstAgain, out _));
        heap.Words[word] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var secondAgain, out _));
        Assert.Same(first, firstAgain);
        Assert.Same(second, secondAgain);
        Assert.Equal((2, 2), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AChangedMaskMaterializesAgain()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        heap.Words[HeapBase + 0x80] = 1u << 1;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void GpuOwnedMemoryIsNeverTrusted()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        heap.GpuOwned = true;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(0, cache.Hits);
    }

    private static ShaderResourcePlan PlanWithSpareUserData() =>
        ShaderResourcePlan.Extract(DirectImageTableTests.CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 4);

    [Fact]
    public void AUserDataWordTheProgramNeverReadsDoesNotSplitTheEntry()
    {
        var plan = PlanWithSpareUserData();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0, 5, 6], out var first, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0, 7, 8], out var second, out _));
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
        // The hit carries the second draw's user data and leaves the first draw's snapshot alone.
        Assert.Equal(new uint[] { 0x1000, 0, 7, 8 }, second.UserData);
        Assert.Equal(new uint[] { 0x1000, 0, 5, 6 }, first.UserData);
        Assert.Same(first.Images, second.Images);
        Assert.True(Run(cache, plan, heap, [0x1000, 0, 5, 6], out var third, out _));
        Assert.Same(first, third);
    }

    [Fact]
    public void ADifferentDrawKeyIsADifferentEntry()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _, shaderBase: 0x100));
        Assert.NotSame(first, second);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var third, out _));
        Assert.Same(first, third);
        Assert.Equal((1, 2), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AFailedReadIsNotCached()
    {
        var plan = Plan();
        var heap = new Heap();
        heap.Words.Remove(HeapBase + 0x100 + 5 * 32 + 4);
        var cache = new ResourceMaterializationCache();
        var ok = Run(cache, plan, heap, [0x1000, 0], out _, out _);
        var reads = heap.Reads;
        Assert.Equal(ok, Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.True(heap.Reads > reads);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void AFailedRecordingDoesNotPoisonTheNextReader()
    {
        var plan = Plan();
        var incomplete = new Heap();
        incomplete.Words.Remove(HeapBase + 0x100 + 5 * 32 + 4);
        var cache = new ResourceMaterializationCache();
        Run(cache, plan, incomplete, [0x1000, 0], out _, out _);

        var complete = new Heap();
        Assert.True(Run(cache, plan, complete, [0x1000, 0], out var first, out _));
        Assert.True(Run(cache, plan, complete, [0x1000, 0], out var second, out _));
        Assert.Same(first, second);
        Assert.Equal(1, cache.Hits);

        // Retained entries must not refer to scratch reads reused for another key.
        complete.Words[HeapBase + 0x80] = 1u << 1;
        Assert.True(Run(cache, plan, complete, [0x1000, 0], out _, out _, shaderBase: 0x100));
        var original = new Heap();
        Assert.True(Run(cache, plan, original, [0x1000, 0], out var restored, out _));
        Assert.Same(first, restored);
    }

    // The table pointer in s[0:1]; the rest of the nine user-data registers the program declares.
    private static readonly uint[] TableUserData = [0x1000, 0, 0, 0, 0, 0, 0, 0, 0];

    // Reads the four constants of FlattenedReadReuseTests.RepeatedReadProgram from a word memory.
    private static bool RunTable(ResourceMaterializationCache cache, ShaderResourcePlan plan, TestWordMemory memory,
        out ResourceSnapshot snapshot)
    {
        snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read), (address, destination, _) =>
        {
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word))
                    return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }, ref snapshot, ref specialization, out _);
    }

    [Fact]
    public void AChangedTableOnlyWordRefreshesOnlyTheTable()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out var first));
        Assert.Equal([11u, 22, 33, 44], first.FlattenedResourceTable);

        memory.Words[2] = 99;
        Assert.True(RunTable(cache, plan, memory, out var second));
        Assert.Equal((0, 1, 1), (cache.Hits, cache.Misses, cache.TableRefreshes));
        Assert.Equal([11u, 22, 99, 44], second.FlattenedResourceTable);
        Assert.Same(first.Buffers, second.Buffers);

        // The refreshed table matches an uncached full walk, and the refreshed entry is then a hit.
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read), ref reference, ref referenceSpecialization));
        Assert.Equal(reference.FlattenedResourceTable, second.FlattenedResourceTable);
        Assert.True(RunTable(cache, plan, memory, out var third));
        Assert.Same(second, third);
        Assert.Equal((1, 1, 1), (cache.Hits, cache.Misses, cache.TableRefreshes));
    }

    [Fact]
    public void AnUnreadableTableWordFallsBackToAFullWalk()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out _));
        memory.Words[1] = 7;
        memory.FailAddress = 0x1000 + 3 * 4;
        RunTable(cache, plan, memory, out _);
        Assert.Equal(0, cache.TableRefreshes);
    }

    // A V# taken verbatim from s[4:7], the way titles pass per-draw constants.
    private static ShaderResourcePlan DirectBufferPlan() =>
        Extract(Program(BufferLoad(0, 4), EndProgram(8)), userDataCount: 8);

    // The same V#, whose base pair s[4:5] is also the address of a scalar load.
    private static ShaderResourcePlan SteeringBufferPlan() =>
        Extract(Program(ScalarLoad(0, 4, 16), Vop1(8, "VMovB32", 0, Gen5Operand.Scalar(16)), BufferStore(12, 4), EndProgram(20)),
            userDataCount: 8);

    private static bool RunWords(ResourceMaterializationCache cache, ShaderResourcePlan plan, TestWordMemory memory, uint[] userData,
        out ResourceSnapshot snapshot)
    {
        snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(userData, memory.Read, memory.Read), (address, destination, _) =>
        {
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word))
                    return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }, ref snapshot, ref specialization, out _);
    }

    private static uint[] Reference(ShaderResourcePlan plan, TestWordMemory memory, uint[] userData)
    {
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref snapshot, ref specialization));
        return snapshot.Buffers[0];
    }

    private static uint[] LinearConstants(int draw) => [0, 0, 0, 0, 0x10000u + (uint)draw * 0x100u, 0, 256, 0x2_0000];

    [Fact]
    public void ALinearlyAllocatedBufferBaseStopsMissingOnceConfirmed()
    {
        var plan = DirectBufferPlan();
        var memory = new TestWordMemory();
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = LinearConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            Assert.Equal(Reference(plan, memory, userData), snapshot.Buffers[0]);
            Assert.Equal(userData, snapshot.UserData);
        }

        // One miss to build, three to confirm the word; every later draw is a hit.
        Assert.Equal((12, 4), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void InterleavedObjectsConfirmTheirOwnBases()
    {
        // Constants V# in s[4:7], a second buffer in s[8:11] whose size tells the two objects apart.
        var plan = Extract(Program(BufferLoad(0, 4), BufferLoad(8, 8), EndProgram(16)), userDataCount: 12);
        var memory = new TestWordMemory();
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 32; draw++)
        {
            uint[] userData = [0, 0, 0, 0, 0x10000u + (uint)draw * 0x100u, 0, 256, 0x2_0000, 0x40000, 0, draw % 2 == 0 ? 256u : 512u, 0x2_0000];
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
        }

        Assert.True(cache.Hits >= 20, $"hits={cache.Hits} misses={cache.Misses}");
    }

    // Pointer s[0:1] moves on every draw; the program loads four constants through it.
    private static uint[] PointerConstants(int draw) => [0x10000u + (uint)draw * 0x100u, 0, 0, 0, 0x30000, 0, 256, 0x2_0000, 0];

    [Fact]
    public void AMovingScalarLoadPointerStopsMissingOnceConfirmed()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        // The same four words repeat every 0x100 bytes, so every pointer finds the same constants.
        var memory = new TestWordMemory { Base = 0x10000, Words = Enumerable.Range(0, 0x1000).Select(index => (uint)(index % 64) * 3 + 1).ToArray() };
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = PointerConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
            Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
        }

        Assert.Equal((12, 4), (cache.Hits, cache.Misses));

        // Other constants behind the next pointer: the draw reads them instead of reusing the table.
        var moved = PointerConstants(30);
        memory.At(moved[0] + 8) = 0xBEEF;
        Assert.True(RunWords(cache, plan, memory, moved, out var reread));
        Assert.Equal(0xBEEFu, reread.FlattenedResourceTable[2]);
        Assert.Equal(12, cache.Hits);
    }

    [Fact]
    public void ARebasedHitLeavesTheCachedSnapshotAlone()
    {
        var plan = DirectBufferPlan();
        var memory = new TestWordMemory();
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 4; draw++)
            Assert.True(RunWords(cache, plan, memory, LinearConstants(draw), out _));

        Assert.True(RunWords(cache, plan, memory, LinearConstants(3), out var built));
        Assert.True(RunWords(cache, plan, memory, LinearConstants(9), out var patched));
        Assert.Equal(0x10900u, patched.Buffers[0][0]);
        Assert.Equal(0x10300u, built.Buffers[0][0]);
        Assert.NotSame(built.Buffers, patched.Buffers);
        Assert.True(RunWords(cache, plan, memory, LinearConstants(3), out var again));
        Assert.Equal(0x10300u, again.Buffers[0][0]);
    }

    [Fact]
    public void AReadThatMovesWithTheBaseAndKeepsItsBytesIsReused()
    {
        var plan = SteeringBufferPlan();
        var memory = new TestWordMemory { Base = 0x10000, Words = Enumerable.Repeat(0x1234u, 0x1000).ToArray() };
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = LinearConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.Buffers[0], snapshot.Buffers[0]);
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        }

        Assert.Equal((12, 4), (cache.Hits, cache.Misses));

        // The moved read now finds other bytes at the new base: the draw must not reuse the table.
        var changed = LinearConstants(20);
        memory.At(changed[4]) = 0x5678;
        Assert.True(RunWords(cache, plan, memory, changed, out var refreshed));
        Assert.Equal(0x5678u, refreshed.FlattenedResourceTable[0]);
        Assert.Equal(12, cache.Hits);
    }

    [Fact]
    public void MovedConstantsThatChangeEveryDrawAreReadAgain()
    {
        var plan = SteeringBufferPlan();
        var memory = new TestWordMemory { Base = 0x10000, Words = Enumerable.Range(0, 0x1000).Select(index => (uint)index * 7).ToArray() };
        var cache = new ResourceMaterializationCache();
        for (var draw = 0; draw < 16; draw++)
        {
            var userData = LinearConstants(draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            Assert.Equal(Reference(plan, memory, userData), snapshot.Buffers[0]);
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        }

        // The constants differ at every base, so nothing is reused as is; once the base is
        // confirmed, the moved table is evaluated again instead of the whole walk.
        Assert.Equal(0, cache.Hits);
        Assert.True(cache.TableRefreshes >= 10, $"refreshes={cache.TableRefreshes} misses={cache.Misses}");
    }

    // The V# comes from a descriptor table at s[0:1]; its constants are read through it.
    private static ShaderResourcePlan MemoryDescriptorPlan() =>
        Extract(Program(ScalarLoad(0, 0, 8, 4), ScalarBufferLoad(4, 8, 12, 4), Vop1(8, "VMovB32", 0, Gen5Operand.Scalar(12)),
            BufferLoad(12, 8), EndProgram(16)), userDataCount: 2);

    // The title rewrites the table every draw: a freshly allocated base, and new constants there.
    private static void WriteDraw(TestWordMemory memory, int draw)
    {
        var bufferBase = 0x10000u + (uint)draw * 0x100u;
        memory.At(0x1000) = bufferBase;
        memory.At(0x1004) = 0;
        memory.At(0x1008) = 256;
        memory.At(0x100C) = 0x2_0000;
        for (var word = 0u; word < 4; word++)
            memory.At(bufferBase + word * 4) = (uint)draw * 10 + word;
    }

    [Fact]
    public void ABaseRewrittenInGuestMemoryStopsMissingOnceConfirmed()
    {
        var plan = MemoryDescriptorPlan();
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x8000] };
        var cache = new ResourceMaterializationCache();
        uint[] userData = [0x1000, 0];
        for (var draw = 0; draw < 16; draw++)
        {
            WriteDraw(memory, draw);
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            var expected = new ResourceSnapshot();
            var expectedSpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
            Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
            Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        }

        // One miss to build, three to confirm the base; the later draws patch it.
        Assert.True(cache.Hits >= 10, $"hits={cache.Hits} misses={cache.Misses}");

        // Another descriptor word changed with the base: the draw is materialized again.
        WriteDraw(memory, 20);
        memory.At(0x1008) = 512;
        var hits = cache.Hits;
        Assert.True(RunWords(cache, plan, memory, userData, out var resized));
        Assert.Equal(hits, cache.Hits);
        Assert.Equal(512u, resized.Buffers[0][2]);
    }

    private static void AssertMatchesFullWalk(ShaderResourcePlan plan, TestWordMemory memory, uint[] userData, ResourceSnapshot snapshot)
    {
        var expected = new ResourceSnapshot();
        var expectedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, memory.Read, memory.Read), ref expected, ref expectedSpecialization));
        Assert.Equal(expected.Buffers.Select(buffer => buffer.ToArray()), snapshot.Buffers.Select(buffer => buffer.ToArray()));
        Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
    }

    [Fact]
    public void ATableConstantBesideARewrittenBaseIsCopiedForTheDraw()
    {
        // The V# and a constant both come from the table at s[0:1]; the constant changes every draw.
        var plan = Extract(Program(ScalarLoad(0, 0, 8, 4), ScalarLoad(4, 0, 16, 1, immediateOffset: 16),
            ScalarBufferLoad(8, 8, 12, 4), Vop1(12, "VMovB32", 0, Gen5Operand.Scalar(12)),
            Vop1(16, "VMovB32", 1, Gen5Operand.Scalar(16)), BufferLoad(20, 8), EndProgram(24)), userDataCount: 2);
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x8000] };
        var cache = new ResourceMaterializationCache();
        uint[] userData = [0x1000, 0];
        for (var draw = 0; draw < 16; draw++)
        {
            WriteDraw(memory, draw);
            memory.At(0x1010) = (uint)draw * 3 + 1;
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            AssertMatchesFullWalk(plan, memory, userData, snapshot);
        }

        Assert.True(cache.Hits >= 10, $"hits={cache.Hits} misses={cache.Misses}");
    }

    [Fact]
    public void AlternatingObjectsWithRewrittenBasesRebaseTheirOwnVariant()
    {
        // Two objects share the draw key and differ in their buffer size; both move their base.
        var plan = MemoryDescriptorPlan();
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x8000] };
        var cache = new ResourceMaterializationCache();
        uint[] userData = [0x1000, 0];
        for (var draw = 0; draw < 32; draw++)
        {
            WriteDraw(memory, draw);
            memory.At(0x1008) = draw % 2 == 0 ? 256u : 512u;
            Assert.True(RunWords(cache, plan, memory, userData, out var snapshot));
            AssertMatchesFullWalk(plan, memory, userData, snapshot);
        }

        Assert.True(cache.Hits >= 20, $"hits={cache.Hits} misses={cache.Misses}");
    }

    [Fact]
    public void AFullGenerationKeepsRecentEntries()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(generationCapacity: 1);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _, shaderBase: 0x100));
        // The first entry moved to the old generation and is still found.
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(1, cache.Hits);
    }
}

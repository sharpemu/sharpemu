// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using System.Reflection;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceMaterializationPerformanceTests
{
    [Theory]
    [InlineData(28, false)]
    [InlineData(32, true)]
    [InlineData(64, false)]
    [InlineData(67, true)]
    public void ResidentRecorderPreservesBatchEntriesAndRefusal(int bytes, bool clean)
    {
        var type = typeof(ResourceMaterializationCache).GetNestedType("ReadRecorder", BindingFlags.NonPublic)!;
        var recorder = Activator.CreateInstance(type, true)!;
        var refused = false;
        ResidentGuestBytesReader reader = (address, destination, policy) =>
        {
            for (var index = 0; index < destination.Length; index++) destination[index] = (byte)index;
            Assert.Equal(clean, policy);
            return !refused;
        };
        var wrapped = (ResidentGuestBytesReader)type.GetMethod("WrapResident")!.Invoke(recorder, [reader])!;
        var records = (List<(ulong Address, uint Word, bool Clean, bool Table)>)type.GetProperty("Reads")!.GetValue(recorder)!;
        var phase = (Action<bool>)type.GetProperty("TablePhase")!.GetValue(recorder)!;
        var buffer = new byte[bytes];
        for (var pass = 0; pass < 2; pass++)
        {
            phase(pass != 0);
            Assert.True(wrapped(0x1000, buffer, clean));
        }
        var words = bytes / 4;
        Assert.Equal(words * 2, records.Count);
        for (var index = 0; index < records.Count; index++)
        {
            var offset = index % words * 4;
            Assert.Equal((0x1000UL + (ulong)offset, BitConverter.ToUInt32(buffer, offset), clean, index >= words), records[index]);
        }
        var saved = records.ToArray();
        refused = true;
        Assert.False(wrapped(0x2000, buffer, clean));
        Array.Fill(buffer, (byte)255);
        Assert.Equal(saved, records);
        type.GetMethod("Reset")!.Invoke(recorder, null);
        Assert.Empty(records);
        phase(false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserDataStorageRejectsChangedWords(bool useArray)
    {
        var plan = Extract(Program(BufferLoad(0, 0), EndProgram(8)));
        uint[] words = [0x1000, 0, 256, 0];
        IReadOnlyList<uint> userData = useArray ? words : Array.AsReadOnly(words);
        var cache = new ResourceMaterializationCache();
        var inputs = new ResourceRuntimeInputs { UserData = userData };
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        bool Read(ulong address, Span<byte> bytes, bool clean) => false;
        Assert.True(cache.Materialize(plan, inputs, Read, ref snapshot, ref specialization, out _));
        Assert.True(cache.Materialize(plan, inputs, Read, ref snapshot, ref specialization, out _));
        Assert.Equal(1, cache.Hits);
        words[0] = 0x2000;
        Assert.True(cache.Materialize(plan, inputs, Read, ref snapshot, ref specialization, out _));
        Assert.Equal(2, cache.Misses);
        Assert.Equal(0x2000u, Assert.Single(snapshot.Buffers)[0]);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void StructuredRefreshPreservesActivityAndRejectsChangedPredicate(uint predicate)
    {
        var plan = Extract(Program(ScalarLoad(0, 12, 8),
            ScalarLoad(8, 12, 16, immediateOffset: 4), MoveVectorFromScalar(16, 4, 16),
            Sopc(20, "SCmpEqU32", Gen5Operand.Scalar(8), Operand(1)),
            Branch(24, "SCbranchScc1", 3), BufferLoad(28, 0), Branch(36, "SBranch", 2),
            BufferLoad(40, 4), EndProgram(48)));
        plan.CompileEvaluatorNow();
        Assert.NotNull(plan.CompiledTableTransaction);
        var memory = new TestWordMemory { Words = [predicate, 11] };
        uint[] userData = [0x2000, 0, 256, 0, 0x3000, 0, 256, 0, 0, 0, 0, 0, 0x1000, 0];
        var inputs = Inputs(userData, memory.Read, memory.Read);
        var cache = new ResourceMaterializationCache();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        bool Read(ulong address, Span<byte> bytes, bool clean)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word)) return false;
                BitConverter.TryWriteBytes(bytes[offset..], word);
            }
            return true;
        }
        Assert.True(cache.Materialize(plan, inputs, Read, ref snapshot, ref specialization, out _));
        var buffers = snapshot.Buffers;
        memory.Words[1] = 22;
        Assert.True(cache.Materialize(plan, inputs, Read, ref snapshot, ref specialization, out _));
        Assert.Equal(1, cache.TableRefreshes);
        Assert.Same(buffers, snapshot.Buffers);
        memory.Words[0] = 1 - predicate;
        Assert.True(cache.Materialize(plan, inputs, Read, ref snapshot, ref specialization, out _));
        Assert.Equal(2, cache.Misses);
        Assert.Equal(1, cache.TableRefreshes);
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref reference, ref referenceSpecialization));
        Assert.Equal(reference.FlattenedResourceTable, snapshot.FlattenedResourceTable);
        Assert.Equal(reference.Buffers.SelectMany(words => words), snapshot.Buffers.SelectMany(words => words));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OlderDescriptorVariantRefreshesItsTableAndPreservesOtherVariants(int variantCount)
    {
        var program = Program(
            ScalarLoad(0, 0, 4, 8),
            ScalarLoad(8, 0, 16, immediateOffset: 32),
            Vop1(16, "VMovB32", 4, SharpEmu.ShaderCompiler.Gen5Operand.Scalar(16)),
            Image(20, "ImageLoad", 4), EndProgram(28));
        var (plan, _, _) = Prepare(program, userDataCount: 9);
        var memory = new TestWordMemory { Words = [0x2000, 20u << 20, 0, 0xFACu | (9u << 28), 0, 0, 0, 0, 11] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out var first));
        var retained = new List<ResourceSnapshot>();
        for (var index = 1; index < variantCount; index++)
        {
            memory.Words[0] = (uint)(0x2000 + index * 0x1000);
            Assert.True(RunTable(cache, plan, memory, out var next));
            retained.Add(next);
        }
        Assert.Equal(variantCount, cache.Misses);
        memory.Words[0] = 0x2000;
        memory.Words[8] = 22;
        Assert.True(RunTable(cache, plan, memory, out var refreshed));
        Assert.Equal(variantCount, cache.Misses);
        Assert.Equal(1, cache.TableRefreshes);
        Assert.Same(first.Images, refreshed.Images);
        var reference = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read),
            ref reference, ref specialization));
        Assert.Equal(reference.FlattenedResourceTable, refreshed.FlattenedResourceTable);
        memory.Words[8] = 11;
        for (var index = 1; index < variantCount; index++)
        {
            memory.Words[0] = (uint)(0x2000 + index * 0x1000);
            Assert.True(RunTable(cache, plan, memory, out var reused));
            Assert.Same(retained[index - 1], reused);
        }
        Assert.Equal(variantCount - 1, cache.Hits);
    }

    [Fact]
    public void DescriptorMismatchDoesNotRepeatResidentReadsForRefresh()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        heap.Words[HeapBase + 0x80] = 1u << 4;
        var reads = 0;
        bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            reads++;
            return heap.ReadResident(address, destination, clean);
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: heap.Read), ReadResident,
            ref snapshot, ref specialization, out _));
        Assert.Equal(1, reads);
        Assert.Equal(2, cache.Misses);
    }

    private const ulong HeapBase = 0x1000;

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void RefreshStopsAtTheFirstChangedDescriptorRange(int changedRange)
    {
        var type = typeof(ResourceMaterializationCache);
        var entryType = type.GetNestedType("Entry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType, true)!;
        void Set(string name, object value) => entryType.GetProperty(name)!.SetValue(entry, value);
        Set("TableRefreshable", true);
        Set("RangeAddresses", new ulong[] { 0x1000, 0x2000, 0x3000, 0x4000 });
        Set("RangeOffsets", new[] { 0, 4, 8, 12 });
        Set("RangeLengths", new[] { 4, 4, 4, 4 });
        Set("RangeClean", new[] { true, true, true, true });
        Set("WordTableOnly", new bool[4]);
        var published = new byte[16];
        Set("Bytes", published);
        var reads = 0;
        ResidentGuestBytesReader reader = (ulong address, Span<byte> destination, bool clean) =>
        {
            reads++;
            Assert.True(clean);
            destination.Clear();
            if (address == (ulong)(changedRange + 1) * 0x1000) destination[0] = 1;
            return true;
        };
        var method = type.GetMethod("TryRefreshTable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var plan = Plan();
        var rebase = Activator.CreateInstance(type.GetNestedType("AddressRebase", BindingFlags.NonPublic)!, [plan]);
        var accepted = (bool)method.Invoke(new ResourceMaterializationCache(),
            new object?[] { 0UL, entry, plan, Inputs([0x1000, 0]), reader, rebase, null })!;
        Assert.False(accepted);
        Assert.Equal(changedRange + 1, reads);
        Assert.All(published, value => Assert.Equal(0, value));
    }

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
    public void EightVariantsRemainCachedAndNinthEvictsTheLeastRecent()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        var snapshots = new ResourceSnapshot[9];
        var word = HeapBase + 0x100 + 5 * 32;
        for (var index = 0; index < 8; index++)
        {
            heap.Words[word] = (uint)(0x1000 + index * 0x2000);
            Assert.True(Run(cache, plan, heap, [0x1000, 0], out snapshots[index], out _));
        }
        for (var index = 0; index < 8; index++)
        {
            heap.Words[word] = (uint)(0x1000 + index * 0x2000);
            Assert.True(Run(cache, plan, heap, [0x1000, 0], out var reused, out _));
            Assert.Same(snapshots[index], reused);
        }
        Assert.Equal((8, 8), (cache.Hits, cache.Misses));
        heap.Words[word] = 0x11000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out snapshots[8], out _));
        for (var index = 1; index < 9; index++)
        {
            heap.Words[word] = (uint)(0x1000 + index * 0x2000);
            Assert.True(Run(cache, plan, heap, [0x1000, 0], out var reused, out _));
            Assert.Same(snapshots[index], reused);
        }
        heap.Words[word] = 0x1000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var replaced, out _));
        Assert.NotSame(snapshots[0], replaced);
        Assert.Equal((16, 10), (cache.Hits, cache.Misses));
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

    [Fact]
    public void FailedRefreshCannotChangePublishedBytes()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out var first));
        memory.Words[2] = 99;
        Assert.True(RunTable(cache, plan, memory, out var refreshed));
        memory.Words[0] = 77;
        memory.FailAddress = 0x100C;
        RunTable(cache, plan, memory, out _);
        memory.Words[0] = 11;
        memory.FailAddress = ulong.MaxValue;
        Assert.True(RunTable(cache, plan, memory, out var reused));
        Assert.Same(refreshed, reused);
        Assert.Equal([11u, 22, 33, 44], first.FlattenedResourceTable);
        Assert.Equal([11u, 22, 99, 44], reused.FlattenedResourceTable);
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

    [Fact]
    public void RefreshReusesScratchBeforeReadingAndRejectsChangedEvaluation()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var cache = new ResourceMaterializationCache();
        Assert.True(RunTable(cache, plan, memory, out _));
        memory.Words[0] = 55;
        Assert.True(RunTable(cache, plan, memory, out _));
        memory.Words[0] = 66;
        var reads = 0;
        var previousAllocation = 0L;
        var refreshAllocation = -1L;
        bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            if (++reads == 2)
                refreshAllocation = GC.GetAllocatedBytesForCurrentThread() - previousAllocation;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word)) return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }
            if (reads == 2) memory.Words[0] = 77;
            previousAllocation = GC.GetAllocatedBytesForCurrentThread();
            return true;
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(cache.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read), ReadResident,
            ref snapshot, ref specialization, out _));
        Assert.Equal(0, refreshAllocation);
        Assert.Equal(1, cache.TableRefreshes);
        Assert.Equal(2, cache.Misses);
        Assert.Equal([77u, 22, 33, 44], snapshot.FlattenedResourceTable);
    }
}

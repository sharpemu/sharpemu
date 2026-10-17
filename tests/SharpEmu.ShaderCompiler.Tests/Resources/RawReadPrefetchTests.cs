// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class RawReadPrefetchTests
{
    private static readonly uint[] TableUserData = [0x1000, 0, 0, 0, 0, 0, 0, 0, 0];

    private sealed class ResidentMemory(TestWordMemory memory)
    {
        public int Calls;
        public int Words;
        public bool Refuse;

        public bool Read(ulong address, Span<byte> destination, bool clean)
        {
            Calls++;
            if (Refuse)
            {
                return false;
            }

            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!memory.Read(address + (ulong)offset, out var word))
                {
                    return false;
                }

                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            memory.Reads -= (uint)(destination.Length / 4);
            Words += destination.Length / 4;
            return true;
        }
    }

    private static ResourceRuntimeInputs PrefetchInputs(TestWordMemory memory, ResidentMemory resident) => new()
    {
        UserData = TableUserData,
        ReadMemory = memory.Read,
        ReadCleanMemory = memory.Read,
        ReadResidentMemory = resident.Read,
    };

    [Fact]
    public void ContiguousTableWordsAreReadInOneResidentCall()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var resident = new ResidentMemory(memory);

        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, [], PrefetchInputs(memory, resident), plan.CleanFlatSlots,
            evaluateTable: true, out _, out var table));

        Assert.Equal([11u, 22, 33, 44], table);
        Assert.Equal(1, resident.Calls);
        Assert.Equal(4, resident.Words);
        Assert.Equal(0u, memory.Reads);
    }

    [Fact]
    public void ARefusedRunFallsBackToWordReads()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var resident = new ResidentMemory(memory) { Refuse = true };

        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, [], PrefetchInputs(memory, resident), plan.CleanFlatSlots,
            evaluateTable: true, out _, out var table));

        Assert.Equal([11u, 22, 33, 44], table);
        Assert.Equal(4u, memory.Reads);
    }

    [Fact]
    public void APrefetchedMaterializationMatchesTheWordWalk()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var resident = new ResidentMemory(memory);
        var prefetched = new ResourceSnapshot();
        var prefetchedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, PrefetchInputs(memory, resident), ref prefetched, ref prefetchedSpecialization));

        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(TableUserData, memory.Read, memory.Read), ref reference,
            ref referenceSpecialization));

        Assert.Equal(reference.FlattenedResourceTable, prefetched.FlattenedResourceTable);
        Assert.Equal(reference.Buffers.Select(buffer => buffer.ToArray()), prefetched.Buffers.Select(buffer => buffer.ToArray()));
        Assert.Equal(referenceSpecialization, prefetchedSpecialization);
    }

    [Fact]
    public void ACachedEntryValidatesThePrefetchedWords()
    {
        var (plan, _, _) = Prepare(FlattenedReadReuseTests.RepeatedReadProgram(4), userDataCount: 9);
        var memory = new TestWordMemory { Words = [11, 22, 33, 44] };
        var resident = new ResidentMemory(memory);
        var cache = new ResourceMaterializationCache();

        bool Run(out ResourceSnapshot snapshot)
        {
            snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            return cache.Materialize(plan, PrefetchInputs(memory, resident), resident.Read, ref snapshot, ref specialization, out _);
        }

        Assert.True(Run(out var first));
        Assert.Equal([11u, 22, 33, 44], first.FlattenedResourceTable);

        memory.Words[3] = 77;
        Assert.True(Run(out var second));
        Assert.Equal([11u, 22, 33, 77], second.FlattenedResourceTable);
        Assert.Equal((0, 1, 1), (cache.Hits, cache.Misses, cache.TableRefreshes));

        Assert.True(Run(out var third));
        Assert.Same(second, third);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void TheCleanFlagFollowsTheCleanReader()
    {
        GuestWordReader unclean = (ulong _, out uint word) => { word = 0; return false; };
        GuestWordReader clean = (ulong _, out uint word) => { word = 0; return false; };
        var inputs = new ResourceRuntimeInputs { ReadMemory = unclean, ReadCleanMemory = clean };

        Assert.False(inputs.ReadsClean);
        Assert.True(inputs.WithReader(clean).ReadsClean);
        Assert.False(inputs.WithReader(unclean).ReadsClean);
        Assert.True(inputs.WithReader(clean).WithReader(unclean).ReadsClean);
    }
}

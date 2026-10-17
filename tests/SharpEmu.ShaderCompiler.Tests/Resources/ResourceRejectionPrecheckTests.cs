// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceRejectionPrecheckTests
{
    private sealed class Fixture
    {
        public readonly ResourceMaterializationCache Cache = new();
        public readonly TestWordMemory Memory = new() { Words = [0x2000, 20u << 20, 0, 0xFACu | (9u << 28), 0, 0, 0, 0, 11] };
        public readonly ShaderResourcePlan Plan;
        public readonly List<ResourceSnapshot> Published = [];
        public int Reads;

        public Fixture()
        {
            // Image variants isolate rejection from learned buffer-base rebasing.
            (Plan, _, _) = Prepare(Program(ScalarLoad(0, 0, 4, 8),
                ScalarLoad(8, 0, 16, immediateOffset: 32),
                Vop1(16, "VMovB32", 4, Gen5Operand.Scalar(16)), Image(20, "ImageLoad", 4), EndProgram(28)), userDataCount: 9);
            for (var index = 0; index < 4; index++)
            {
                Memory.Words[0] = (uint)(0x2000 + index * 0x1000);
                var snapshot = new ResourceSnapshot();
                var specialization = new ResourceSpecialization();
                Assert.True(Run(Read, ref snapshot, ref specialization));
                Published.Add(snapshot);
            }
            Reads = 0;
        }

        public bool Read(ulong address, Span<byte> destination, bool clean)
        {
            Reads++;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!Memory.Read(address + (ulong)offset, out var word)) return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }
            return true;
        }

        public bool Run(ResidentGuestBytesReader reader, ref ResourceSnapshot snapshot, ref ResourceSpecialization specialization) =>
            Cache.Materialize(Plan, Inputs([0x1000, 0, 0, 0, 0, 0, 0, 0, 0], Memory.Read, Memory.Read),
                reader, ref snapshot, ref specialization, out _);

        public object OlderEntry()
        {
            var entries = (IDictionary)typeof(ResourceMaterializationCache)
                .GetField("_young", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Cache)!;
            var entry = entries.Values.Cast<object>().Single();
            return entry.GetType().GetProperty("Next")!.GetValue(entry)!;
        }
    }

    [Fact]
    public void ChangedDescriptorRejectsFourVariantsWithOneResidentRead()
    {
        var fixture = new Fixture();
        fixture.Memory.Words[0] = 0x6000;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(fixture.Run(fixture.Read, ref snapshot, ref specialization));
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(5, fixture.Cache.Misses);
        Assert.Equal(0, fixture.Cache.Hits);
        Assert.DoesNotContain(snapshot, fixture.Published);
        fixture.Reads = 0;
        var published = snapshot;
        Assert.True(fixture.Run(fixture.Read, ref snapshot, ref specialization));
        Assert.Equal(1, fixture.Reads);
        Assert.Same(published, snapshot);
    }

    [Fact]
    public void OlderMatchingVariantStillReadsItsDependencies()
    {
        var fixture = new Fixture();
        fixture.Memory.Words[0] = 0x3000;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(fixture.Run(fixture.Read, ref snapshot, ref specialization));
        Assert.Equal(2, fixture.Reads);
        Assert.Same(fixture.Published[1], snapshot);
        Assert.Equal(1, fixture.Cache.Hits);
    }

    [Fact]
    public void MutationAfterTheSampleCannotAcceptAnOlderSnapshot()
    {
        var fixture = new Fixture();
        fixture.Memory.Words[0] = 0x3000;
        bool Read(ulong address, Span<byte> bytes, bool clean)
        {
            var result = fixture.Read(address, bytes, clean);
            if (fixture.Reads == 1) fixture.Memory.Words[0] = 0x6000;
            return result;
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(fixture.Run(Read, ref snapshot, ref specialization));
        Assert.Equal(2, fixture.Reads);
        Assert.Equal(0, fixture.Cache.Hits);
        var expected = new ResourceSnapshot();
        var expectedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(fixture.Plan,
            Inputs([0x1000, 0, 0, 0, 0, 0, 0, 0, 0], fixture.Memory.Read, fixture.Memory.Read),
            ref expected, ref expectedSpecialization));
        Assert.Equal(expected.FlattenedResourceTable, snapshot.FlattenedResourceTable);
    }

    [Fact]
    public void UnreadableValidationDoesNotCreateARejectionSample()
    {
        var fixture = new Fixture();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var reads = 0;
        bool Refuse(ulong address, Span<byte> bytes, bool clean) { reads++; return false; }
        Assert.True(fixture.Run(Refuse, ref snapshot, ref specialization));
        Assert.Equal(4, reads);
        Assert.Equal(0, fixture.Cache.Hits);
        Assert.Equal(5, fixture.Cache.Misses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DifferentPolicyOrTableOnlyWordRequiresNormalValidation(bool changePolicy)
    {
        var fixture = new Fixture();
        var entry = fixture.OlderEntry();
        var property = changePolicy ? "RangeClean" : "WordTableOnly";
        var flags = (bool[])entry.GetType().GetProperty(property)!.GetValue(entry)!;
        flags[0] = true;
        fixture.Memory.Words[0] = 0x6000;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(fixture.Run(fixture.Read, ref snapshot, ref specialization));
        Assert.True(fixture.Reads >= 2);
        Assert.Equal(0, fixture.Cache.Hits);
    }

    [Fact]
    public void DifferentRangeLayoutRequiresNormalValidation()
    {
        var fixture = new Fixture();
        var entry = fixture.OlderEntry();
        var addresses = (ulong[])entry.GetType().GetProperty("RangeAddresses")!.GetValue(entry)!;
        addresses[0] += 4;
        fixture.Memory.Words[0] = 0x6000;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(fixture.Run(fixture.Read, ref snapshot, ref specialization));
        Assert.True(fixture.Reads >= 2);
        Assert.Equal(0, fixture.Cache.Hits);
    }

    [Fact]
    public void FailedMaterializationPreservesPublishedOutputsAfterRejection()
    {
        var fixture = new Fixture();
        fixture.Memory.Words[0] = 0x6000;
        var snapshot = fixture.Published[0];
        var specialization = new ResourceSpecialization();
        var originalSpecialization = specialization;
        var originalWords = snapshot.FlattenedResourceTable.ToArray();
        bool Read(ulong address, Span<byte> bytes, bool clean)
        {
            var result = fixture.Read(address, bytes, clean);
            fixture.Memory.FailAddress = 0x1000;
            return result;
        }
        Assert.False(fixture.Run(Read, ref snapshot, ref specialization));
        Assert.Same(fixture.Published[0], snapshot);
        Assert.Same(originalSpecialization, specialization);
        Assert.Equal(originalWords, snapshot.FlattenedResourceTable);
    }
}

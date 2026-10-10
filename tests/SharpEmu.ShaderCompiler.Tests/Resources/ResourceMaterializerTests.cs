// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceMaterializerTests
{
    private const uint Format32Float = 22;
    private const uint Format32Sint = 21;
    private const uint Format11x2x10Uint = 34;
    private const uint Format8x2Uscaled = 16;
    private const uint ImageType2D = 9;

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(65)]
    public void MaterialImageCapacityPreservesTheLimitAndPublishedState(int distinctCount)
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false));
        uint[] userData = [0x1000, 224 << 16, (uint)distinctCount, 0, 0x10000, 16 << 16, (uint)distinctCount * 2, 0, 7];
        var memory = new TestWordMemory { Words = new uint[0x11000 / 4] };
        for (var index = 0; index < distinctCount; index++)
        {
            memory.At(0x1000 + (ulong)index * 224 + 4) = (uint)index;
            var descriptor = ResourceTrackerTests.ImageDescriptor();
            descriptor[0] += (uint)index;
            ResourceTrackerTests.WriteImage(memory, 0x10000 + (ulong)index * 32, descriptor);
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var previousSnapshot = snapshot;
        var previousSpecialization = specialization;
        var success = ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
            ref snapshot, ref specialization, out var failure);
        Assert.Equal(distinctCount <= ShaderResourceInfo.MaxImages, success);
        if (success)
        {
            Assert.Equal(ResourceMaterializationFailure.None, failure);
            Assert.Equal(distinctCount, snapshot.Images.Length);
        }
        else
        {
            Assert.Equal(ResourceMaterializationFailure.ImageCapacityExceeded, failure);
            Assert.Same(previousSnapshot, snapshot);
            Assert.Same(previousSpecialization, specialization);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompatibleImageCapturePreservesFailureAndPublishedState(bool captureEnabled)
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false));
        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        var memory = ResourceTrackerTests.LinearMemory();
        var first = ResourceTrackerTests.ImageDescriptor();
        var second = first.ToArray();
        // A converted format cannot share a case with a directly sampled one.
        second[1] = (second[1] & ~(0x1FFu << 20)) | (GuestImageFormat.Format11x2x10Uint << 20);
        ResourceTrackerTests.WriteImage(memory, 0x2000, first);
        ResourceTrackerTests.WriteImage(memory, 0x2020, second);
        memory.At(0x1000 + 36) = 1;
        var inputs = Inputs(userData, readCleanMemory: memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var previousSnapshot = snapshot;
        var previousSpecialization = specialization;
        var captures = new List<IndirectImageFailure>();

        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization,
            out var failureKind, captureEnabled ? captures.Add : null));
        Assert.Equal(ResourceMaterializationFailure.IncompatibleImageCandidates, failureKind);
        Assert.Same(previousSnapshot, snapshot);
        Assert.Same(previousSpecialization, specialization);
        if (captureEnabled)
        {
            var failure = Assert.Single(captures);
            Assert.Equal(plan.Info.Images[0].FirstUsePc, failure.InstructionAddress);
            Assert.Equal(0u, failure.RootResource);
            Assert.Equal(0u, failure.ExemplarResource);
            Assert.Equal(1u, failure.IncompatibleResource);
            Assert.Equal(userData[..4], failure.MaterialDescriptor);
            Assert.Equal(userData[4..8], failure.HeapDescriptor);
            Assert.Equal(new uint[] { 0, 1 }, failure.Keys);
            Assert.Equal(new uint[] { 0, 1 }, failure.CandidateIndices);
            Assert.Equal(first, failure.TableDescriptors[0]);
            Assert.Equal(second, failure.TableDescriptors[1]);
            Assert.Equal(second, failure.ImageDescriptors[1]);
            Assert.NotEqual(failure.ImageSpecializations[0].ConversionFormat, failure.ImageSpecializations[1].ConversionFormat);
            Assert.Equal(userData, failure.UserData);
            var diagnostic = Assert.IsType<IndirectSelectorDiagnostic>(failure.SelectorDiagnostic);
            Assert.Equal("full_domain_no_proof", diagnostic.SelectionMode);
            Assert.Null(diagnostic.EvaluationFailure);
            Assert.Empty(diagnostic.MemoryReads);
            Assert.Equal(new SelectorKeyProbe(36, 1), Assert.Single(diagnostic.KeyProbes));
        }
        else
        {
            Assert.Empty(captures);
        }

        ResourceTrackerTests.WriteImage(memory, 0x2020, first);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, captures.Add));
        Assert.Equal(captureEnabled ? 1 : 0, captures.Count);
    }

    [Fact]
    public void FailureCaptureIncludesBothScalarLoadComponentsWithoutExtraReads()
    {
        var original = ResourceTrackerTests.IndirectImageProgram(false);
        var plan = Extract(Program([ScalarLoad(0xF00, 40, 52, count: 2), .. original.Instructions]));
        var registers = new uint[64];
        new uint[] { 0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0 }.CopyTo(registers, 0);
        registers[40] = 0x3000;
        var memory = ResourceTrackerTests.LinearMemory();
        memory.At(0x3000) = 0;
        memory.At(0x3004) = 1;
        memory.At(0x1000 + 36) = 1;
        var first = ResourceTrackerTests.ImageDescriptor();
        var second = first.ToArray();
        // A converted format cannot share a case with a directly sampled one.
        second[1] = (second[1] & ~(0x1FFu << 20)) | (GuestImageFormat.Format11x2x10Uint << 20);
        ResourceTrackerTests.WriteImage(memory, 0x2000, first);
        ResourceTrackerTests.WriteImage(memory, 0x2020, second);
        var addresses = new List<ulong>();
        bool ReadWord(ulong address, out uint word)
        {
            addresses.Add(address);
            return memory.Read(address, out word);
        }
        var inputs = Inputs(registers, readMemory: ReadWord, readCleanMemory: ReadWord);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        var baselineAddresses = addresses.ToArray();
        addresses.Clear();
        var captures = new List<IndirectImageFailure>();
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, captures.Add));
        Assert.Equal(baselineAddresses, addresses);
        var reads = Assert.Single(captures).ScalarReads.Where(read => read.InstructionAddress == 0xF00).ToArray();
        Assert.Equal(2, reads.Length);
        Assert.Equal(0u, Assert.Single(reads, read => read.ComponentIndex == 0).Value);
        Assert.Equal(1u, Assert.Single(reads, read => read.ComponentIndex == 1).Value);
    }

    // A 116-byte-stride material table with a selector offset of 4: the exhaustive probe step is
    // 4 bytes, so a table of 2260+ records exceeds the probe cap. Keys 1 and 2 sit at record
    // aligned offsets (4 + 116 * 3 and 4 + 116 * 2000); key 3 is a poison value at a misaligned
    // offset (4 + 116 * 10 + 8) that only the exhaustive enumeration can reach.
    private const uint AlignedStride = 116;
    private const uint AlignedKeyOneOffset = 4 + AlignedStride * 3;
    private const uint AlignedKeyTwoOffset = 4 + AlignedStride * 2000;
    private const uint MisalignedKeyThreeOffset = 4 + AlignedStride * 10 + 8;

    private static (ShaderResourcePlan Plan, ResourceRuntimeInputs Inputs, uint[][] Heap) RecordAlignedTable(uint records, bool incompatibleKeyTwo = false)
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false, selectorStride: AlignedStride));
        var selector = plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage!;
        Assert.Equal(AlignedStride, selector.SelectorStride);
        Assert.Equal(4u, selector.SelectorOffset);

        const ulong heapBase = 0x60000;
        uint[] userData = [0x1000, AlignedStride << 16, records, 0, (uint)heapBase, 16 << 16, 16, 0, 7];
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0x60000 / 4], RequireAlignment = true };
        var heap = new uint[4][];
        for (var key = 0; key < heap.Length; key++)
        {
            heap[key] = ResourceTrackerTests.ImageDescriptor();
            heap[key][0] += (uint)key;
        }

        if (incompatibleKeyTwo)
        {
            // A converted format cannot share a case with a directly sampled one.
            heap[2][1] = (heap[2][1] & ~(0x1FFu << 20)) | (GuestImageFormat.Format11x2x10Uint << 20);
        }

        for (var key = 0; key < heap.Length; key++)
        {
            ResourceTrackerTests.WriteImage(memory, heapBase + (ulong)key * 32, heap[key]);
        }

        memory.At(0x1000 + AlignedKeyOneOffset) = 1;
        memory.At(0x1000 + AlignedKeyTwoOffset) = 2;
        memory.At(0x1000 + MisalignedKeyThreeOffset) = 3;
        return (plan, Inputs(userData, readCleanMemory: memory.Read), heap);
    }

    // 2259 records keep the exhaustive probe count under the cap (65512 offsets, step 4): the
    // misaligned key is found as before. 2260 records exceed it (65541) and probe only the
    // record-aligned offsets instead of failing.
    [Theory]
    [InlineData(2259u, true)]
    [InlineData(2260u, false)]
    public void UnboundedSelectorProbesFallBackToRecordAlignedOffsetsOnlyPastTheCap(uint records, bool exhaustive)
    {
        var (plan, inputs, heap) = RecordAlignedTable(records);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal(exhaustive ? 4 : 3, snapshot.Images.Length);
        for (var key = 0; key < 3; key++)
        {
            Assert.Contains(snapshot.Images, image => image.SequenceEqual(heap[key]));
        }

        Assert.Equal(exhaustive, snapshot.Images.Any(image => image.SequenceEqual(heap[3])));
    }

    [Theory]
    [InlineData(2259u, false)]
    [InlineData(2260u, true)]
    public void RecordAlignedFallbackReportsTheProbedOffsets(uint records, bool fallback)
    {
        var (plan, inputs, _) = RecordAlignedTable(records, incompatibleKeyTwo: true);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var captures = new List<IndirectImageFailure>();

        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization,
            out var failureKind, captures.Add));
        Assert.Equal(ResourceMaterializationFailure.IncompatibleImageCandidates, failureKind);
        var diagnostic = Assert.IsType<IndirectSelectorDiagnostic>(Assert.Single(captures).SelectorDiagnostic);
        Assert.Equal(fallback ? "record_aligned_fallback" : "full_domain_no_proof", diagnostic.SelectionMode);
        SelectorKeyProbe[] expected = fallback ?
            [new(AlignedKeyOneOffset, 1), new(AlignedKeyTwoOffset, 2)] :
            [new(AlignedKeyOneOffset, 1), new(MisalignedKeyThreeOffset, 3), new(AlignedKeyTwoOffset, 2)];
        Assert.Equal(expected, diagnostic.KeyProbes);
    }

    // 70000 records leave more than the cap even among the record-aligned offsets.
    [Fact]
    public void UnboundedSelectorProbesStillFailWhenTheAlignedOffsetsExceedTheCap()
    {
        var (plan, inputs, _) = RecordAlignedTable(70000);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var previousSnapshot = snapshot;
        var previousSpecialization = specialization;

        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization, out var failureKind));
        Assert.Equal(ResourceMaterializationFailure.Other, failureKind);
        Assert.Contains("unbounded selector probes exceed 65536", ResourceMaterializer.LastFailureDetail);
        Assert.Same(previousSnapshot, snapshot);
        Assert.Same(previousSpecialization, specialization);
    }

    private static IEnumerable<Gen5ShaderInstruction> ImageWords(ref uint pc, uint register, uint address, uint format)
    {
        uint[] words = [address, format << 20, 3 | (3 << 14), 0xFAC | (ImageType2D << 28), 0, 0, 0, 0];
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint dword = 0; dword < 8; dword++)
        {
            instructions.Add(MoveScalar(pc, register + dword, words[dword]));
            pc += 8;
        }

        return instructions;
    }

    private static IEnumerable<Gen5ShaderInstruction> SamplerWords(ref uint pc, uint register, uint dword0)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (uint dword = 0; dword < 4; dword++)
        {
            instructions.Add(MoveScalar(pc, register + dword, dword == 0 ? dword0 : 0));
            pc += 8;
        }

        return instructions;
    }

    [Fact]
    public void MappedTable_UsesTheDirectReaderByDefault()
    {
        var plan = Extract(Program(
            MoveScalar(0, 4, 0x1000),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, 4, destination: 6),
            EndProgram(16)));
        var cleanReads = 0;
        var inputs = Inputs(
            [],
            readMemory: (ulong address, out uint word) => { word = 0x12345678; return address == 0x1000; },
            readCleanMemory: (ulong address, out uint word) => { cleanReads++; word = 0; return false; });
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal(0, cleanReads);
        Assert.Equal([0x12345678u], snapshot.FlattenedResourceTable);
    }

    [Fact]
    public void UnbasedFlatCacheHit_Materializes()
    {
        var plan = Extract(Program(GlobalAccess(0, "FlatStoreDword", 0, vectorAddress: 2), EndProgram(8)));
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([]), ref snapshot, ref specialization));
        Assert.Empty(snapshot.Buffers);
        Assert.Empty(snapshot.Images);
    }

    [Fact]
    public void FailedMaterialization_LeavesSnapshotAndSpecializationUnchanged()
    {
        var plan = Extract(Program(BufferLoad(0, 0), EndProgram(8)));
        var snapshot = new ResourceSnapshot { UserData = [0xFEEDBEEF] };
        var specialization = new ResourceSpecialization { Buffers = [new BufferSpecialization(7, 0, 0)] };
        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;

        Assert.False(ResourceMaterializer.Materialize(plan, Inputs([]), ref snapshot, ref specialization));
        Assert.Same(priorSnapshot, snapshot);
        Assert.Same(priorSpecialization, specialization);
        Assert.Equal([0xFEEDBEEFu], snapshot.UserData);
        Assert.Equal(7u, specialization.Buffers[0].PackedStride);
    }

    [Fact]
    public void MixedSampler_DuplicatesTheCorrectSnapshot()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, 16, 0x1000, Format32Float));
        instructions.AddRange(ImageWords(ref pc, 24, 0x2000, Format11x2x10Uint));
        instructions.AddRange(SamplerWords(ref pc, 32, 0x11111111));
        instructions.AddRange(SamplerWords(ref pc, 36, 0x22222222));
        instructions.Add(Image(pc, "ImageSample", 16, 32));
        instructions.Add(Image(pc + 8, "ImageSample", 16, 36));
        instructions.Add(Image(pc + 16, "ImageSample", 24, 36));
        instructions.Add(EndProgram(pc + 24));
        var plan = Extract(Program([.. instructions]));
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([]), ref snapshot, ref specialization));
        Assert.Equal(3, snapshot.Samplers.Length);
        Assert.Equal(snapshot.Samplers[1], snapshot.Samplers[2]);
        Assert.NotEqual(snapshot.Samplers[0], snapshot.Samplers[2]);
    }

    [Fact]
    public void ScaledImage_MaterializesAsAFloatSampledImage()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, 16, 0x1000, Format8x2Uscaled));
        instructions.AddRange(SamplerWords(ref pc, 24, 0));
        instructions.Add(Image(pc, "ImageSample", 16, 24));
        instructions.Add(EndProgram(pc + 8));
        var plan = Extract(Program([.. instructions]));
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([]), ref snapshot, ref specialization));
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        var image = Assert.Single(applied.Info.Images);
        Assert.Equal(ImageNumericClass.Float, image.NumericClass);
    }

    // Three images share one sampler; the packed and signed ones need point filtering,
    // so the sampler splits and their accesses sample through the duplicate.
    [Fact]
    public void SignedImage_SplitsTheSharedSamplerIntoPointAndNativeVariants()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.AddRange(ImageWords(ref pc, 16, 0x1000, Format32Float));
        instructions.AddRange(ImageWords(ref pc, 24, 0x2000, Format11x2x10Uint));
        instructions.AddRange(ImageWords(ref pc, 32, 0x3000, Format32Sint));
        instructions.AddRange(SamplerWords(ref pc, 40, 0));
        var floatPc = pc;
        var packedPc = pc + 8;
        var signedPc = pc + 16;
        instructions.Add(Image(floatPc, "ImageSample", 16, 40));
        instructions.Add(Image(packedPc, "ImageSample", 24, 40));
        instructions.Add(Image(signedPc, "ImageSample", 32, 40));
        instructions.Add(EndProgram(pc + 24));
        var plan = Extract(Program([.. instructions]));
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([]), ref snapshot, ref specialization));
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(2, applied.Info.Samplers.Count);
        Assert.False(applied.Info.Samplers[0].ForcePointFiltering);
        Assert.True(applied.Info.Samplers[1].ForcePointFiltering);
        Assert.Equal(0u, applied.Info.SampledPairs[0].Sampler);
        Assert.Equal(1u, applied.Info.SampledPairs[1].Sampler);
        Assert.Equal(1u, applied.Info.SampledPairs[2].Sampler);
        Assert.True(plan.Memory.TryGetIndex(signedPc, 0, out var signedIndex));
        Assert.Equal(1u, applied.SamplerByMemoryIndex[signedIndex]);
        Assert.True(plan.Memory.TryGetIndex(floatPc, 0, out var floatIndex));
        Assert.False(applied.SamplerByMemoryIndex.ContainsKey(floatIndex));
        Assert.Equal(2, snapshot.Samplers.Length);
    }
}

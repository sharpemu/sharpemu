// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Metal;
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
    [InlineData("write")]
    [InlineData("atomic")]
    [InlineData("compare")]
    [InlineData("numeric")]
    [InlineData("dimension")]
    [InlineData("swizzle")]
    public void SharedSampledCandidatesRejectDifferentUseSemantics(string difference)
    {
        var first = new ImageResource { ResourceClass = ImageResourceClass.Sampled, Read = true,
            NumericClass = ImageNumericClass.Float, Dimension = ImageDimension.Dim2D };
        var second = first.Clone();
        Assert.True(ResourceMaterializer.CanShareSampledImageUse(first, second));
        switch (difference)
        {
            case "write": second.Written = true; break;
            case "atomic": second.Atomic = true; break;
            case "compare": second.DepthCompare = true; break;
            case "numeric": second.NumericClass = ImageNumericClass.Uint; break;
            case "dimension": second.Dimension = ImageDimension.Dim3D; break;
            case "swizzle": second.ShaderSwizzle ^= 1; break;
        }
        Assert.False(ResourceMaterializer.CanShareSampledImageUse(first, second));
        Assert.False(ResourceMaterializer.CanShareSampledImageUse(second, first));
    }

    [Fact]
    public void SharedImageCandidateOrderIsPartOfTheSpecializationIdentity()
    {
        var first = new ResourceSpecialization { IndirectImageCandidates = [(0, 0), (0, 2), (1, 1), (1, 2)] };
        var clone = first.Clone();
        Assert.Equal(first, clone);
        Assert.Equal(first.GetHashCode(), clone.GetHashCode());
        clone.IndirectImageCandidates[1] = (0, 1);
        Assert.NotEqual(first, clone);
        Assert.Equal((0u, 2u), first.IndirectImageCandidates[1]);
        var reordered = first.Clone();
        reordered.IndirectImageCandidates.Reverse();
        Assert.NotEqual(first, reordered);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(false, 130)]
    [InlineData(true, 3)]
    [InlineData(true, 130)]
    public void RepeatedSampledTablesShareCompatibleCandidatesAndKeepRootOrdering(bool differentViewWidth, int candidateCount)
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false));
        var secondRoot = plan.Info.Images[0].Clone();
        if (differentViewWidth) secondRoot.R128 = !secondRoot.R128;
        plan.Info.Images.Add(secondRoot);
        plan.Info.SampledPairs.Add(new SampledImagePair { Image = 1, Sampler = 0 });
        uint[] userData = [0x1000, 224 << 16, (uint)candidateCount, 0, 0x10000, 16 << 16, (uint)candidateCount * 2, 0, 7];
        var memory = new TestWordMemory { Words = new uint[0x13000 / 4] };
        for (var index = 0; index < candidateCount; index++)
        {
            memory.At(0x1000 + (ulong)index * 224 + 4) = (uint)index;
            var descriptor = ResourceTrackerTests.ImageDescriptor();
            descriptor[0] += (uint)index;
            ResourceTrackerTests.WriteImage(memory, 0x10000 + (ulong)index * 32, descriptor);
        }
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read),
            ref snapshot, ref specialization, out var failure), failure.ToString());
        Assert.Equal(differentViewWidth ? candidateCount * 2 : candidateCount + 1, snapshot.Images.Length);
        var first = specialization.IndirectImageCandidates.Where(edge => edge.Root == 0).Select(edge => edge.Candidate).ToArray();
        var second = specialization.IndirectImageCandidates.Where(edge => edge.Root == 1).Select(edge => edge.Candidate).ToArray();
        Assert.Equal(0u, first[0]);
        Assert.Equal(1u, second[0]);
        Assert.Equal(candidateCount, first.Length);
        Assert.Equal(candidateCount, second.Length);
        if (!differentViewWidth) Assert.Equal(first.Skip(1), second.Skip(1));
        else Assert.Empty(first.Intersect(second));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(first, resources.Info.Images[0].IndirectResources);
        Assert.Equal(second, resources.Info.Images[1].IndirectResources);
        var layout = BindingLayout.Allocate(resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64), false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources), false);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out error), error);

    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(256)]
    [InlineData(257)]
    public void MaterialImageCapacityPreservesTheLimitAndPublishedState(int distinctCount)
    {
        var plan = Extract(ResourceTrackerTests.IndirectImageProgram(false));
        uint[] userData = [0x1000, 224 << 16, (uint)distinctCount, 0, 0x10000, 16 << 16, (uint)distinctCount * 2, 0, 7];
        var memory = new TestWordMemory { Words = new uint[0x13000 / 4] };
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
        second[3] = (second[3] & 0x0FFFFFFF) | (10u << 28);
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
            Assert.NotEqual(failure.ImageSpecializations[0].Dimension, failure.ImageSpecializations[1].Dimension);
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
        second[3] = (second[3] & 0x0FFFFFFF) | (10u << 28);
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

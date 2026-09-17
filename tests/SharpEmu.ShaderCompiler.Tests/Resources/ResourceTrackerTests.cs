// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceTrackerTests
{
    private const uint Format32x4Float = 77;
    private const uint Format32x2Float = 64;
    private const uint ImageType2D = 9;
    private const uint IdentitySwizzle = 0xFAC;

    [Fact]
    public void DenseBufferTracking()
    {
        var program = Program(
            BufferLoad(4, 0, offset: 4, formatted: true),
            BufferStore(8, 0, offset: 12, formatted: true),
            BufferAtomicAdd(12, 0),
            BufferLoad(28, 4, offset: 4, formatted: true),
            EndProgram(36));
        var plan = Extract(program);

        Assert.Equal(2, plan.Info.Buffers.Count);
        Assert.Equal(2, plan.DescriptorSources.Count);
        var resource = plan.Info.Buffers[0];
        Assert.True(resource.Read && resource.Written && resource.Atomic && resource.Formatted);
        Assert.Equal(16u, resource.MaxByteExtent);
        Assert.Equal(4u, resource.FirstUsePc);
        Assert.Equal(0u, plan.Memory[0].Resource);
        Assert.Equal(0u, plan.Memory[1].Resource);
        Assert.Equal(0u, plan.Memory[2].Resource);
        Assert.Equal(1u, plan.Memory[3].Resource);
    }

    [Fact]
    public void ScalarAndVectorBufferAlias()
    {
        var program = Program(
            ScalarBufferLoad(4, 0, destination: 8, dynamicOffsetRegister: 4),
            BufferLoad(8, 0),
            EndProgram(12));
        var plan = Extract(program);

        var buffer = Assert.Single(plan.Info.Buffers);
        Assert.True(buffer.Scalar);
        Assert.Equal(0u, plan.Memory[0].Resource);
        Assert.Equal(0u, plan.Memory[1].Resource);
    }

    [Fact]
    public void RuntimeUnsignedMinDescriptor()
    {
        var program = Program(
            MoveScalar(0, 8, 0),
            MoveScalar(4, 9, 0),
            MoveScalar(8, 10, 64),
            Sop2(12, "SMinU32", 11, Gen5Operand.Scalar(0), Operand(0x100)),
            BufferLoad(0x330, 8),
            EndProgram(0x338));
        var plan = Extract(program);
        var source = plan.Info.Buffers[0].Source;

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source, Inputs([0xFFFFFFFF]), out var clamped));
        Assert.Equal(0x100u, clamped.Dwords[3]);
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, source, Inputs([0x80]), out var kept));
        Assert.Equal(0x80u, kept.Dwords[3]);
    }

    [Fact]
    public void ImagesSamplersAndAliases()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        foreach (var (register, value) in new (uint, uint)[] { (16, 0), (17, 1), (18, 2), (19, 0x1111), (20, 0), (21, 1), (22, 2), (23, 0x2222) })
        {
            instructions.Add(MoveScalar(pc, register, value));
            pc += 4;
        }

        instructions.Add(Image(0x40, "ImageSample", 0, 16));
        instructions.Add(Image(0x48, "ImageSample", 0, 20));
        instructions.Add(Image(0x50, "ImageSampleCLz", 0, 16));
        instructions.Add(Image(0x58, "ImageAtomicAdd", 0));
        instructions.Add(BufferLoad(0x60, 0));
        instructions.Add(EndProgram(0x68));
        var plan = Extract(Program([.. instructions]));

        Assert.Equal(3, plan.Info.Images.Count);
        Assert.Single(plan.Info.Samplers);
        Assert.Equal(2, plan.Info.SampledPairs.Count);
        var normal = plan.Memory.Find(0x40)!;
        var repeated = plan.Memory.Find(0x48)!;
        var compare = plan.Memory.Find(0x50)!;
        Assert.Equal(normal.Resource, repeated.Resource);
        Assert.NotEqual(normal.Resource, compare.Resource);
        Assert.Equal(0u, normal.Sampler);
        Assert.Equal(0u, repeated.Sampler);
        var samplerSource = plan.DescriptorSources[(int)plan.Info.Samplers[0].Source];
        Assert.True(samplerSource.Dwords[3].IsConstant);
        Assert.Equal(0u, samplerSource.Dwords[3].ConstantU32);
        Assert.Equal(0u, plan.Info.Buffers[0].ImageAlias);
    }

    [Fact]
    public void SamplerWithDivergentBits_IsRejected()
    {
        var program = Program(
            MoveScalarRegister(0, 16, 0),
            MoveScalarRegister(4, 17, 1),
            MoveScalarRegister(8, 18, 2),
            Vop2(12, "VLshlrevB32", 1, Operand(12), Gen5Operand.Vector(0)),
            ReadFirstLane(16, 20, 1),
            Sop2(20, "SOrB32", 19, Gen5Operand.Scalar(3), Gen5Operand.Scalar(20)),
            Image(0x200, "ImageSample", 8, 16),
            EndProgram(0x208));

        var error = Assert.Throws<ResourcePlanException>(() => Extract(program));
        Assert.Contains("not a valid runtime value", error.Message);
        Assert.Contains("pc=0x00000200", error.Message);
    }

    private static uint[] StorageDescriptorUserData(uint mipBase, uint mipLast) =>
    [
        0x1000, Format32x4Float << 20, 3 | (3 << 14), IdentitySwizzle | (mipBase << 12) | (mipLast << 16) | (ImageType2D << 28), 0, 3 << 4, 0, 0, 2,
    ];

    [Fact]
    public void DynamicStorageMipTracking()
    {
        var program = Program(
            Image(4, "ImageStore", 0),
            Vop1(12, "VMovB32", 2, Operand(1)),
            Image(16, "ImageStoreMip", 0),
            Vop1(24, "VMovB32", 2, Operand(2)),
            Image(28, "ImageStoreMip", 0),
            Vop1(36, "VMovB32", 2, Gen5Operand.Scalar(8)),
            Image(40, "ImageStoreMip", 0),
            EndProgram(48));
        var plan = Extract(program);

        var images = plan.Info.Images;
        Assert.Equal(2, images.Count);
        Assert.Equal(ImageMipMode.None, images[0].MipMode);
        Assert.Equal(1u, images[0].MipCount);
        Assert.Equal(ImageMipMode.DynamicStorage, images[1].MipMode);
        Assert.Equal(1u, images[1].MipCount);
        Assert.Equal(0u, plan.Memory.Find(4)!.Resource);
        Assert.Equal(1u, plan.Memory.Find(16)!.Resource);
        Assert.Equal(1u, plan.Memory.Find(28)!.Resource);
        Assert.Equal(1u, plan.Memory.Find(40)!.Resource);

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(StorageDescriptorUserData(1, 3)), ref snapshot, ref specialization));
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(3u, applied.Info.Images[1].MipCount);
        Assert.Equal(applied.Info.Images.Count, snapshot.Images.Length);
        var layout = BindingLayout.Allocate(applied.Info, [], false, false, false);
        var storageKind = ImageDescriptorBinding.ForImage(applied.Info.Images[0]);
        Assert.NotNull(storageKind);
        var storageBinding = layout.Find(storageKind!.Value);
        Assert.NotNull(storageBinding);
        Assert.Equal([0u, 1u, 1u, 1u], storageBinding!.Resources);

        var nullProgram = Program(
            [.. Enumerable.Range(0, 8).Select(index => MoveScalar((uint)index * 4, 16 + (uint)index, 0)),
             Vop1(32, "VMovB32", 2, Gen5Operand.Scalar(0)),
             Image(36, "ImageStoreMip", 16),
             EndProgram(44)]);
        var nullPlan = Extract(nullProgram);
        var nullSnapshot = new ResourceSnapshot();
        var nullSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(nullPlan, Inputs([0]), ref nullSnapshot, ref nullSpecialization));
        Assert.Equal(1u, ResourceMaterializer.ApplyTo(nullPlan, nullSpecialization).Info.Images[0].MipCount);
        Assert.Single(nullSnapshot.Images);

        var changedSnapshot = new ResourceSnapshot();
        var changedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(StorageDescriptorUserData(1, 2)), ref changedSnapshot, ref changedSpecialization));
        Assert.NotEqual(specialization, changedSpecialization);

        var validSnapshot = changedSnapshot;
        var validSpecialization = changedSpecialization;
        Assert.False(ResourceMaterializer.Materialize(plan, Inputs(StorageDescriptorUserData(4, 3)), ref changedSnapshot, ref changedSpecialization));
        Assert.Same(validSnapshot, changedSnapshot);
        Assert.Same(validSpecialization, changedSpecialization);
    }

    // The material table s[0:3], the heap s[4:7], the key selector in s8; the image words
    // come from the heap record the key selects.
    internal static Gen5ShaderProgram IndirectImageProgram(bool malformed, int materialImmediate = 0, bool memoryBackedMaterial = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0x1000;
        void Add(Gen5ShaderInstruction instruction) { instructions.Add(instruction); pc += 8; }
        Gen5ShaderInstruction At(Func<uint, Gen5ShaderInstruction> make) => make(pc);

        if (memoryBackedMaterial)
        {
            Add(At(current => ScalarLoad(current, 9, destination: 28)));
            Add(At(current => MoveScalar(current, 29, 0)));
            Add(At(current => ScalarLoad(current, 28, destination: 0)));
        }

        Add(At(current => Vop1(current, "VMovB32", 1, Gen5Operand.Scalar(8))));
        Add(At(current => ReadFirstLane(current, 9, 1)));
        Add(At(current => Sop2(current, "SMulI32", 10, Gen5Operand.Scalar(9), Operand(224))));
        Add(At(current => Sop2(current, "SAddU32", 11, Gen5Operand.Scalar(10), Operand(4))));
        Add(At(current => ScalarBufferLoad(current, 0, destination: 12, immediateOffset: materialImmediate, dynamicOffsetRegister: 11)));
        Add(At(current => Sop2(current, "SLshlB32", 13, Gen5Operand.Scalar(12), Operand(5))));
        if (malformed)
        {
            Add(At(current => ScalarBufferLoad(current, 4, destination: 16, count: 4, dynamicOffsetRegister: 13)));
            Add(At(current => ScalarBufferLoad(current, 4, destination: 20, count: 2, immediateOffset: 16, dynamicOffsetRegister: 13)));
            Add(At(current => ScalarBufferLoad(current, 4, destination: 22, immediateOffset: 24, dynamicOffsetRegister: 13)));
            Add(At(current => ScalarBufferLoad(current, 4, destination: 23, immediateOffset: 32, dynamicOffsetRegister: 13)));
        }
        else
        {
            Add(At(current => ScalarBufferLoad(current, 4, destination: 16, count: 8, dynamicOffsetRegister: 13)));
        }

        for (uint index = 0; index < 4; index++)
        {
            Add(At(current => MoveScalar(current, 24 + index, 0)));
        }

        Add(At(current => Image(current, "ImageSample", 16, 24)));
        Add(At(EndProgram));
        return Program([.. instructions]);
    }

    internal static uint[] ImageDescriptor() =>
        [0x20, Format32x4Float << 20, 3 | (3 << 14), IdentitySwizzle | (ImageType2D << 28), 0, 0, 0, 0];

    internal static TestWordMemory LinearMemory() => new() { Base = 0x1000, Words = new uint[0x2200 / 4], RequireAlignment = true };

    internal static void WriteImage(TestWordMemory memory, ulong address, uint[] descriptor)
    {
        for (var dword = 0; dword < descriptor.Length; dword++)
        {
            memory.At(address + (ulong)dword * 4) = descriptor[dword];
        }
    }

    [Fact]
    public void InvariantIndirectImageMaterialization()
    {
        var plan = Extract(IndirectImageProgram(false));
        Assert.Single(plan.Info.Buffers);
        Assert.Single(plan.Info.Images);
        Assert.Single(plan.DynamicReads);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.NotNull(source.IndirectImage);
        Assert.Single(plan.IndirectImages);
        Assert.Equal(ScalarValueKind.ScalarBufferWord, plan.IndirectImages[0].Key.Kind);

        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7];
        var memory = LinearMemory();
        var descriptor = ImageDescriptor();
        WriteImage(memory, 0x2000, descriptor);
        WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2020) ^= 1;
        var inputs = Inputs(userData, readCleanMemory: memory.Read);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Single(snapshot.Images);
        Assert.Equal(descriptor, snapshot.Images[0]);

        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;
        memory.FailAddress = 0x1004;
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Same(priorSnapshot, snapshot);
        Assert.Same(priorSpecialization, specialization);
        memory.FailAddress = ulong.MaxValue;

        memory.At(0x1000 + 36) = 1;
        for (uint dword = 0; dword < 8; dword++)
        {
            memory.At(0x2000 + dword * 4) = 0;
            memory.At(0x2020 + dword * 4) = 0;
        }

        memory.At(0x2004) = descriptor[1];
        memory.At(0x200C) = descriptor[3];
        memory.At(0x2024) = descriptor[1];
        memory.At(0x202C) = descriptor[3] ^ (1u << 28);
        var nullSnapshot = new ResourceSnapshot();
        var nullSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref nullSnapshot, ref nullSpecialization));
        Assert.All(nullSnapshot.Images[0], word => Assert.Equal(0u, word));

        WriteImage(memory, 0x2000, descriptor);
        WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2020) ^= 1;
        memory.At(0x1000 + 36) = 1;
        var dynamicSnapshot = new ResourceSnapshot();
        var dynamicSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref dynamicSnapshot, ref dynamicSpecialization));
        Assert.Equal(2, dynamicSnapshot.Images.Length);
        Assert.Equal(2, dynamicSpecialization.Images.Count);
        var applied = ResourceMaterializer.ApplyTo(plan, dynamicSpecialization);
        Assert.Equal(2, applied.Info.Images.Count);
        Assert.Equal(0u, applied.Info.Images[0].IndirectRoot);
        Assert.NotEqual(0u, applied.Info.Images[0].IndirectSearchIterations);
        Assert.Equal(2, applied.Info.Images[0].IndirectResources.Count);
        var mapping = dynamicSpecialization.Images[0];
        var keyCount = dynamicSnapshot.FlattenedResourceTable[mapping.IndirectMappingOffset];
        Assert.Equal(2u, mapping.IndirectSearchIterations);
        Assert.Equal((uint)dynamicSnapshot.FlattenedResourceTable.Length, mapping.IndirectMappingOffset + 1 + keyCount * 2);

        WriteImage(memory, 0x2000, descriptor);
        WriteImage(memory, 0x2020, descriptor);
        memory.At(0x2000) += 0x100;
        memory.At(0x2020) += 0x101;
        var reboundSnapshot = new ResourceSnapshot();
        var reboundSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref reboundSnapshot, ref reboundSpecialization));
        Assert.Equal(dynamicSpecialization, reboundSpecialization);
        memory.At(0x2020) = memory.At(0x2000);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref reboundSnapshot, ref reboundSpecialization));
        Assert.NotEqual(dynamicSpecialization, reboundSpecialization);
        var collapsedSpecialization = reboundSpecialization;

        foreach (var records in new uint[] { 1, 3 })
        {
            userData[2] = records;
            var capacitySnapshot = new ResourceSnapshot();
            var capacitySpecialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref capacitySnapshot, ref capacitySpecialization));
        }

        userData[2] = 2;
        memory.At(0x2020) = memory.At(0x2000) + 1;
        memory.At(0x2040) = memory.At(0x2000) + 2;
        for (uint dword = 1; dword < 8; dword++)
        {
            memory.At(0x2040 + dword * 4) = descriptor[dword];
        }

        memory.At(0x1000 + 68) = 2;
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref reboundSnapshot, ref reboundSpecialization));
        Assert.NotEqual(collapsedSpecialization, reboundSpecialization);
    }

    [Fact]
    public void IndirectImage_MemoryBackedMaterialReadsThroughTheCleanReader()
    {
        var plan = Extract(IndirectImageProgram(false, memoryBackedMaterial: true));
        Assert.Equal(2, plan.TableReads.Count);
        Assert.All(plan.CleanFlatSlots, slot => Assert.Equal(1, slot));

        uint[] userData = [0x1000, 224 << 16, 2, 0, 0x2000, 16 << 16, 4, 0, 7, 0x3100, 0];
        var memory = LinearMemory();
        WriteImage(memory, 0x2000, ImageDescriptor());
        memory.At(0x3100) = 0x3000;
        memory.At(0x3000) = 0x1000;
        var dirtyReads = 0;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var inputs = Inputs(userData, readMemory: (ulong address, out uint word) => { dirtyReads++; return memory.Read(address, out word); }, readCleanMemory: memory.Read);
        Assert.True(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal(0, dirtyReads);
        Assert.Equal(ImageDescriptor(), snapshot.Images[0]);

        memory.FailAddress = 0x3100;
        var priorSnapshot = snapshot;
        var priorSpecialization = specialization;
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Same(priorSnapshot, snapshot);
        Assert.Same(priorSpecialization, specialization);
    }

    [Fact]
    public void MalformedIndirectImage_IsRejected()
    {
        var error = Assert.Throws<ResourcePlanException>(() => Extract(IndirectImageProgram(true)));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    [Fact]
    public void WrappedScalarImmediate_DoesNotEnterTheIndirectImageProof()
    {
        var error = Assert.Throws<ResourcePlanException>(() => Extract(IndirectImageProgram(false, materialImmediate: 4)));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    [Fact]
    public void ResourceTableFlatteningAndRuntimeMemoization()
    {
        var program = Program(
            ScalarLoad(4, 0, destination: 8, immediateOffset: 4),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 64),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            MoveScalarRegister(28, 12, 8),
            MoveScalar(32, 13, 0),
            MoveScalar(36, 14, 64),
            MoveScalar(40, 15, 0),
            BufferLoad(44, 12),
            EndProgram(48));
        var plan = Extract(program);

        Assert.Single(plan.TableReads);
        Assert.Single(plan.Info.Buffers);
        Assert.False(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Memory[0].PlanningOnly);

        var memory = new TestWordMemory();
        memory.Words[1] = 0xDEADBEEF;
        var inputs = Inputs([0x1000, 0], readMemory: memory.Read);
        Assert.True(RuntimeValueEvaluator.EvaluateSources(plan, [plan.Info.Buffers[0].Source], inputs, [], evaluateTable: true, out var descriptors, out var table));
        Assert.Equal(0xDEADBEEFu, Assert.Single(descriptors).Dwords[0]);
        Assert.Equal([0xDEADBEEFu], table);
        Assert.Equal(1u, memory.Reads);

        memory.Reads = 0;
        memory.FailAfter = 0;
        var snapshot = new ResourceSnapshot { UserData = [1] };
        var specialization = new ResourceSpecialization { Buffers = [new BufferSpecialization(7, 0, 0)] };
        Assert.False(ResourceMaterializer.Materialize(plan, inputs, ref snapshot, ref specialization));
        Assert.Equal([1u], snapshot.UserData);
        Assert.Equal(7u, specialization.Buffers[0].PackedStride);

        var layout = BindingLayout.Allocate(plan.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64), false, plan.TableReads.Count != 0, false);
        Assert.NotNull(layout.Find(DescriptorBindingKind.FlattenedResourceTable));
    }

    [Fact]
    public void DynamicResourceTableReadRemainsExplicit()
    {
        var program = Program(
            ScalarLoad(4, 0, destination: 8, dynamicOffsetRegister: 2),
            MoveScalar(12, 9, 0),
            MoveScalar(16, 10, 64),
            MoveScalar(20, 11, 0),
            BufferLoad(24, 8),
            EndProgram(28));
        var plan = Extract(program);

        Assert.Empty(plan.TableReads);
        Assert.Single(plan.DynamicReads);
        Assert.True(plan.Info.UsesDeviceAddresses);
        var memory = new TestWordMemory();
        memory.Words[1] = 0xABCDEF01;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([0x1000, 0, 4], readMemory: memory.Read), out var descriptor));
        Assert.Equal(0xABCDEF01u, descriptor.Dwords[0]);
        Assert.Equal(1u, memory.Reads);

        var layout = BindingLayout.Allocate(plan.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64), false, plan.TableReads.Count != 0, false);
        Assert.Null(layout.Find(DescriptorBindingKind.FlattenedResourceTable));
        Assert.NotNull(layout.Find(DescriptorBindingKind.DeviceAddressPageTable));
        Assert.NotNull(layout.Find(DescriptorBindingKind.FaultBuffer));
        Assert.Equal((uint)layout.UserDataRegisters.Count, layout.MemoryOffsetDword);
        Assert.Equal(1u, layout.MemoryOffsetCount);
        Assert.Equal(layout.MemoryOffsetDword + 1, layout.ShaderDataDwordCount);
    }

    [Fact]
    public void PhiValidation()
    {
        var program = Program(
            Branch(0, "SCbranchScc0", 2),
            MoveScalar(4, 12, 1),
            Branch(8, "SBranch", 1),
            MoveScalar(12, 12, 2),
            Sop2(16, "SMinU32", 11, Gen5Operand.Scalar(12), Operand(0x100)),
            MoveScalar(20, 8, 0),
            MoveScalar(24, 9, 0),
            MoveScalar(28, 10, 0),
            BufferLoad(32, 8),
            EndProgram(36));

        var error = Assert.Throws<ResourcePlanException>(() => Extract(program));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    [Fact]
    public void LoopCycleEnteredThroughRuntimeValue()
    {
        var program = Program(
            Sop2(0, "SAndB32", 0, Gen5Operand.Scalar(0), Operand(0xFFFFFFFF)),
            Branch(4, "SCbranchScc1", -2),
            BufferLoad(8, 0),
            EndProgram(12));

        var graph = ScalarValueGraph.Build(program, 0, 4);
        var reads = ResourceTableReadPlanner.Plan(graph, ShaderStage.Compute, Hash);
        Assert.Empty(reads.Reads);
    }

    [Fact]
    public void InvariantLoopPhi()
    {
        var program = Program(
            Nop(0),
            Branch(4, "SCbranchScc1", -2),
            BufferLoad(8, 0),
            EndProgram(12));
        var plan = Extract(program);

        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs([0x12345678, 0, 0, 0]), out var descriptor));
        Assert.Equal(0x12345678u, descriptor.Dwords[0]);
    }

    [Fact]
    public void DeviceAddressMaterialization()
    {
        var program = Program(
            GlobalAccess(4, "GlobalLoadDword", 0, offset: -8),
            GlobalAccess(8, "FlatStoreDword", 0, vectorAddress: 2),
            EndProgram(16));
        var plan = Extract(program);

        Assert.True(plan.Info.UsesDeviceAddresses);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x2008, 0]), ref snapshot, ref specialization));
        ResourceMaterializer.ApplyTo(plan, specialization);
    }

    [Fact]
    public void DynamicFlatAddressesUseDeviceAddresses()
    {
        var program = Program(
            Vop1(0, "VMovB32", 10, Operand(0)),
            Vopc(4, "VCmpNeU32", Gen5Operand.Scalar(2), 10),
            Sop1(8, "SAndSaveexecB64", 6, Gen5Operand.Scalar(106)),
            Vop1(12, "VMovB32", 0, Gen5Operand.Scalar(0)),
            Vop1(16, "VMovB32", 1, Gen5Operand.Scalar(1)),
            GlobalAccess(0xA4, "FlatLoadUbyte", 0),
            EndProgram(0xAC));
        var plan = Extract(program);

        Assert.True(plan.Info.UsesDeviceAddresses);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x23456780, 1, 1]), ref snapshot, ref specialization));
    }

    [Fact]
    public void BufferSwizzleSpecialization()
    {
        var program = Program(BufferLoad(4, 0, formatted: true), EndProgram(12));
        var plan = Extract(program);
        const uint swizzle = 4 | (5 << 3) | (0 << 6) | (1 << 9);
        uint[] userData = [0, 16 << 16, 1, swizzle | (Format32x2Float << 12) | (1u << 24)];
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(swizzle, applied.Info.Buffers[0].DescriptorSwizzle);
        Assert.Equal(swizzle, specialization.Buffers[0].DescriptorSwizzle);

        userData[3] ^= 1u << 9;
        var changedSnapshot = new ResourceSnapshot();
        var changedSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref changedSnapshot, ref changedSpecialization));
        Assert.NotEqual(specialization, changedSpecialization);
    }

    [Fact]
    public void ResourceLimits_FailBeforeAnyTableIsWritten()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (uint index = 0; index <= ShaderResourceInfo.MaxBuffers; index++)
        {
            for (uint dword = 0; dword < 4; dword++)
            {
                instructions.Add(MoveScalar(pc, 8 + dword, index + dword + 0x100));
                pc += 8;
            }

            instructions.Add(BufferLoad(pc, 8));
            pc += 8;
        }

        instructions.Add(EndProgram(pc));
        var error = Assert.Throws<ResourcePlanException>(() => Extract(Program([.. instructions])));
        Assert.Contains("buffer resource limit exceeded", error.Message);
    }

    [Fact]
    public void GlobalDataShareAccess_AllocatesTheBinding()
    {
        var program = Program(DataShareWrite(0, gds: true), EndProgram(8));
        var plan = Extract(program);
        var layout = BindingLayout.Allocate(plan.Info, [], BindingLayout.UsesGlobalDataShare(program), false, false);

        Assert.NotNull(layout.Find(DescriptorBindingKind.GlobalDataShare));
        Assert.Null(BindingLayout.Allocate(plan.Info, [], BindingLayout.UsesGlobalDataShare(Program(DataShareWrite(0, gds: false), EndProgram(8))), false, false).Find(DescriptorBindingKind.GlobalDataShare));
    }
}

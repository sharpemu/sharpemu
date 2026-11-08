// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// A compute pass walks a table of records. Each record holds a bindless heap index; the loop body fetches the
// heap descriptor with S_BUFFER_LOAD and samples it. The material key is selected by the loop counter, which every
// lane shares, rather than by a lane value read through v_readfirstlane.
public sealed class LoopBindlessImageTests
{
    private const uint Format32x4Float = 77;
    private const uint ImageType3D = 10;
    private const uint IdentitySwizzle = 0xFAC;
    private const uint RecordStride = 224;
    private const uint Records = 8;
    private const uint KeyOffset = 4;
    private const uint ThreeDimensions = 2;

    // The smallest layout the materializer gives a loop-selected table: its own image plus this many candidates, and a
    // key mapping of 32 entries, which a binary search of six steps covers. More volumes take the next power of two.
    private const int MinCandidates = 8;
    private const uint MappingSearchIterations = 6;

    private static short Back(uint from, uint to) => (short)(((int)to - (int)from - 4) / 4);

    // The material table is s[0:3] and the heap s[4:7]. The descriptor s[16:23] is fetched in the loop body, or
    // before an inner loop that samples it; then its dwords reach the image as loop-invariant phis.
    private static Gen5ShaderProgram LoopProgram(bool descriptorBeforeInnerLoop, bool keyFromMaterialTable = true, bool storeResult = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction) { instructions.Add(instruction); pc += 8; }

        Add(MoveScalar(pc, 9, 0));
        var outer = pc;
        Add(Sop2(pc, "SMulI32", 10, Gen5Operand.Scalar(9), Operand(RecordStride)));
        Add(Sop2(pc, "SAddU32", 11, Gen5Operand.Scalar(10), Operand(KeyOffset)));
        if (keyFromMaterialTable)
        {
            Add(ScalarBufferLoad(pc, 0, destination: 12, dynamicOffsetRegister: 11));
        }
        else
        {
            Add(Sop2(pc, "SAddU32", 12, Gen5Operand.Scalar(11), Operand(1)));
        }

        Add(Sop2(pc, "SLshlB32", 13, Gen5Operand.Scalar(12), Operand(5)));
        Add(ScalarBufferLoad(pc, 4, destination: 16, count: 8, dynamicOffsetRegister: 13));
        for (uint index = 0; index < 4; index++)
        {
            Add(MoveScalar(pc, 24 + index, 0));
        }

        if (descriptorBeforeInnerLoop)
        {
            Add(MoveScalar(pc, 30, 0));
            var inner = pc;
            Add(Image(pc, "ImageSampleLz", 16, 24, dimension: ThreeDimensions, dmask: 1));
            Add(Sop2(pc, "SAddU32", 30, Gen5Operand.Scalar(30), Operand(1)));
            Add(Sopc(pc, "SCmpLgU32", Gen5Operand.Scalar(30), Operand(2)));
            Add(Branch(pc, "SCbranchScc1", Back(pc, inner)));
        }
        else
        {
            Add(Image(pc, "ImageSampleLz", 16, 24, dimension: ThreeDimensions, dmask: 1));
        }

        if (storeResult)
        {
            Add(BufferAccess(pc, "BufferStoreDword", 52, 0, 1, vectorData: 4));
        }

        Add(Sop2(pc, "SAddU32", 9, Gen5Operand.Scalar(9), Operand(1)));
        Add(Sopc(pc, "SCmpLgU32", Gen5Operand.Scalar(9), Operand(Records)));
        Add(Branch(pc, "SCbranchScc1", Back(pc, outer)));
        Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    // The same fetch with a selector that is one user data dword instead of a loop counter: the host can evaluate
    // that descriptor directly.
    private static Gen5ShaderProgram UserDataSelectorProgram()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction) { instructions.Add(instruction); pc += 8; }

        Add(Sop2(pc, "SMulI32", 10, Gen5Operand.Scalar(8), Operand(RecordStride)));
        Add(Sop2(pc, "SAddU32", 11, Gen5Operand.Scalar(10), Operand(KeyOffset)));
        Add(ScalarBufferLoad(pc, 0, destination: 12, dynamicOffsetRegister: 11));
        Add(Sop2(pc, "SLshlB32", 13, Gen5Operand.Scalar(12), Operand(5)));
        Add(ScalarBufferLoad(pc, 4, destination: 16, count: 8, dynamicOffsetRegister: 13));
        for (uint index = 0; index < 4; index++)
        {
            Add(MoveScalar(pc, 24 + index, 0));
        }

        Add(Image(pc, "ImageSampleLz", 16, 24, dimension: ThreeDimensions, dmask: 1));
        Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    private static uint[] Volume(uint address) =>
        [address, Format32x4Float << 20, 3 | (3 << 14), IdentitySwizzle | (ImageType3D << 28), 0, 0, 0, 0];

    // A pass that fetches one volume per image from every record: image k reads its key at dword k of the record.
    private static Gen5ShaderProgram ManyImagesProgram(uint images)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction) { instructions.Add(instruction); pc += 8; }

        Add(MoveScalar(pc, 9, 0));
        for (uint index = 0; index < 4; index++)
        {
            Add(MoveScalar(pc, 24 + index, 0));
        }

        var outer = pc;
        Add(Sop2(pc, "SMulI32", 10, Gen5Operand.Scalar(9), Operand(RecordStride)));
        for (uint image = 0; image < images; image++)
        {
            var descriptor = 32 + 8 * image;
            Add(Sop2(pc, "SAddU32", 11, Gen5Operand.Scalar(10), Operand(KeyOffset + 4 * image)));
            Add(ScalarBufferLoad(pc, 0, destination: 12, dynamicOffsetRegister: 11));
            Add(Sop2(pc, "SLshlB32", 13, Gen5Operand.Scalar(12), Operand(5)));
            Add(ScalarBufferLoad(pc, 4, destination: descriptor, count: 8, dynamicOffsetRegister: 13));
            Add(Image(pc, "ImageSampleLz", descriptor, 24, dimension: ThreeDimensions, dmask: 1));
        }

        Add(BufferAccess(pc, "BufferStoreDword", 28, 0, 1, vectorData: 4));
        Add(Sop2(pc, "SAddU32", 9, Gen5Operand.Scalar(9), Operand(1)));
        Add(Sopc(pc, "SCmpLgU32", Gen5Operand.Scalar(9), Operand(Records)));
        Add(Branch(pc, "SCbranchScc1", Back(pc, outer)));
        Add(EndProgram(pc));
        return Program([.. instructions]);
    }

    // `records` records that name `images` volumes each: record i names heap entry 1 + i + records * image for image k.
    // Every entry is a distinct volume unless sameVolume says they are all the same descriptor.
    private static (uint[] UserData, TestWordMemory Memory) ManyVolumes(uint records, uint images, bool sameVolume = false)
    {
        const uint heap = 0x8000;
        var userData = new uint[64];
        uint[] tables = [0x1000, RecordStride << 16, records, 0, heap, 32 << 16, records * images + 1, 0, 1];
        tables.CopyTo(userData, 0);
        userData[30] = 64;
        var memory = new TestWordMemory { Base = 0x1000, Words = new uint[0xA000 / 4], RequireAlignment = true };
        for (uint key = 1; key <= records * images; key++)
        {
            ResourceTrackerTests.WriteImage(memory, heap + key * 32, Volume(sameVolume ? 0x100 : 0x100 + key));
        }

        for (uint record = 0; record < records; record++)
        {
            for (uint image = 0; image < images; image++)
            {
                memory.At(0x1000 + record * RecordStride + KeyOffset + 4 * image) = 1 + record + records * image;
            }
        }

        return (userData, memory);
    }

    // Eight records whose keys are 1, 2, 1, 3, 0, 0, 0, 0; heap entry 0 stays empty.
    private static (uint[] UserData, TestWordMemory Memory) Inputs3D()
    {
        var userData = new uint[64];
        uint[] tables = [0x1000, RecordStride << 16, Records, 0, 0x2000, 32 << 16, 4, 0, 1];
        tables.CopyTo(userData, 0);
        userData[54] = 64;
        var memory = ResourceTrackerTests.LinearMemory();
        ResourceTrackerTests.WriteImage(memory, 0x2020, Volume(0x20));
        ResourceTrackerTests.WriteImage(memory, 0x2040, Volume(0x30));
        ResourceTrackerTests.WriteImage(memory, 0x2060, Volume(0x40));
        uint[] keys = [1, 2, 1, 3, 0, 0, 0, 0];
        for (uint record = 0; record < Records; record++)
        {
            memory.At(0x1000 + record * RecordStride + KeyOffset) = keys[record];
        }

        return (userData, memory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopCounterSelector_BecomesAnIndirectImage(bool descriptorBeforeInnerLoop)
    {
        var plan = Extract(LoopProgram(descriptorBeforeInnerLoop));

        Assert.Single(plan.Info.Images);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.NotNull(source.IndirectImage);
        Assert.Single(plan.IndirectImages);
        Assert.Equal(ScalarValueKind.ScalarBufferWord, plan.IndirectImages[0].Key.Kind);
        Assert.Equal(RecordStride, source.IndirectImage!.SelectorStride);
        Assert.Equal(KeyOffset, source.IndirectImage.SelectorOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopCounterSelector_MaterializesEveryHeapDescriptorTheTableNames(bool descriptorBeforeInnerLoop)
    {
        var plan = Extract(LoopProgram(descriptorBeforeInnerLoop));
        var (userData, memory) = Inputs3D();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        // The empty entry (key 0), the three volumes the records name and null padding.
        Assert.Equal(1 + MinCandidates, snapshot.Images.Length);
        foreach (var address in new uint[] { 0x20, 0x30, 0x40 })
        {
            Assert.Contains(snapshot.Images, image => image.SequenceEqual(Volume(address)));
        }

        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(1 + MinCandidates, applied.Info.Images.Count);
        Assert.All(applied.Info.Images, image => Assert.Equal(0u, image.IndirectRoot));
        Assert.Equal(MappingSearchIterations, applied.Info.Images[0].IndirectSearchIterations);
        // Key 0 and the three keys that read a volume: the keys that read the empty entry are left out.
        Assert.Equal(4u, snapshot.FlattenedResourceTable[(int)applied.Info.Images[0].IndirectMappingOffset]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopCounterSelector_CompilesWithCandidateSelection(bool descriptorBeforeInnerLoop)
    {
        var plan = Extract(LoopProgram(descriptorBeforeInnerLoop, storeResult: true));
        var (userData, memory) = Inputs3D();
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(1 + MinCandidates, resources.Info.Images.Count);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64),
            false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            false);

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out var shader, out var error), error);
        Assert.Contains((ushort)SpirvOp.Select, new SpirvModuleInspector(shader.Spirv).Opcodes);
    }

    [Fact]
    public void LoopCounterSelector_ReadsADescriptorOfAnotherKindAsNull()
    {
        var plan = Extract(LoopProgram(false));
        var (userData, memory) = Inputs3D();
        // The default heap entry is a plain 2D image: a 3D instruction cannot sample it.
        ResourceTrackerTests.WriteImage(memory, 0x2000, ResourceTrackerTests.ImageDescriptor());
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        Assert.Equal(1 + MinCandidates, snapshot.Images.Length);
        Assert.DoesNotContain(snapshot.Images, image => image.SequenceEqual(ResourceTrackerTests.ImageDescriptor()));
        foreach (var address in new uint[] { 0x20, 0x30, 0x40 })
        {
            Assert.Contains(snapshot.Images, image => image.SequenceEqual(Volume(address)));
        }

        Assert.All(ResourceMaterializer.ApplyTo(plan, specialization).Info.Images, image => Assert.Equal(ImageDimension.Dim3D, image.Dimension));
    }

    [Fact]
    public void LoopCounterSelector_ReadsAsNullWhenNoDescriptorCanBeSampled()
    {
        var plan = Extract(LoopProgram(false));
        var (userData, memory) = Inputs3D();
        foreach (var address in new ulong[] { 0x2000, 0x2020, 0x2040, 0x2060 })
        {
            var descriptor = ResourceTrackerTests.ImageDescriptor();
            descriptor[0] += (uint)address;
            ResourceTrackerTests.WriteImage(memory, address, descriptor);
        }

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        var image = Assert.Single(snapshot.Images);
        Assert.All(image, word => Assert.Equal(0u, word));
    }

    [Fact]
    public void LoopCounterSelector_ReadsAsANullImageWhenTheTableCannotBeRead()
    {
        var plan = Extract(LoopProgram(false));
        var (userData, memory) = Inputs3D();
        memory.FailAddress = 0x1000 + KeyOffset;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        var image = Assert.Single(snapshot.Images);
        Assert.All(image, word => Assert.Equal(0u, word));
    }

    [Fact]
    public void LoopCounterSelector_ReadsOnlyTheKeysTheRecordsHold()
    {
        var plan = Extract(LoopProgram(false));
        var (userData, memory) = Inputs3D();
        for (uint record = 0; record < Records; record++)
        {
            memory.At(0x1000 + record * RecordStride + KeyOffset) = record < 4 ? 1u : 0u;
            // The stride lattice reaches this dword, 32 bytes on, but the loop never reads it as a key.
            memory.At(0x1000 + record * RecordStride + KeyOffset + 32) = 2;
        }

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        Assert.Contains(snapshot.Images, image => image.SequenceEqual(Volume(0x20)));
        Assert.DoesNotContain(snapshot.Images, image => image.SequenceEqual(Volume(0x30)));
    }

    [Fact]
    public void LoopCounterSelector_KeepsEveryCandidateOfALargeTable()
    {
        var plan = Extract(ManyImagesProgram(1));
        var (userData, memory) = ManyVolumes(records: 70, images: 1);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        // Seventy volumes take 128 candidates, the next power of two.
        Assert.Equal(1 + 128, snapshot.Images.Length);
        Assert.Contains(snapshot.Images, image => image.SequenceEqual(Volume(0x100 + 1)));
        Assert.Contains(snapshot.Images, image => image.SequenceEqual(Volume(0x100 + 70)));
    }

    [Fact]
    public void LoopCounterSelector_KeepsEveryVolumeOfAFrameThatNamesMoreThanEight()
    {
        // A view whose volume list grows past eight records: a volume left out reads the first candidate, and its
        // light goes missing wherever that volume covers the view.
        var plan = Extract(ManyImagesProgram(1));
        var (userData, memory) = ManyVolumes(records: 15, images: 1);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        for (uint key = 1; key <= 15; key++)
        {
            Assert.Contains(snapshot.Images, image => image.SequenceEqual(Volume(0x100 + key)));
        }

        // Key 0 and every key the records hold.
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(16u, snapshot.FlattenedResourceTable[(int)applied.Info.Images[0].IndirectMappingOffset]);
    }

    [Fact]
    public void LoopCounterSelector_KeepsTheSameLayoutForVolumeCountsOfOneSizeClass()
    {
        var plan = Extract(ManyImagesProgram(1));
        var layouts = new List<(ResourceSpecialization Specialization, int Images, int Table)>();
        foreach (var records in new uint[] { 9, 12, 16 })
        {
            var (userData, memory) = ManyVolumes(records, 1);
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));
            layouts.Add((specialization, snapshot.Images.Length, snapshot.FlattenedResourceTable.Length));
        }

        // Another set of volumes of a similar count in every frame must not compile another pipeline.
        Assert.All(layouts, layout =>
        {
            Assert.Equal(layouts[0].Specialization, layout.Specialization);
            Assert.Equal(layouts[0].Images, layout.Images);
            Assert.Equal(layouts[0].Table, layout.Table);
        });
    }

    [Fact]
    public void LoopCounterSelector_KeepsTheLargestLayoutAProgramHasNeeded()
    {
        var plan = Extract(ManyImagesProgram(1));
        var layouts = new List<(ResourceSpecialization Specialization, int Images, int Table)>();
        foreach (var records in new uint[] { 40, 3, 12 })
        {
            var (userData, memory) = ManyVolumes(records, 1);
            var snapshot = new ResourceSnapshot();
            var specialization = new ResourceSpecialization();
            Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));
            layouts.Add((specialization, snapshot.Images.Length, snapshot.FlattenedResourceTable.Length));
        }

        // A view that names fewer volumes after one that named many must not compile another pipeline.
        Assert.Equal(1 + 64, layouts[0].Images);
        Assert.All(layouts, layout =>
        {
            Assert.Equal(layouts[0].Specialization, layout.Specialization);
            Assert.Equal(layouts[0].Images, layout.Images);
            Assert.Equal(layouts[0].Table, layout.Table);
        });
    }

    [Fact]
    public void LoopCounterSelector_KeepsEveryKeyWhenManyKeysReadTheSameVolume()
    {
        var plan = Extract(ManyImagesProgram(1));
        var (userData, memory) = ManyVolumes(records: 40, images: 1, sameVolume: true);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));

        // Key 0 and the 40 keys take a 64-entry mapping, which a binary search of seven steps covers.
        var applied = ResourceMaterializer.ApplyTo(plan, specialization);
        Assert.Equal(MappingSearchIterations + 1, applied.Info.Images[0].IndirectSearchIterations);
        Assert.Equal(41u, snapshot.FlattenedResourceTable[(int)applied.Info.Images[0].IndirectMappingOffset]);
    }

    [Fact]
    public void LoopCounterSelectors_AtTheImageLimitCompile()
    {
        const uint images = 8;
        var plan = Extract(ManyImagesProgram(images));
        var (userData, memory) = ManyVolumes(20, images);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData, readCleanMemory: memory.Read), ref snapshot, ref specialization));
        var resources = ResourceMaterializer.ApplyTo(plan, specialization);
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(plan.Graph.Program, 0, 64),
            false,
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            false);

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out _, out var error), error);
    }

    [Fact]
    public void UserDataSelector_KeepsTheDirectDescriptor()
    {
        var plan = Extract(UserDataSelectorProgram());

        Assert.Single(plan.Info.Images);
        Assert.Null(plan.DescriptorSources[(int)plan.Info.Images[0].Source].IndirectImage);
        Assert.Empty(plan.IndirectImages);
    }

    [Fact]
    public void LoopWithoutAMaterialKey_StaysANullImage()
    {
        var plan = Extract(LoopProgram(false, keyFromMaterialTable: false));

        Assert.Single(plan.Info.Images);
        var source = plan.DescriptorSources[(int)plan.Info.Images[0].Source];
        Assert.Null(source.IndirectImage);
        Assert.Empty(plan.IndirectImages);
        Assert.All(source.Dwords, dword => Assert.True(dword.IsConstant && dword.ConstantU32 == 0));
    }
}

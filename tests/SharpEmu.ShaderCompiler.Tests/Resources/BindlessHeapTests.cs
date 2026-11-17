// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class BindlessHeapTests
{
    // With the heap set (bindlessHeap), an image read from a heap V# at a lane-chosen offset is a heap record: its reads
    // stay on the host, and the shader takes the element of the record its key picks from descriptor set 1 and marks it
    // for the host. The program has one heap view, whose elements the draw names in two dwords of shader data.
    [Fact]
    public void AnImageReadAtALaneChosenOffsetIsAHeapRecord()
    {
        var program = ResourceTrackerTests.LaneIndexedImageProgram();

        var plan = Extract(program, stage: ShaderStage.Compute, bindlessHeap: true);

        var record = Assert.Single(plan.HeapRecords);
        Assert.False(record.Sampler);
        var heap = plan.DescriptorSources[(int)record.Source].Heap!;
        Assert.Equal((0u, BindlessHeap.ImageRecordBytes, false), (heap.Immediate, heap.RecordBytes, heap.Cube));
        Assert.Equal(40u, plan.Memory[heap.KeyMemoryIndex].Pc);
        Assert.Equal(new HeapRecordSource(heap.HeapSource, false), Assert.Single(plan.HeapRecordSources));
        for (var component = 0u; component < 8; component++)
        {
            Assert.True(plan.Memory.TryGetIndex(40, component, out var index));
            Assert.True(plan.Memory[index].PlanningOnly);
        }

        Assert.True(plan.Info.UsesBindlessHeap);
        Assert.Equal(1u, plan.Info.HeapViewCount);
        var request = Request(program, bindlessHeap: true);
        Assert.True(request.UsesBindlessHeap);
        Assert.True(request.Bindings.UsesBindlessHeap);
        Assert.Equal(1u, request.Bindings.HeapViewCount);
        Assert.Equal(request.Bindings.ShaderDataDwordCount, request.Bindings.HeapViewsDword + HeapView.DwordCount);
        Assert.Contains(heap.KeyMemoryIndex, request.IndirectOffsetKeyMemoryIndices);

        var module = Compile(request);
        Assert.Equal([0u, BindlessHeap.FeedbackBinding], module.SetOneBindings);
        Assert.Contains(SpirvOp.AtomicOr, module.Opcodes);
        Assert.Contains((uint)SpirvDecoration.NonUniform, module.Decorations);
        Assert.Contains((uint)SpirvCapability.SampledImageArrayNonUniformIndexing, module.Capabilities);
        Assert.Contains(SpirvOp.ULessThan, module.Opcodes);
        Assert.Contains(SpirvOp.UDiv, module.Opcodes);
        Assert.Contains(BindlessHeap.CallRecordNullIndex, module.Constants);
    }

    // A sampler read from a heap V# at a lane-chosen offset indexes the sampler array of set 1; its words still load.
    [Fact]
    public void ASamplerReadAtALaneChosenOffsetIsAHeapRecord()
    {
        var program = ResourceTrackerTests.LaneIndexedSamplerProgram();

        var plan = Extract(program, stage: ShaderStage.Pixel, bindlessHeap: true);

        var record = Assert.Single(plan.HeapRecords);
        Assert.True(record.Sampler);
        var heap = plan.DescriptorSources[(int)record.Source].Heap!;
        Assert.Equal((0u, BindlessHeap.SamplerRecordBytes), (heap.Immediate, heap.RecordBytes));
        Assert.True(plan.Memory.TryGetIndex(24, 0, out var index));
        Assert.False(plan.Memory[index].PlanningOnly);

        var module = Compile(Request(program, ShaderStage.Pixel, bindlessHeap: true));
        Assert.Equal([(uint)BindlessHeap.SamplerSlot, BindlessHeap.FeedbackBinding], module.SetOneBindings);
    }

    // With both opt-ins, a heap record is read from the heap and not as null.
    [Fact]
    public void AHeapRecordIsNotReadAsNull()
    {
        var plan = Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute,
            bindlessNullDescriptors: true, bindlessHeap: true);

        Assert.Single(plan.HeapRecords);
        Assert.Empty(plan.NullBindlessDescriptorPcs);
    }

    // Each draw evaluates the heap V# for the host, and the heap image takes its class from the instruction.
    [Fact]
    public void MaterializingReportsTheHeapAndSpecializesTheImageFromTheInstruction()
    {
        var plan = Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute, bindlessHeap: true);
        var userData = new uint[64];
        userData[0] = 0x0001_0000;
        userData[1] = 32u << 16;
        userData[2] = 4096;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));

        Assert.Equal([new BindlessHeapDescriptor(false, 0x0001_0000, 4096, 32)], snapshot.Heaps);
        var image = Assert.Single(specialization.Images);
        Assert.Equal((ImageNumericClass.Float, ImageDimension.Dim2D, false), (image.NumericClass, image.Dimension, image.Cube));
    }

    // Materialized the way the program cache chooses its path, a heap program still reports its heap: the fast path
    // assembles no heap descriptors, and with an empty heap the shader's records read nothing.
    [Fact]
    public void AHeapProgramKeepsItsHeapOnThePathTheCacheChooses()
    {
        var plan = Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute, bindlessHeap: true);
        var userData = new uint[64];
        userData[0] = 0x0001_0000;
        userData[1] = 32u << 16;
        userData[2] = 4096;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();

        var materialized = ResourceMaterializer.CanMaterializeFast(plan)
            ? ResourceMaterializer.MaterializeFast(plan, Inputs(userData), ref snapshot, ref specialization, out _)
            : ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization);

        Assert.True(materialized);
        Assert.Equal([new BindlessHeapDescriptor(false, 0x0001_0000, 4096, 32)], snapshot.Heaps);
    }

    // A 3D read of a heap record also has the record's 2D-array element: a record that holds a 2D texture has a null 3D
    // element, and the hardware takes the third coordinate as the layer.
    [Fact]
    public void AHeapVolumeReadFallsBackToTheRecordsLayers()
    {
        var module = Compile(Request(ResourceTrackerTests.LaneIndexedImageProgram(dimension: 2), bindlessHeap: true));

        Assert.Equal([(uint)BindlessHeap.VolumeLayerSlot, (uint)BindlessHeap.VolumeSlot, BindlessHeap.FeedbackBinding], module.SetOneBindings);
        Assert.Contains(SpirvOp.ImageQueryLevels, module.Opcodes);
        Assert.Contains((uint)SpirvCapability.ImageQuery, module.Capabilities);
    }

    // A heap read takes the element its draw's heap view has for the record it reads: the view's first element plus the
    // record number, when the read is at the view's descriptor in a record the view holds. Any other read (past the
    // view's records, between two descriptors, or of a view the draw gave no elements) takes the null element, so no read
    // takes the element of another view. A terrain or material table has records larger than a descriptor (stride 440
    // here, with the T# 64 bytes in); a heap of packed descriptors has the descriptor's size as its stride.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHeapReadTakesTheElementOfItsViewsRecord(bool sampler)
    {
        var program = sampler ? ResourceTrackerTests.LaneIndexedSamplerProgram() : ResourceTrackerTests.LaneIndexedImageProgram();
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, ShaderStage.Pixel, bindlessHeap: true), out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var module = new Expressions(shader.Spirv);
        var descriptorBytes = sampler ? BindlessHeap.SamplerRecordBytes : BindlessHeap.ImageRecordBytes;
        var indexes = module.ElementIndexes(Assert.Single(module.Arrays(sampler ? (uint)BindlessHeap.SamplerSlot : 0u)).Array);
        Assert.NotEmpty(indexes);
        var table = new HeapView(First: 1000, Stride: 440, FieldOffset: 64, Records: 49);
        var packed = new HeapView(First: 7, Stride: descriptorBytes, FieldOffset: 0, Records: 4096);
        foreach (var index in indexes)
        {
            var (offset, word0, word1) = module.HeapViewInputs(index);
            var values = new Dictionary<uint, uint>();
            if (!sampler)
            {
                // Every element holds a float view.
                values[module.HeapClassInput(index)] = 0;
            }

            uint Element(HeapView view, uint byteOffset) =>
                module.Evaluate(index, new Dictionary<uint, uint>(values) { [offset] = byteOffset, [word0] = view.Word0, [word1] = view.Word1 });

            Assert.Equal(1000u, Element(table, 64));
            Assert.Equal(1003u, Element(table, 3 * 440 + 64));
            Assert.Equal(1048u, Element(table, 48 * 440 + 64));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(table, 49 * 440 + 64));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(table, 3 * 440));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(table, 3 * 440 + 96));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(table, uint.MaxValue));

            Assert.Equal(7u, Element(packed, 0));
            Assert.Equal(7u + 1234, Element(packed, 1234 * descriptorBytes));
            Assert.Equal(7u + 4095, Element(packed, 4095 * descriptorBytes));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(packed, 4096 * descriptorBytes));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(packed, 1234 * descriptorBytes + 4));

            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(HeapView.None, 0));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(HeapView.None, 5 * descriptorBytes));
        }
    }

    // A table of records that hold more than a descriptor, read at a record the shader picks (a terrain
    // pass reads each tile's height and mask textures from a table of 440-byte tile records): each descriptor of the
    // record is a heap view of the program, and each draw evaluates the table V# for both, with the offset of the view's
    // descriptor in a record.
    [Fact]
    public void EachDescriptorOfATablesRecordsIsAView()
    {
        var program = Program(
            // The table V# in s16..s19 and the sampler in s24..s27 come from user data.
            MoveScalarRegister(0, 16, 0),
            MoveScalarRegister(4, 17, 1),
            MoveScalarRegister(8, 18, 2),
            MoveScalarRegister(12, 19, 3),
            MoveScalarRegister(16, 24, 4),
            MoveScalarRegister(20, 25, 5),
            MoveScalarRegister(24, 26, 6),
            MoveScalarRegister(28, 27, 7),
            // s106 = readfirstlane(v7) * 440: the byte offset of the record the first lane asks for.
            ReadFirstLane(32, 106, 7),
            Sop2(36, "SMulI32", 106, Gen5Operand.Scalar(106), new Gen5Operand(Gen5OperandKind.LiteralConstant, 440)),
            ScalarBufferLoad(44, 16, destination: 0, count: 8, dynamicOffsetRegister: 106),
            ScalarBufferLoad(52, 16, destination: 8, count: 8, immediateOffset: 64, dynamicOffsetRegister: 106),
            Image(60, "ImageSample", 0, 24),
            Image(68, "ImageSample", 8, 24),
            EndProgram(76));

        var plan = Extract(program, stage: ShaderStage.Compute, bindlessHeap: true);

        Assert.Equal(2, plan.HeapRecords.Count);
        Assert.Equal(2, plan.HeapRecordSources.Count);
        Assert.Equal(plan.HeapRecordSources[0].Source, plan.HeapRecordSources[1].Source);
        Assert.Equal([0u, 64u], plan.HeapRecordSources.Select(view => view.Immediate));
        Assert.Equal([0, 1], plan.HeapRecords.Select(plan.HeapViewOf));
        Assert.Equal(2u, plan.Info.HeapViewCount);
        var request = Request(program, bindlessHeap: true);
        Assert.Equal(2u, request.Bindings.HeapViewCount);
        Assert.Equal([0, 1], request.HeapImageByMemoryIndex.Values.Select(use => use.View).Order());
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);

        var userData = new uint[64];
        userData[0] = 0x0001_0000;
        userData[1] = 440u << 16;
        userData[2] = 49;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        Assert.Equal([new BindlessHeapDescriptor(false, 0x0001_0000, 49, 440), new BindlessHeapDescriptor(false, 0x0001_0000, 49, 440, 64)], snapshot.Heaps);
    }

    // Where a view's descriptors are in guest memory. The records of a table are its elements: the T# 64 bytes into
    // record 3 of a table of 440-byte records is at the heap's base + 3 * 440 + 64, whether the shader keeps 64 or a
    // whole record more in its immediate. A heap whose stride is the descriptor's size or less is packed descriptors.
    [Fact]
    public void AViewsRecordsAreItsHeapsRecords()
    {
        var table = new BindlessHeapDescriptor(false, 0x0001_0000, 49, 440, 64);
        var view = HeapView.Of(table, first: 1000, records: 49);
        Assert.Equal(new HeapView(1000, 440, 64, 49), view);
        Assert.Equal(0x0001_0000ul + 3 * 440 + 64, view.DescriptorAddress(table, 3));
        Assert.Equal(view, HeapView.Of(table with { Immediate = 440 + 64 }, first: 1000, records: 49));

        var packed = new BindlessHeapDescriptor(false, 0x0002_0000, 4096, 32, 32);
        Assert.Equal(new HeapView(0, 32, 0, 4096), HeapView.Of(packed, first: 0, records: 4096));
        Assert.Equal(0x0002_0000ul + 5 * 32, HeapView.Of(packed, 0, 4096).DescriptorAddress(packed, 5));
        var samplers = new BindlessHeapDescriptor(true, 0x0003_0000, 64, 4);
        Assert.Equal(new HeapView(0, BindlessHeap.SamplerRecordBytes, 0, 64), HeapView.Of(samplers, first: 0, records: 64));

        var words = new HeapView(1000, 440, 64, 49);
        Assert.Equal((1000u | (440u << 18), 49u | (64u << 18)), (words.Word0, words.Word1));
        var last = new HeapView(BindlessHeap.CallRecordFirstIndex - 1, 0x3FFF, 0x3FFF, BindlessHeap.CallRecordFirstIndex);
        Assert.Equal((last.First, last.Stride, last.Records, last.FieldOffset),
            (last.Word0 & HeapView.ElementMask, last.Word0 >> HeapView.ElementBits, last.Word1 & HeapView.ElementMask, last.Word1 >> HeapView.ElementBits));
    }

    // A T# with RESOURCE_LEVEL set (word 2 bit 31, a GFX10 field) is a texture like any other: terrain
    // tile records hold their R16 height textures that way, and the heap gave them no view, so the terrain cull sampled no
    // height. A freed record (0xFEFEFEFE words) still has reserved bits set.
    [Fact]
    public void AHeapRecordWithItsResourceLevelSetIsAnImage()
    {
        uint[] height = [0x80689630, 0x00700000, 0x80800080, 0x9050024C, 0, 0, 0, 0];
        uint[] levelClear = [0x80689630, 0x00700000, 0x00800080, 0x9050024C, 0, 0, 0, 0];
        uint[] freed = [0xFEFEFEFE, 0xFEFEFEFE, 0xFEFEFEFE, 0xFEFEFEFE, 0xFEFEFEFE, 0xFEFEFEFE, 0xFEFEFEFE, 0xFEFEFEFE];

        Assert.Equal(ImageNumericClass.Float, ResourceMaterializer.HeapImageNumericClass(height));
        Assert.Equal(ImageNumericClass.Float, ResourceMaterializer.HeapImageNumericClass(levelClear));
        Assert.Equal(ImageNumericClass.Unsupported, ResourceMaterializer.HeapImageNumericClass(freed));
        Assert.Equal(ImageNumericClass.Unsupported,
            ResourceMaterializer.HeapImageNumericClass([0x80689630, 0x00700000, 0xC0800080, 0x9050024C, 0, 0, 0, 0]));
    }

    // A record can hold an integer texture: terrain tile records can hold an 8-bit unsigned 513x513 mask
    // 96 bytes in, next to the R16 height, and its foliage passes read an 8-bit signed 128x128 map. The heap gave such
    // a record no view (its arrays were float), so those reads were null. The record's format names the numeric class
    // of its view; a format the shader would have to convert has none.
    [Fact]
    public void AHeapRecordOfAnIntegerTextureHasItsNumericClass()
    {
        uint[] mask = [0x80813360, 0x00500000, 0x80800080, 0x90500924, 0, 0, 0, 0];
        uint[] signed = [0x80813360, 0xC0600000, 0x001FC01F, 0x90500924, 0, 0, 0, 0];
        uint[] scaled = [0x80813360, 0x00300000, 0x80800080, 0x90500924, 0, 0, 0, 0];

        Assert.Equal(ImageNumericClass.Uint, ResourceMaterializer.HeapImageNumericClass(mask));
        Assert.Equal(ImageNumericClass.Sint, ResourceMaterializer.HeapImageNumericClass(signed));
        Assert.Equal(ImageNumericClass.Unsupported, ResourceMaterializer.HeapImageNumericClass(scaled));
        Assert.Equal(1u, BindlessHeap.ClassCode(ImageNumericClass.Uint));
        Assert.Equal(2u, BindlessHeap.ClassCode(ImageNumericClass.Sint));
        Assert.Equal(0u, BindlessHeap.ClassCode(ImageNumericClass.Float));
        // Loads read integer textures; the cube and depth-compare classes are sampled.
        Assert.Equal([true, true, true, true, false, false, false, false],
            Enumerable.Range(0, BindlessHeap.ImageClasses.Length).Select(BindlessHeap.HoldsIntegers).ToArray());
        Assert.Equal(BindlessHeap.Capacity * BindlessHeap.ClassBits, BindlessHeap.ClassWords * 32);
        Assert.Equal(BindlessHeap.ClassesPerWord, 1u << (int)BindlessHeap.ClassWordShift);
    }

    // An R128 image instruction reads a 16-byte T#: the words after it in the heap are another field of the record.
    // Large material records can hold their textures as 16-byte T#s one after another (at offsets such as 468, 484 and
    // 500 bytes in), so the 32 bytes at each were a T# followed by the next one, whose words set reserved bits of the
    // first: every such record was refused. The view of an R128 read says so, and its descriptors are 16 bytes.
    [Fact]
    public void AnR128ImageReadFromAHeapIsASixteenByteDescriptor()
    {
        var plan = Extract(ResourceTrackerTests.LaneIndexedImageProgram(r128: true), stage: ShaderStage.Compute, bindlessHeap: true);

        var record = Assert.Single(plan.HeapRecords);
        Assert.True(plan.DescriptorSources[(int)record.Source].Heap!.R128);
        Assert.True(Assert.Single(plan.HeapRecordSources).R128);
        Assert.False(Assert.Single(Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute, bindlessHeap: true)
            .HeapRecordSources).R128);

        var userData = new uint[64];
        userData[0] = 0x0001_0000;
        userData[1] = 1016u << 16;
        userData[2] = 257;
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(userData), ref snapshot, ref specialization));
        var heap = Assert.Single(snapshot.Heaps);
        Assert.Equal(new BindlessHeapDescriptor(false, 0x0001_0000, 257, 1016, 0, R128: true), heap);
        Assert.Equal(BindlessHeap.R128RecordBytes, heap.DescriptorBytes);
        Assert.Equal(BindlessHeap.ImageRecordBytes, (heap with { R128 = false }).DescriptorBytes);
        Assert.Equal(BindlessHeap.SamplerRecordBytes, (heap with { Sampler = true, R128 = false }).DescriptorBytes);
        // A heap of packed 16-byte T#s has one element for each.
        Assert.Equal(new HeapView(5, 16, 0, 100), HeapView.Of(new BindlessHeapDescriptor(false, 0x1000, 100, 16, 0, R128: true), 5, 100));
        Assert.Equal(new HeapView(5, 32, 0, 100), HeapView.Of(new BindlessHeapDescriptor(false, 0x1000, 100, 16), 5, 100));

        // Two of the material record's T#s, as the host reads them: 32 bytes at the first, and the first one's four words.
        uint[] overlapped = [0xB3396E00, 0xCB500000, 0x003FC03F, 0x90580FAC, 0x900006E0, 0x0A900000, 0x00000000, 0x90500FAC];
        uint[] extended = [0xB3396E00, 0xCB500000, 0x003FC03F, 0x90580FAC, 0, 0, 0, 0];
        Assert.Equal(ImageNumericClass.Unsupported, ResourceMaterializer.HeapImageNumericClass(overlapped));
        Assert.Equal(ImageNumericClass.Float, ResourceMaterializer.HeapImageNumericClass(extended, r128: true));
    }

    // A load of a heap record reads the texel's bits whatever the texture's format, so it has a float, an unsigned and a
    // signed declaration of its array, all at the array's binding. Each reads the record's element only when the host
    // says the element holds a view of its numeric class (the class words after the mark bitsets), and the null element
    // otherwise: no declaration reads a view of another class.
    [Theory]
    [InlineData("ImageLoad")]
    [InlineData("ImageLoadMip")]
    [InlineData("ImageGetResinfo")]
    public void AHeapLoadReadsAnIntegerRecordThroughTheIntegerDeclarationsOfItsArray(string opcode)
    {
        var program = ResourceTrackerTests.LaneIndexedImageProgram(opcode: opcode);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, ShaderStage.Pixel, bindlessHeap: true), out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var module = new Expressions(shader.Spirv);

        var arrays = module.Arrays(0);
        Assert.Equal(["float", "int", "uint"], arrays.Select(array => array.SampledType).Order().ToArray());
        var view = new HeapView(First: 1000, Stride: 440, FieldOffset: 96, Records: 31);
        foreach (var (array, sampledType) in arrays)
        {
            var code = sampledType switch { "uint" => 1u, "int" => 2u, _ => 0u };
            var indexes = module.ElementIndexes(array);
            Assert.NotEmpty(indexes);
            foreach (var index in indexes)
            {
                var (offset, word0, word1) = module.HeapViewInputs(index);
                var classes = module.HeapClassInput(index);
                // Record 5 is element 1005: element 13 of its class word (1005 % 16), two bits each.
                uint Element(uint elementClass) => module.Evaluate(index, new Dictionary<uint, uint>
                {
                    [offset] = 5 * 440 + 96, [word0] = view.Word0, [word1] = view.Word1, [classes] = 0xF3FFFFFF | (elementClass << 26),
                });
                for (var elementClass = 0u; elementClass < 4; elementClass++)
                {
                    Assert.Equal(elementClass == code ? 1005u : BindlessHeap.CallRecordNullIndex, Element(elementClass));
                }
            }
        }

        Assert.Equal(BindlessHeap.MarkWords + (1005u >> (int)BindlessHeap.ClassWordShift), module.HeapClassWord(module.ElementIndexes(arrays[0].Array)[0]));
    }

    // A sampled read has the float declaration alone, and takes the null element of a record that holds an integer view.
    [Fact]
    public void AHeapSampleDoesNotReadAnIntegerRecord()
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(
            Request(ResourceTrackerTests.LaneIndexedImageProgram(), ShaderStage.Pixel, bindlessHeap: true), out var shader, out var error), error);
        var module = new Expressions(shader.Spirv);

        var (array, sampledType) = Assert.Single(module.Arrays(0));
        Assert.Equal("float", sampledType);
        var view = new HeapView(First: 7, Stride: BindlessHeap.ImageRecordBytes, FieldOffset: 0, Records: 4096);
        foreach (var index in module.ElementIndexes(array))
        {
            var (offset, word0, word1) = module.HeapViewInputs(index);
            var classes = module.HeapClassInput(index);
            uint Element(uint classWord) => module.Evaluate(index, new Dictionary<uint, uint>
            {
                [offset] = 2 * BindlessHeap.ImageRecordBytes, [word0] = view.Word0, [word1] = view.Word1, [classes] = classWord,
            });
            // Record 2 is element 9: bits 18 and 19 of its class word.
            Assert.Equal(9u, Element(0));
            Assert.Equal(9u, Element(~(3u << 18)));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(1u << 18));
            Assert.Equal(BindlessHeap.CallRecordNullIndex, Element(2u << 18));
        }
    }

    // Without the heap set nothing changes: compute still rejects the lane-chosen image.
    [Fact]
    public void WithoutTheOptInTheImageIsStillRejected()
    {
        var error = Assert.Throws<ResourcePlanException>(() =>
            Extract(ResourceTrackerTests.LaneIndexedImageProgram(), stage: ShaderStage.Compute));
        Assert.Contains("not a valid runtime value", error.Message);
    }

    private sealed record Module(List<SpirvOp> Opcodes, List<uint> Decorations, List<uint> Capabilities, List<uint> SetOneBindings,
        List<uint> Constants);

    private static Module Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        var code = shader.Spirv;
        var opcodes = new List<SpirvOp>();
        var decorations = new List<uint>();
        var capabilities = new List<uint>();
        var constants = new List<uint>();
        var sets = new Dictionary<uint, uint>();
        var bindings = new Dictionary<uint, uint>();
        for (var offset = 20; offset + 4 <= code.Length;)
        {
            var word = BitConverter.ToUInt32(code, offset);
            var count = (int)(word >> 16);
            var opcode = (SpirvOp)(word & 0xFFFF);
            uint Operand(int index) => BitConverter.ToUInt32(code, offset + index * sizeof(uint));
            opcodes.Add(opcode);
            if (opcode == SpirvOp.Capability)
            {
                capabilities.Add(Operand(1));
            }
            else if (opcode == SpirvOp.Constant && count == 4)
            {
                constants.Add(Operand(3));
            }
            else if (opcode == SpirvOp.Decorate)
            {
                decorations.Add(Operand(2));
                if (Operand(2) == (uint)SpirvDecoration.DescriptorSet)
                {
                    sets[Operand(1)] = Operand(3);
                }
                else if (Operand(2) == (uint)SpirvDecoration.Binding)
                {
                    bindings[Operand(1)] = Operand(3);
                }
            }

            offset += count * sizeof(uint);
        }

        var setOne = sets.Where(pair => pair.Value == BindlessHeap.DescriptorSet).Select(pair => bindings[pair.Key]).Order().ToList();
        return new Module(opcodes, decorations, capabilities, setOne, constants);
    }

    // The integer value instructions of a module, enough to evaluate a set-1 element index for a given byte offset.
    private sealed class Expressions
    {
        private readonly Dictionary<uint, (SpirvOp Opcode, uint[] Operands)> _definitions = [];
        private readonly Dictionary<uint, uint> _constants = [];
        private readonly Dictionary<uint, (uint? Set, uint? Binding)> _bindings = [];
        private readonly List<(uint Base, uint Index)> _elementPointers = [];
        private readonly Dictionary<uint, (SpirvOp Opcode, uint[] Operands)> _types = [];
        private readonly Dictionary<uint, uint> _variableTypes = [];
        private readonly Dictionary<uint, uint> _loads = [];

        public Expressions(byte[] code)
        {
            for (var offset = 20; offset + 4 <= code.Length;)
            {
                var word = BitConverter.ToUInt32(code, offset);
                var count = (int)(word >> 16);
                var opcode = (SpirvOp)(word & 0xFFFF);
                var operands = new uint[count - 1];
                for (var index = 0; index < operands.Length; index++)
                {
                    operands[index] = BitConverter.ToUInt32(code, offset + (index + 1) * sizeof(uint));
                }

                switch (opcode)
                {
                    case SpirvOp.Constant when operands.Length == 3:
                        _constants[operands[1]] = operands[2];
                        break;
                    case SpirvOp.Decorate when operands.Length == 3 && operands[1] == (uint)SpirvDecoration.DescriptorSet:
                        _bindings[operands[0]] = (operands[2], _bindings.GetValueOrDefault(operands[0]).Binding);
                        break;
                    case SpirvOp.Decorate when operands.Length == 3 && operands[1] == (uint)SpirvDecoration.Binding:
                        _bindings[operands[0]] = (_bindings.GetValueOrDefault(operands[0]).Set, operands[2]);
                        break;
                    case SpirvOp.AccessChain when operands.Length >= 4:
                        _elementPointers.Add((operands[2], operands[3]));
                        _definitions[operands[1]] = (opcode, operands[2..]);
                        break;
                    case SpirvOp.TypeFloat or SpirvOp.TypeInt or SpirvOp.TypeImage or SpirvOp.TypeRuntimeArray or SpirvOp.TypePointer:
                        _types[operands[0]] = (opcode, operands[1..]);
                        break;
                    case SpirvOp.Variable:
                        _variableTypes[operands[1]] = operands[0];
                        break;
                    case SpirvOp.Load:
                        _loads[operands[1]] = operands[2];
                        break;
                    case SpirvOp.IAdd or SpirvOp.ISub or SpirvOp.IMul or SpirvOp.UDiv or SpirvOp.BitwiseAnd or SpirvOp.BitwiseOr or
                        SpirvOp.ShiftLeftLogical or SpirvOp.ShiftRightLogical or SpirvOp.ULessThan or SpirvOp.IEqual or SpirvOp.LogicalAnd or
                        SpirvOp.Select or SpirvOp.CopyObject or SpirvOp.Bitcast:
                        _definitions[operands[1]] = (opcode, operands[2..]);
                        break;
                }

                offset += count * sizeof(uint);
            }
        }

        // The declarations of the set-1 array at binding slot, each with the sampled type of its images ("sampler" for
        // a sampler array).
        public List<(uint Array, string SampledType)> Arrays(uint slot) =>
            _bindings.Where(entry => entry.Value.Set == BindlessHeap.DescriptorSet && entry.Value.Binding == slot)
                .Select(entry => (entry.Key, SampledType(entry.Key))).ToList();

        private string SampledType(uint array)
        {
            if (!_types.TryGetValue(_types[_types[_variableTypes[array]].Operands[1]].Operands[0], out var element) ||
                element.Opcode != SpirvOp.TypeImage)
            {
                return "sampler";
            }

            var sampled = _types[element.Operands[0]];
            return sampled.Opcode == SpirvOp.TypeFloat ? "float" : sampled.Operands[1] != 0 ? "int" : "uint";
        }

        // The indexes of the element pointers into a set-1 array.
        public List<uint> ElementIndexes(uint array) =>
            _elementPointers.Where(pointer => pointer.Base == array).Select(pointer => pointer.Index).ToList();

        // The load of the class word an image element index is chosen by: the one value its computation shifts that is
        // read from the feedback buffer.
        public uint HeapClassInput(uint index) =>
            Assert.Single(Reachable(index).Select(id => _definitions[id]).Where(definition =>
                definition.Opcode == SpirvOp.ShiftRightLogical && _loads.TryGetValue(definition.Operands[0], out var pointer) &&
                _definitions.TryGetValue(pointer, out var chain) && chain.Opcode == SpirvOp.AccessChain &&
                _bindings.GetValueOrDefault(chain.Operands[0]) == (BindlessHeap.DescriptorSet, BindlessHeap.FeedbackBinding))
                .Select(definition => definition.Operands[0]).Distinct());

        // The feedback word that load reads, for the element the view inputs of the tests above give (1005).
        public uint HeapClassWord(uint index)
        {
            var pointer = _definitions[_loads[HeapClassInput(index)]];
            Assert.Equal(SpirvOp.AccessChain, pointer.Opcode);
            var (offset, word0, word1) = HeapViewInputs(index);
            var view = new HeapView(First: 1000, Stride: 440, FieldOffset: 96, Records: 31);
            return Evaluate(pointer.Operands[^1], new Dictionary<uint, uint> { [offset] = 5 * 440 + 96, [word0] = view.Word0, [word1] = view.Word1 });
        }

        // What a heap element index is computed from: the read's byte offset (the dividend of the division by the view's
        // stride) and the two shader data dwords of its heap view (the stride and the record count are fields of them).
        public (uint Offset, uint Word0, uint Word1) HeapViewInputs(uint index)
        {
            var division = Assert.Single(Reachable(index).Where(id => _definitions[id].Opcode == SpirvOp.UDiv).Distinct());
            var (_, operands) = _definitions[division];
            var stride = _definitions[operands[1]];
            Assert.Equal(SpirvOp.ShiftRightLogical, stride.Opcode);
            var bound = Assert.Single(Reachable(index).Where(id => _definitions[id].Opcode == SpirvOp.ULessThan).Distinct());
            var records = _definitions[_definitions[bound].Operands[1]];
            Assert.Equal(SpirvOp.BitwiseAnd, records.Opcode);
            return (operands[0], stride.Operands[0], records.Operands[0]);
        }

        private IEnumerable<uint> Reachable(uint value)
        {
            var pending = new Queue<uint>();
            pending.Enqueue(value);
            var seen = new HashSet<uint>();
            while (pending.TryDequeue(out var id))
            {
                if (!seen.Add(id) || !_definitions.TryGetValue(id, out var definition))
                {
                    continue;
                }

                yield return id;
                foreach (var operand in definition.Operands)
                {
                    pending.Enqueue(operand);
                }
            }
        }

        public SpirvOp Opcode(uint id) => _definitions.TryGetValue(id, out var definition) ? definition.Opcode : SpirvOp.Nop;

        // The value of id when the named values hold these.
        public uint Evaluate(uint id, IReadOnlyDictionary<uint, uint> values)
        {
            if (values.TryGetValue(id, out var value))
            {
                return value;
            }

            if (_constants.TryGetValue(id, out var constant))
            {
                return constant;
            }

            var (opcode, operands) = _definitions[id];
            uint Operand(int index) => Evaluate(operands[index], values);
            return opcode switch
            {
                SpirvOp.IAdd => Operand(0) + Operand(1),
                SpirvOp.ISub => Operand(0) - Operand(1),
                SpirvOp.IMul => Operand(0) * Operand(1),
                SpirvOp.UDiv => Operand(0) / Operand(1),
                SpirvOp.BitwiseAnd => Operand(0) & Operand(1),
                SpirvOp.BitwiseOr => Operand(0) | Operand(1),
                SpirvOp.ShiftLeftLogical => Operand(0) << (int)(Operand(1) & 31),
                SpirvOp.ShiftRightLogical => Operand(0) >> (int)(Operand(1) & 31),
                SpirvOp.ULessThan => Operand(0) < Operand(1) ? 1u : 0u,
                SpirvOp.IEqual => Operand(0) == Operand(1) ? 1u : 0u,
                SpirvOp.LogicalAnd => Operand(0) & Operand(1),
                SpirvOp.Select => Operand(0) != 0 ? Operand(1) : Operand(2),
                _ => Operand(0),
            };
        }
    }
}
